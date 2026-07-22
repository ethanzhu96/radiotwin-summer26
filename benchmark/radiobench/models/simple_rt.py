from pathlib import Path
from dataclasses import dataclass
import warnings

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
import torch

from .base import RadioModel


@dataclass(frozen=True)
class PropagationPath:
    path_type: str
    total_length_m: float
    bounce_count: int
    diffraction_count: int
    points: np.ndarray
    plane_ids: tuple[int, ...] = ()
    edge_id: int | None = None


class SimpleRTModel(RadioModel):
    name = "Simple RT (LOS-only)"

    def __init__(self, config: dict | None = None) -> None:
        cfg = config or {}
        exponent = cfg.get("path_loss_exponent", {})
        self.d0_m = float(cfg.get("d0_m", 1.0))
        self.min_distance_m = float(cfg.get("min_distance_m", 0.1))
        self.n_min = float(exponent.get("min", 0.5))
        self.n_max = float(exponent.get("max", 6.0))
        self.grid_points = int(exponent.get("grid_points", 200))
        if self.d0_m <= 0 or self.min_distance_m <= 0:
            raise ValueError("Simple RT d0_m and min_distance_m must be positive.")
        if self.n_min <= 0 or self.n_max <= self.n_min or self.grid_points < 2:
            raise ValueError("Simple RT path-loss exponent search configuration is invalid.")
        self.mesh = None
        self.tx: np.ndarray | None = None
        self.path_loss_exponent: float | None = None
        self.tx_offset_dbm: float | None = None
        self.fit_los_count = 0
        self.fit_blocked_count = 0

    @staticmethod
    def _positions(values, label: str) -> np.ndarray:
        points = np.asarray(values, dtype=float)
        if points.ndim != 2 or points.shape[1] != 3 or points.shape[0] == 0:
            raise ValueError(f"{label} must have non-empty shape [N, 3].")
        if not np.all(np.isfinite(points)):
            raise ValueError(f"{label} contains NaN or infinite values.")
        return points

    def _set_context(self, ctx: dict | None) -> None:
        if not ctx or ctx.get("mesh") is None or ctx.get("tx") is None:
            raise ValueError("Simple RT fit requires ctx with 'mesh' and 'tx'.")
        tx = np.asarray(ctx["tx"], dtype=float)
        if tx.shape != (3,) or not np.all(np.isfinite(tx)):
            raise ValueError("Simple RT TX must contain three finite coordinates.")
        self.mesh = ctx["mesh"]
        self.tx = tx

    def los_mask(self, positions) -> np.ndarray:
        if self.mesh is None or self.tx is None:
            raise RuntimeError("Simple RT model has no mesh/TX context; call fit first.")
        points = self._positions(positions, "positions")
        return ~self.mesh.blocked_mask(self.tx, points)

    def distances(self, positions) -> np.ndarray:
        if self.tx is None:
            raise RuntimeError("Simple RT model has no TX context; call fit first.")
        points = self._positions(positions, "positions")
        return np.linalg.norm(points - self.tx, axis=1)

    def fit(self, train_pos, train_rssi, ctx=None):
        points = self._positions(train_pos, "train_pos")
        rssi = np.asarray(train_rssi, dtype=float)
        if rssi.shape != (len(points),) or not np.all(np.isfinite(rssi)):
            raise ValueError("train_rssi must be finite and match train_pos rows.")
        self._set_context(ctx)
        los = self.los_mask(points)
        self.fit_los_count = int(los.sum())
        self.fit_blocked_count = int((~los).sum())
        if self.fit_los_count < 2:
            raise ValueError(
                "Simple RT needs at least two LOS training samples for calibration; "
                f"found {self.fit_los_count}."
            )
        distances = np.maximum(self.distances(points)[los], self.min_distance_m)
        observed = rssi[los]
        best = None
        for exponent in np.linspace(self.n_min, self.n_max, self.grid_points):
            path_term = -10.0 * exponent * np.log10(distances / self.d0_m)
            offset = float(np.mean(observed - path_term))
            rmse = float(np.sqrt(np.mean((offset + path_term - observed) ** 2)))
            if best is None or rmse < best[0]:
                best = (rmse, float(exponent), offset)
        _, self.path_loss_exponent, self.tx_offset_dbm = best
        grid_step = (self.n_max - self.n_min) / (self.grid_points - 1)
        if (
            self.path_loss_exponent <= self.n_min + grid_step * 0.5
            or self.path_loss_exponent >= self.n_max - grid_step * 0.5
        ):
            warnings.warn(
                f"Fitted n={self.path_loss_exponent:.3f} is at/near the configured "
                "search boundary; geometry alignment or LOS-only adequacy may be poor.",
                stacklevel=2,
            )
        return self

    def predict(self, positions) -> np.ndarray:
        if self.path_loss_exponent is None or self.tx_offset_dbm is None:
            raise RuntimeError("Simple RT model must be fit before prediction.")
        points = self._positions(positions, "positions")
        distances = np.maximum(self.distances(points), self.min_distance_m)
        predictions = self.tx_offset_dbm - 10.0 * self.path_loss_exponent * np.log10(
            distances / self.d0_m
        )
        predictions[~self.los_mask(points)] = np.nan
        return predictions


class FullSimpleRTModel(RadioModel):
    name = "Simple RT (Full)"

    def __init__(self, config: dict | None = None) -> None:
        root = config or {}
        cfg = root.get("full", root)
        self.root_config = root
        self.cfg = cfg
        self.d0_m = float(root.get("d0_m", 1.0))
        self.min_distance_m = float(root.get("min_distance_m", 0.1))
        self.point_tolerance_m = float(cfg.get("point_tolerance_m", 0.08))
        self.dedup_tolerance_m = float(cfg.get("dedup_tolerance_m", 0.05))
        reflections = cfg.get("reflections", {})
        diffraction = cfg.get("diffraction", {})
        limits = cfg.get("path_limits", {})
        calibration = cfg.get("calibration", {})
        self.enable_one_bounce = bool(reflections.get("enable_one_bounce", True))
        self.enable_two_bounce = bool(reflections.get("enable_two_bounce", True))
        self.enable_diffraction = bool(diffraction.get("enabled", True))
        self.max_one = int(limits.get("max_one_bounce_per_rx", 20))
        self.max_two = int(limits.get("max_two_bounce_per_rx", 30))
        self.max_diffraction = int(limits.get("max_diffraction_per_rx", 20))
        self.n_bounds = (
            float(calibration.get("n_min", 0.5)),
            float(calibration.get("n_max", 6.0)),
        )
        self.r_bounds = (
            float(calibration.get("reflection_min", 0.01)),
            float(calibration.get("reflection_max", 0.99)),
        )
        self.diffraction_bounds = (
            float(calibration.get("diffraction_loss_min_db", 0.0)),
            float(calibration.get("diffraction_loss_max_db", 40.0)),
        )
        self.initial_parameters = np.asarray(
            [
                calibration.get("n_init", 2.0),
                calibration.get("reflection_init", 0.6),
                calibration.get("diffraction_loss_init_db", 10.0),
            ],
            dtype=float,
        )
        if str(calibration.get("optimizer", "adam")).casefold() != "adam":
            raise ValueError("Full Simple RT differentiable calibration supports optimizer='adam'.")
        self.learning_rate = float(calibration.get("learning_rate", 0.02))
        self.epochs = int(calibration.get("epochs", 1000))
        self.random_state = int(calibration.get("random_state", 42))
        if self.learning_rate <= 0 or self.epochs < 1:
            raise ValueError("Full Simple RT Adam learning_rate and epochs must be positive.")
        self.mesh = None
        self.tx: np.ndarray | None = None
        self.planes = []
        self.edges = []
        self.geometry_cache: dict[tuple[float, float, float], list[PropagationPath]] = {}
        self.path_loss_exponent: float | None = None
        self.reflection_coefficient: float | None = None
        self.diffraction_loss_db: float | None = None
        self.tx_offset_dbm: float | None = None
        self.final_training_loss: float | None = None

    @staticmethod
    def _positions(values, label: str) -> np.ndarray:
        points = np.asarray(values, dtype=float)
        if points.ndim != 2 or points.shape[1] != 3 or points.shape[0] == 0:
            raise ValueError(f"{label} must have non-empty shape [N, 3].")
        if not np.all(np.isfinite(points)):
            raise ValueError(f"{label} contains NaN or infinite values.")
        return points

    @staticmethod
    def mirror_point(point, normal, offset: float) -> np.ndarray:
        value = np.asarray(point, dtype=float)
        unit = np.asarray(normal, dtype=float)
        return value - 2.0 * (float(np.dot(value, unit)) - offset) * unit

    @staticmethod
    def line_plane_intersection(start, end, normal, offset: float) -> np.ndarray | None:
        first = np.asarray(start, dtype=float)
        delta = np.asarray(end, dtype=float) - first
        denominator = float(np.dot(delta, normal))
        if abs(denominator) < 1e-9:
            return None
        fraction = (offset - float(np.dot(first, normal))) / denominator
        if fraction <= 1e-4 or fraction >= 1.0 - 1e-4:
            return None
        return first + fraction * delta

    def _set_context(self, ctx: dict | None) -> None:
        if not ctx or ctx.get("mesh") is None or ctx.get("tx") is None:
            raise ValueError("Full Simple RT fit requires ctx with 'mesh' and 'tx'.")
        self.mesh = ctx["mesh"]
        self.tx = np.asarray(ctx["tx"], dtype=float)
        cfg = self.cfg
        self.planes = self.mesh.extract_dominant_planes(
            top_k=int(cfg.get("dominant_planes_k", 10)),
            normal_angle_tolerance_deg=float(
                cfg.get("plane_normal_angle_tolerance_deg", 10)
            ),
            offset_tolerance_m=float(cfg.get("plane_offset_tolerance_m", 0.10)),
        )
        diffraction = cfg.get("diffraction", {})
        self.edges = self.mesh.extract_wedge_edges(
            maximum_edges=int(diffraction.get("max_edges", 30)),
            minimum_edge_length_m=float(diffraction.get("minimum_edge_length_m", 0.08)),
            minimum_dihedral_deg=float(diffraction.get("minimum_dihedral_deg", 25)),
        )
        if not self.planes:
            warnings.warn("No dominant reflection planes were extracted.", stacklevel=2)
        if self.enable_diffraction and not self.edges:
            warnings.warn("No diffraction wedge edges were extracted.", stacklevel=2)

    def _segment_clear(self, start, end) -> bool:
        return not self.mesh.is_blocked(start, end)

    def _one_bounce_point(self, rx, plane) -> np.ndarray | None:
        tx_side = float(np.dot(self.tx, plane.normal) - plane.offset)
        rx_side = float(np.dot(rx, plane.normal) - plane.offset)
        if tx_side * rx_side <= 1e-4:
            return None
        image_tx = self.mirror_point(self.tx, plane.normal, plane.offset)
        point = self.line_plane_intersection(image_tx, rx, plane.normal, plane.offset)
        if point is None or not self.mesh.point_on_plane_surface(
            point, plane, self.point_tolerance_m
        ):
            return None
        return point

    def _two_bounce_points(self, rx, first_plane, second_plane):
        image_one = self.mirror_point(self.tx, first_plane.normal, first_plane.offset)
        image_two = self.mirror_point(
            image_one, second_plane.normal, second_plane.offset
        )
        point_two = self.line_plane_intersection(
            image_two, rx, second_plane.normal, second_plane.offset
        )
        if point_two is None:
            return None
        point_one = self.line_plane_intersection(
            image_one, point_two, first_plane.normal, first_plane.offset
        )
        if (
            point_one is None
            or np.linalg.norm(point_one - point_two) < 0.03
            or not self.mesh.point_on_plane_surface(
                point_one, first_plane, self.point_tolerance_m
            )
            or not self.mesh.point_on_plane_surface(
                point_two, second_plane, self.point_tolerance_m
            )
        ):
            return None
        return point_one, point_two

    @staticmethod
    def _minimum_length_point_on_edge(tx, rx, edge) -> np.ndarray:
        low, high = 0.0, 1.0
        for _ in range(18):
            left = low + (high - low) / 3.0
            right = high - (high - low) / 3.0
            left_point = edge.point_a + (edge.point_b - edge.point_a) * left
            right_point = edge.point_a + (edge.point_b - edge.point_a) * right
            left_length = np.linalg.norm(tx - left_point) + np.linalg.norm(rx - left_point)
            right_length = np.linalg.norm(tx - right_point) + np.linalg.norm(rx - right_point)
            if left_length <= right_length:
                high = right
            else:
                low = left
        return edge.point_a + (edge.point_b - edge.point_a) * ((low + high) * 0.5)

    def _is_duplicate(self, path: PropagationPath, paths: list[PropagationPath]) -> bool:
        for existing in paths:
            if path.path_type != existing.path_type or len(path.points) != len(existing.points):
                continue
            if (
                abs(path.total_length_m - existing.total_length_m) <= self.dedup_tolerance_m
                and np.all(
                    np.linalg.norm(path.points - existing.points, axis=1)
                    <= self.dedup_tolerance_m
                )
            ):
                return True
        return False

    @staticmethod
    def _make_path(path_type, points, bounce_count=0, diffraction_count=0,
                   plane_ids=(), edge_id=None) -> PropagationPath:
        values = np.asarray(points, dtype=float)
        length = float(np.linalg.norm(np.diff(values, axis=0), axis=1).sum())
        return PropagationPath(
            path_type, length, bounce_count, diffraction_count, values,
            tuple(plane_ids), edge_id,
        )

    def _enumerate_one(self, rx) -> list[PropagationPath]:
        paths: list[PropagationPath] = []
        los_clear = self._segment_clear(self.tx, rx)
        if los_clear:
            paths.append(self._make_path("los", [self.tx, rx]))
        if self.enable_one_bounce:
            candidates = []
            for plane in self.planes:
                bounce = self._one_bounce_point(rx, plane)
                if (
                    bounce is not None
                    and self._segment_clear(self.tx, bounce)
                    and self._segment_clear(bounce, rx)
                ):
                    candidate = self._make_path(
                        "reflection_1", [self.tx, bounce, rx], 1, 0,
                        plane_ids=(plane.plane_id,),
                    )
                    if not self._is_duplicate(candidate, candidates):
                        candidates.append(candidate)
            paths.extend(sorted(candidates, key=lambda path: path.total_length_m)[: self.max_one])
        if self.enable_two_bounce:
            candidates = []
            for first in self.planes:
                for second in self.planes:
                    if first.plane_id == second.plane_id:
                        continue
                    bounce_points = self._two_bounce_points(rx, first, second)
                    if bounce_points is None:
                        continue
                    first_point, second_point = bounce_points
                    if (
                        self._segment_clear(self.tx, first_point)
                        and self._segment_clear(first_point, second_point)
                        and self._segment_clear(second_point, rx)
                    ):
                        candidate = self._make_path(
                            "reflection_2", [self.tx, first_point, second_point, rx],
                            2, 0, plane_ids=(first.plane_id, second.plane_id),
                        )
                        if not self._is_duplicate(candidate, candidates):
                            candidates.append(candidate)
            paths.extend(sorted(candidates, key=lambda path: path.total_length_m)[: self.max_two])
        if self.enable_diffraction and not los_clear:
            candidates = []
            for edge in self.edges:
                point = self._minimum_length_point_on_edge(self.tx, rx, edge)
                if self._segment_clear(self.tx, point) and self._segment_clear(point, rx):
                    candidate = self._make_path(
                        "diffraction", [self.tx, point, rx], 0, 1, edge_id=edge.edge_id
                    )
                    if not self._is_duplicate(candidate, candidates):
                        candidates.append(candidate)
            paths.extend(
                sorted(candidates, key=lambda path: path.total_length_m)[: self.max_diffraction]
            )
        return paths

    @staticmethod
    def _cache_key(rx) -> tuple[float, float, float]:
        return tuple(np.round(np.asarray(rx, dtype=float), 5))

    def enumerate_paths(self, positions, progress_label: str | None = None):
        points = self._positions(positions, "positions")
        output = []
        interval = max(100, len(points) // 10)
        for index, rx in enumerate(points, start=1):
            key = self._cache_key(rx)
            if key not in self.geometry_cache:
                self.geometry_cache[key] = self._enumerate_one(rx)
            output.append(self.geometry_cache[key])
            if progress_label and len(points) >= 100 and (
                index % interval == 0 or index == len(points)
            ):
                print(f"  {progress_label}: {index} / {len(points)}")
        return output

    def _base_predictions(self, path_sets, parameters) -> np.ndarray:
        exponent, reflection, diffraction_loss = parameters
        values = np.full(len(path_sets), np.nan, dtype=float)
        for index, paths in enumerate(path_sets):
            if not paths:
                continue
            powers = []
            for path in paths:
                length = max(path.total_length_m, self.min_distance_m)
                power = (self.d0_m / length) ** exponent
                power *= reflection ** (2 * path.bounce_count)
                power *= 10.0 ** (-diffraction_loss * path.diffraction_count / 10.0)
                powers.append(power)
            total = float(np.sum(powers))
            if total > 0:
                values[index] = 10.0 * np.log10(total + 1e-30)
        return values

    @staticmethod
    def _inverse_bounded(value: float, bounds: tuple[float, float]) -> float:
        fraction = (value - bounds[0]) / (bounds[1] - bounds[0])
        fraction = float(np.clip(fraction, 1e-6, 1.0 - 1e-6))
        return float(np.log(fraction / (1.0 - fraction)))

    @staticmethod
    def _bounded(raw: torch.Tensor, bounds: tuple[float, float]) -> torch.Tensor:
        return bounds[0] + (bounds[1] - bounds[0]) * torch.sigmoid(raw)

    @staticmethod
    def _path_tensors(path_sets):
        lengths = []
        bounces = []
        diffractions = []
        receiver_indices = []
        for receiver_index, paths in enumerate(path_sets):
            for path in paths:
                lengths.append(path.total_length_m)
                bounces.append(path.bounce_count)
                diffractions.append(path.diffraction_count)
                receiver_indices.append(receiver_index)
        dtype = torch.float64
        return (
            torch.as_tensor(lengths, dtype=dtype),
            torch.as_tensor(bounces, dtype=dtype),
            torch.as_tensor(diffractions, dtype=dtype),
            torch.as_tensor(receiver_indices, dtype=torch.int64),
        )

    def _torch_predictions(
        self,
        path_tensors,
        receiver_count: int,
        raw_n: torch.Tensor,
        raw_reflection: torch.Tensor,
        raw_diffraction: torch.Tensor,
        offset_dbm: torch.Tensor,
    ):
        lengths, bounces, diffractions, receiver_indices = path_tensors
        exponent = self._bounded(raw_n, self.n_bounds)
        reflection = self._bounded(raw_reflection, self.r_bounds)
        diffraction_loss = self._bounded(raw_diffraction, self.diffraction_bounds)
        safe_lengths = torch.clamp(lengths, min=self.min_distance_m)
        path_power = torch.pow(self.d0_m / safe_lengths, exponent)
        path_power = path_power * torch.pow(reflection, 2.0 * bounces)
        path_power = path_power * torch.pow(
            torch.tensor(10.0, dtype=torch.float64),
            -diffraction_loss * diffractions / 10.0,
        )
        total_power = torch.zeros(receiver_count, dtype=torch.float64)
        total_power.scatter_add_(0, receiver_indices, path_power)
        predictions = offset_dbm + 10.0 * torch.log10(total_power + 1e-30)
        return predictions, total_power, exponent, reflection, diffraction_loss

    def fit(self, train_pos, train_rssi, ctx=None):
        points = self._positions(train_pos, "train_pos")
        observed = np.asarray(train_rssi, dtype=float)
        if observed.shape != (len(points),) or not np.all(np.isfinite(observed)):
            raise ValueError("train_rssi must be finite and match train_pos rows.")
        self._set_context(ctx)
        path_sets = self.enumerate_paths(points, "training geometry")
        covered = np.asarray([bool(paths) for paths in path_sets])
        if covered.sum() < 2:
            raise ValueError("Full Simple RT needs at least two covered training samples.")

        torch.manual_seed(self.random_state)
        path_tensors = self._path_tensors(path_sets)
        raw_n = torch.nn.Parameter(
            torch.tensor(self._inverse_bounded(self.initial_parameters[0], self.n_bounds), dtype=torch.float64)
        )
        raw_reflection = torch.nn.Parameter(
            torch.tensor(self._inverse_bounded(self.initial_parameters[1], self.r_bounds), dtype=torch.float64)
        )
        raw_diffraction = torch.nn.Parameter(
            torch.tensor(self._inverse_bounded(self.initial_parameters[2], self.diffraction_bounds), dtype=torch.float64)
        )
        initial_base = self._base_predictions(path_sets, self.initial_parameters)
        initial_offset = float(np.mean(observed[covered] - initial_base[covered]))
        offset_dbm = torch.nn.Parameter(torch.tensor(initial_offset, dtype=torch.float64))
        optimizer = torch.optim.Adam(
            [raw_n, raw_reflection, raw_diffraction, offset_dbm],
            lr=self.learning_rate,
        )
        observed_tensor = torch.as_tensor(observed, dtype=torch.float64)
        covered_tensor = torch.as_tensor(covered, dtype=torch.bool)
        for _ in range(self.epochs):
            optimizer.zero_grad()
            predictions, _, _, _, _ = self._torch_predictions(
                path_tensors,
                len(points),
                raw_n,
                raw_reflection,
                raw_diffraction,
                offset_dbm,
            )
            loss = torch.mean(
                (predictions[covered_tensor] - observed_tensor[covered_tensor]) ** 2
            )
            loss.backward()
            optimizer.step()
        with torch.no_grad():
            predictions, _, exponent, reflection, diffraction_loss = self._torch_predictions(
                path_tensors,
                len(points),
                raw_n,
                raw_reflection,
                raw_diffraction,
                offset_dbm,
            )
            self.final_training_loss = float(
                torch.mean(
                    (predictions[covered_tensor] - observed_tensor[covered_tensor]) ** 2
                ).item()
            )
            self.path_loss_exponent = float(exponent.item())
            self.reflection_coefficient = float(reflection.item())
            self.diffraction_loss_db = float(diffraction_loss.item())
            self.tx_offset_dbm = float(offset_dbm.item())
        for value, bounds_value, name in (
            (self.path_loss_exponent, self.n_bounds, "path-loss exponent n"),
            (self.reflection_coefficient, self.r_bounds, "reflection coefficient R"),
            (self.diffraction_loss_db, self.diffraction_bounds, "diffraction loss"),
        ):
            tolerance = max(1e-4, (bounds_value[1] - bounds_value[0]) * 0.01)
            if value <= bounds_value[0] + tolerance or value >= bounds_value[1] - tolerance:
                warnings.warn(f"Fitted {name}={value:.3f} is at/near a bound.", stacklevel=2)
        return self

    @property
    def fitted_parameters(self) -> np.ndarray:
        if self.path_loss_exponent is None:
            raise RuntimeError("Full Simple RT model has not been fit.")
        return np.asarray(
            [self.path_loss_exponent, self.reflection_coefficient, self.diffraction_loss_db]
        )

    def predict(self, positions) -> np.ndarray:
        if self.tx_offset_dbm is None:
            raise RuntimeError("Full Simple RT model must be fit before prediction.")
        points = self._positions(positions, "positions")
        path_sets = self.enumerate_paths(points, "radio-map geometry")
        path_tensors = self._path_tensors(path_sets)
        raw_n = torch.tensor(
            self._inverse_bounded(self.path_loss_exponent, self.n_bounds), dtype=torch.float64
        )
        raw_reflection = torch.tensor(
            self._inverse_bounded(self.reflection_coefficient, self.r_bounds), dtype=torch.float64
        )
        raw_diffraction = torch.tensor(
            self._inverse_bounded(self.diffraction_loss_db, self.diffraction_bounds), dtype=torch.float64
        )
        with torch.no_grad():
            predictions, total_power, _, _, _ = self._torch_predictions(
                path_tensors,
                len(points),
                raw_n,
                raw_reflection,
                raw_diffraction,
                torch.tensor(self.tx_offset_dbm, dtype=torch.float64),
            )
        values = predictions.numpy()
        values[total_power.numpy() <= 0] = np.nan
        return values

    def mechanism_codes(self, positions) -> np.ndarray:
        path_sets = self.enumerate_paths(positions)
        codes = np.zeros(len(path_sets), dtype=np.uint8)
        for index, paths in enumerate(path_sets):
            types = {path.path_type for path in paths}
            has_los = "los" in types
            has_reflection = bool(types & {"reflection_1", "reflection_2"})
            has_diffraction = "diffraction" in types
            mechanisms = int(has_los) + int(has_reflection) + int(has_diffraction)
            if mechanisms > 1:
                codes[index] = 4
            elif has_los:
                codes[index] = 1
            elif has_reflection:
                codes[index] = 2
            elif has_diffraction:
                codes[index] = 3
        return codes

    def path_statistics(self, positions) -> dict:
        path_sets = self.enumerate_paths(positions)
        counts = {kind: 0 for kind in ("los", "reflection_1", "reflection_2", "diffraction")}
        totals = []
        for paths in path_sets:
            totals.append(len(paths))
            for path in paths:
                counts[path.path_type] += 1
        counts["covered_rx"] = sum(total > 0 for total in totals)
        counts["no_path_rx"] = sum(total == 0 for total in totals)
        counts["average_paths_per_rx"] = float(np.mean(totals)) if totals else 0.0
        counts["maximum_paths_per_rx"] = max(totals, default=0)
        return counts


def save_path_type_map(path: str | Path, x_grid, z_grid, codes) -> Path:
    from matplotlib.colors import BoundaryNorm, ListedColormap

    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    code_grid = np.asarray(codes).reshape(np.asarray(x_grid).shape)
    colors = ["white", "#3cb44b", "#4363d8", "#f58231", "#911eb4"]
    labels = ["No path", "LOS", "Reflection only", "Diffraction only", "Multiple"]
    figure, axis = plt.subplots(figsize=(8, 7), constrained_layout=True)
    image = axis.pcolormesh(
        x_grid, z_grid, code_grid, shading="auto",
        cmap=ListedColormap(colors), norm=BoundaryNorm(np.arange(-0.5, 5.5), 5),
    )
    colorbar = figure.colorbar(image, ax=axis, ticks=range(5))
    colorbar.ax.set_yticklabels(labels)
    axis.set_xlabel("x (m)")
    axis.set_ylabel("z (m)")
    axis.set_title("Simple RT Full Path-Type Availability")
    axis.set_aspect("equal", adjustable="box")
    figure.savefig(destination, dpi=180)
    plt.close(figure)
    return destination.resolve()


def save_los_diagnostic(
    path: str | Path,
    positions,
    los_mask,
    tx,
) -> Path:
    points = np.asarray(positions, dtype=float)
    los = np.asarray(los_mask, dtype=bool)
    tx_position = np.asarray(tx, dtype=float)
    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    figure, axis = plt.subplots(figsize=(7, 7), constrained_layout=True)
    axis.plot(points[:, 0], points[:, 2], color="0.7", linewidth=0.8)
    axis.scatter(points[los, 0], points[los, 2], color="green", s=28, label="LOS RX")
    axis.scatter(
        points[~los, 0], points[~los, 2], color="red", marker="x", s=40,
        label="Blocked RX",
    )
    axis.scatter(
        [tx_position[0]], [tx_position[2]], color="magenta", marker="*", s=160,
        edgecolors="black", linewidths=0.6, label="TX",
    )
    axis.set_xlabel("x (m)")
    axis.set_ylabel("z (m)")
    axis.set_title("Simple RT LOS Diagnostic")
    axis.set_aspect("equal", adjustable="box")
    axis.legend(loc="best")
    figure.savefig(destination, dpi=180)
    plt.close(figure)
    return destination.resolve()
