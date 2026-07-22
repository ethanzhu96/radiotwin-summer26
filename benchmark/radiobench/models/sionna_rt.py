"""Optional Sionna-RT backend for the offline radio benchmark.

The calibrated material is an effective global room material. Its parameters
must not be interpreted as measurements of every physical surface in the room.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass
from hashlib import sha256
import importlib.metadata
import json
from pathlib import Path
import time
from typing import Any, Callable

import numpy as np

from .base import RadioModel


try:  # Heavy optional dependency; importing the rest of radiobench must stay safe.
    from sionna.rt import (
        PathSolver,
        PlanarArray,
        RadioMaterial,
        Receiver,
        SceneObject,
        Transmitter,
        load_scene,
    )

    SIONNA_AVAILABLE = True
    SIONNA_IMPORT_ERROR: Exception | None = None
except (ImportError, OSError, RuntimeError) as error:  # DLL/backend failures are actionable too.
    PathSolver = PlanarArray = RadioMaterial = Receiver = SceneObject = None
    Transmitter = load_scene = None
    SIONNA_AVAILABLE = False
    SIONNA_IMPORT_ERROR = error


COORDINATE_TRANSFORM = "unity(x,y,z)->sionna(x,z,y)"
MESH_TRANSFORM_VERSION = "unity-xzy-effectmesh-reverse-winding-v2"


def unity_to_sionna(points) -> np.ndarray:
    """Convert one or many Unity y-up points to Sionna z-up coordinates."""
    values = np.asarray(points, dtype=float)
    if values.shape == (3,):
        return values[[0, 2, 1]].copy()
    if values.ndim == 2 and values.shape[1] == 3:
        return values[:, [0, 2, 1]].copy()
    raise ValueError(f"points must have shape (3,) or (N, 3); got {values.shape}.")


def _transform_obj_text(text: str) -> str:
    """Transform OBJ geometry, keeping only MRUK room faces when identifiable."""
    output: list[str] = []
    has_effect_mesh = any(
        line.lstrip().startswith("o ") and "EffectMesh" in line
        for line in text.splitlines()
    )
    include_faces = not has_effect_mesh
    for raw_line in text.splitlines(keepends=True):
        newline = "\r\n" if raw_line.endswith("\r\n") else "\n" if raw_line.endswith("\n") else ""
        line = raw_line.rstrip("\r\n")
        parts = line.split()
        if parts and parts[0] == "o":
            include_faces = not has_effect_mesh or "EffectMesh" in " ".join(parts[1:])
            output.append(raw_line)
        elif parts and parts[0] in {"v", "vn"} and len(parts) >= 4:
            prefix = parts[0]
            xyz = np.asarray(parts[1:4], dtype=float)
            transformed = unity_to_sionna(xyz)
            suffix = parts[4:]
            output.append(" ".join([prefix, *(f"{v:.17g}" for v in transformed), *suffix]) + newline)
        elif parts and parts[0] == "f" and len(parts) >= 4:
            if include_faces:
                # An odd axis permutation reverses handedness. Reversing all face
                # corners restores the original geometric normal orientation.
                output.append(" ".join(["f", *reversed(parts[1:])]) + newline)
        else:
            output.append(raw_line)
    return "".join(output)


def prepare_sionna_obj(source: str | Path, cache_root: str | Path) -> tuple[Path, str]:
    """Create or reuse a deterministic, transformed OBJ without touching source."""
    source_path = Path(source).resolve()
    if not source_path.is_file():
        raise FileNotFoundError(f"Sionna room mesh does not exist: {source_path}")
    source_bytes = source_path.read_bytes()
    mesh_hash = sha256(MESH_TRANSFORM_VERSION.encode() + b"\0" + source_bytes).hexdigest()
    destination_dir = Path(cache_root) / mesh_hash
    destination = destination_dir / f"{source_path.stem}_sionna.obj"
    if not destination.is_file():
        destination_dir.mkdir(parents=True, exist_ok=True)
        try:
            source_text = source_bytes.decode("utf-8")
        except UnicodeDecodeError as error:
            raise ValueError(f"OBJ must be UTF-8 text: {source_path}") from error
        destination.write_text(_transform_obj_text(source_text), encoding="utf-8", newline="")
    return destination.resolve(), mesh_hash


def analytic_offset_and_rmse(measured_dbm, simulated_gain_db) -> tuple[float, float]:
    measured = np.asarray(measured_dbm, dtype=float)
    simulated = np.asarray(simulated_gain_db, dtype=float)
    if measured.ndim != 1 or simulated.shape != measured.shape or measured.size == 0:
        raise ValueError("measured and simulated arrays must be matching non-empty vectors.")
    if not np.all(np.isfinite(measured)) or not np.all(np.isfinite(simulated)):
        raise ValueError("calibration arrays must contain only finite values.")
    offset = float(np.mean(measured - simulated))
    residual = simulated + offset - measured
    return offset, float(np.sqrt(np.mean(np.square(residual))))


def evenly_spaced_indices(total: int, maximum: int) -> np.ndarray:
    if total <= 0 or maximum <= 0:
        raise ValueError("total and maximum must be positive.")
    if total <= maximum:
        return np.arange(total, dtype=int)
    return np.linspace(0, total - 1, maximum, dtype=int)


@dataclass(frozen=True)
class CandidateResult:
    relative_permittivity: float
    conductivity: float
    offset_db: float
    training_rmse_db: float
    num_no_path: int
    elapsed_seconds: float
    stage: str


class SionnaRTModel(RadioModel):
    """LOS + depth-two specular Sionna model with black-box material search."""

    name = "Sionna RT"

    def __init__(self, config: dict | None = None) -> None:
        cfg = config or {}
        search = cfg.get("search", {})
        self.config = cfg
        self.epsilon_bounds = tuple(map(float, search.get("relative_permittivity", [1.5, 12.0])))
        self.sigma_bounds = tuple(map(float, search.get("conductivity", [1e-4, 5.0])))
        self.coarse_epsilon_points = int(search.get("coarse_epsilon_points", 6))
        self.coarse_sigma_points = int(search.get("coarse_sigma_points", 6))
        self.refinement_rounds = int(search.get("refinement_rounds", 1))
        self.refinement_points = int(search.get("refinement_points", 5))
        self.max_calibration_points = int(cfg.get("calibration_max_points", 100))
        self.no_path_gain_db = float(cfg.get("no_path_gain_db", -200.0))
        self.seed = int(cfg.get("seed", 42))
        self.samples_per_src = int(cfg.get("samples_per_src", 1_000_000))
        self.progress = bool(cfg.get("progress", True))
        self.best_relative_permittivity: float | None = None
        self.best_conductivity: float | None = None
        self.offset_db: float | None = None
        self.training_rmse: float | None = None
        self.frequency_hz: float | None = None
        self.num_training_points = 0
        self.num_calibration_points = 0
        self.num_no_path_calibration_points = 0
        self.candidate_results: list[CandidateResult] = []
        self.calibration_indices: np.ndarray | None = None
        self.mesh_source: Path | None = None
        self.transformed_mesh: Path | None = None
        self.mesh_hash: str | None = None
        self.artifact_path: Path | None = None
        self.last_num_no_path_predictions = 0
        self.last_num_predictions = 0
        self._scene = self._material = self._receiver = self._solver = None
        self._simulator: Callable[[np.ndarray, float, float], tuple[np.ndarray, np.ndarray]] | None = None
        self._fitted = False

        if not (1.0 < self.epsilon_bounds[0] < self.epsilon_bounds[1]):
            raise ValueError("Sionna relative-permittivity bounds must satisfy 1 < min < max.")
        if not (0.0 < self.sigma_bounds[0] < self.sigma_bounds[1]):
            raise ValueError("Sionna conductivity bounds must satisfy 0 < min < max.")
        if min(self.coarse_epsilon_points, self.coarse_sigma_points, self.refinement_points) < 2:
            raise ValueError("Sionna search grids must contain at least two points per axis.")
        if self.refinement_rounds < 0 or self.max_calibration_points <= 0:
            raise ValueError("Invalid Sionna refinement/calibration point count.")
        if not np.isfinite(self.no_path_gain_db):
            raise ValueError("Sionna no-path gain floor must be finite.")

    @staticmethod
    def _positions(values, label: str) -> np.ndarray:
        positions = np.asarray(values, dtype=float)
        if positions.ndim != 2 or positions.shape[1] != 3 or positions.shape[0] == 0:
            raise ValueError(f"{label} must have shape [N, 3] and be non-empty.")
        if not np.all(np.isfinite(positions)):
            raise ValueError(f"{label} contains NaN or infinite values.")
        return positions

    @staticmethod
    def _require_sionna() -> None:
        if not SIONNA_AVAILABLE:
            detail = f" ({SIONNA_IMPORT_ERROR})" if SIONNA_IMPORT_ERROR else ""
            raise RuntimeError(
                "Sionna-RT is not installed or could not initialize. Install the optional "
                f"dependency with 'pip install sionna-rt' to use model='sionna_rt'.{detail}"
            )

    @staticmethod
    def _frequency(ctx: dict[str, Any]) -> float:
        value = ctx.get("frequency_hz")
        if value is None:
            raise ValueError("Sionna ctx must provide the dataset carrier as frequency_hz.")
        frequency = float(value)
        if not np.isfinite(frequency) or frequency <= 0:
            raise ValueError("Sionna frequency_hz must be positive and finite.")
        return frequency

    def _initialize_scene(self, ctx: dict[str, Any]) -> None:
        self._require_sionna()
        mesh_path = ctx.get("mesh_path")
        tx = np.asarray(ctx.get("tx"), dtype=float)
        if mesh_path is None or tx.shape != (3,) or not np.all(np.isfinite(tx)):
            raise ValueError("Sionna ctx requires mesh_path and a finite TX vector.")
        self.mesh_source = Path(mesh_path).resolve()
        cache_root = Path(ctx.get("cache_dir", self.mesh_source.parent / ".sionna_cache"))
        self.transformed_mesh, self.mesh_hash = prepare_sionna_obj(self.mesh_source, cache_root)
        self.frequency_hz = self._frequency(ctx)

        self._scene = load_scene(None)
        self._scene.frequency = self.frequency_hz
        self._material = RadioMaterial(
            name="effective-room-material",
            thickness=0.1,
            relative_permittivity=self.epsilon_bounds[0],
            conductivity=self.sigma_bounds[0],
            scattering_coefficient=0.0,
        )
        room = SceneObject(
            fname=str(self.transformed_mesh),
            name="quest-room",
            radio_material=self._material,
        )
        self._scene.edit(add=[room])
        self._scene.tx_array = PlanarArray(
            num_rows=1, num_cols=1, vertical_spacing=0.5,
            horizontal_spacing=0.5, pattern="iso", polarization="V",
        )
        self._scene.rx_array = PlanarArray(
            num_rows=1, num_cols=1, vertical_spacing=0.5,
            horizontal_spacing=0.5, pattern="iso", polarization="V",
        )
        self._scene.add(Transmitter("benchmark-tx", position=unity_to_sionna(tx)))
        self._receiver = Receiver("benchmark-rx", position=[0.0, 0.0, 0.0])
        self._scene.add(self._receiver)
        self._solver = PathSolver()

    @staticmethod
    def _numpy(value) -> np.ndarray:
        return np.asarray(value.numpy() if hasattr(value, "numpy") else value)

    def _relative_gain_from_paths(self, paths) -> tuple[float, bool]:
        coefficients, delays = paths.cir(out_type="numpy")
        a = np.asarray(coefficients)
        tau = np.asarray(delays)
        if a.ndim == tau.ndim + 1 and a.shape[-1] == 1:
            a = a[..., 0]
        valid = self._numpy(paths.valid).astype(bool) if hasattr(paths, "valid") else np.isfinite(tau)
        try:
            valid = np.broadcast_to(valid, a.shape)
        except ValueError:
            valid = np.broadcast_to(np.squeeze(valid), np.squeeze(a).shape)
            a = np.squeeze(a)
        path_power = np.abs(a[valid]) ** 2
        path_power = path_power[np.isfinite(path_power)]
        linear_gain = float(np.sum(path_power)) if path_power.size else 0.0
        if not np.isfinite(linear_gain) or linear_gain <= 0.0:
            return self.no_path_gain_db, True
        # Deliberately incoherent: RSSI-style spatial averaging uses the sum of
        # individual path powers, not abs(sum(a_i))**2 coherent fast fading.
        return float(10.0 * np.log10(linear_gain)), False

    def _simulate_sionna(self, positions_sionna: np.ndarray, epsilon: float, sigma: float):
        self._material.relative_permittivity = float(epsilon)
        self._material.conductivity = float(sigma)
        gains = np.empty(len(positions_sionna), dtype=float)
        no_path = np.zeros(len(positions_sionna), dtype=bool)
        for index, position in enumerate(positions_sionna):
            self._receiver.position = position
            paths = self._solver(
                scene=self._scene,
                max_depth=2,
                samples_per_src=self.samples_per_src,
                synthetic_array=True,
                los=True,
                specular_reflection=True,
                diffuse_reflection=False,
                refraction=False,
                diffraction=False,
                edge_diffraction=False,
                seed=self.seed,
            )
            gains[index], no_path[index] = self._relative_gain_from_paths(paths)
        return gains, no_path

    def _simulate(self, unity_positions: np.ndarray, epsilon: float, sigma: float):
        positions_sionna = unity_to_sionna(unity_positions)
        simulator = self._simulator or self._simulate_sionna
        gains, no_path = simulator(positions_sionna, float(epsilon), float(sigma))
        gains = np.asarray(gains, dtype=float)
        no_path = np.asarray(no_path, dtype=bool)
        if gains.shape != (len(unity_positions),) or no_path.shape != gains.shape:
            raise RuntimeError("Sionna simulator returned an invalid result shape.")
        if not np.all(np.isfinite(gains)):
            raise RuntimeError("Sionna simulator returned non-finite gains.")
        return gains, no_path

    def _evaluate_grid(self, eps_values, sigma_values, positions, measured, stage):
        candidates: list[CandidateResult] = []
        total = len(eps_values) * len(sigma_values)
        for number, (epsilon, sigma) in enumerate(
            ((e, s) for e in eps_values for s in sigma_values), start=1
        ):
            started = time.perf_counter()
            gains, no_path = self._simulate(positions, epsilon, sigma)
            offset, rmse = analytic_offset_and_rmse(measured, gains)
            result = CandidateResult(
                float(epsilon), float(sigma), offset, rmse, int(no_path.sum()),
                time.perf_counter() - started, stage,
            )
            candidates.append(result)
            if self.progress:
                print(
                    f"  Sionna {stage} candidate {number}/{total}: "
                    f"epsilon={epsilon:.4g}, sigma={sigma:.4g}, RMSE={rmse:.3f} dB"
                )
        return candidates

    @staticmethod
    def _best(results: list[CandidateResult]) -> CandidateResult:
        if not results:
            raise ValueError("Sionna calibration produced no candidates.")
        return min(
            results,
            key=lambda item: (
                item.training_rmse_db,
                item.relative_permittivity,
                item.conductivity,
            ),
        )

    @staticmethod
    def _local_axis(best: float, values: np.ndarray, bounds, count: int, logarithmic=False):
        index = int(np.argmin(np.abs(values - best)))
        lower = values[max(0, index - 1)]
        upper = values[min(len(values) - 1, index + 1)]
        lower, upper = max(float(lower), bounds[0]), min(float(upper), bounds[1])
        if np.isclose(lower, upper):
            return np.asarray([lower])
        return np.geomspace(lower, upper, count) if logarithmic else np.linspace(lower, upper, count)

    def fit(self, train_pos, train_rssi, ctx=None):
        positions = self._positions(train_pos, "train_pos")
        measured = np.asarray(train_rssi, dtype=float)
        if measured.shape != (len(positions),) or not np.all(np.isfinite(measured)):
            raise ValueError("train_rssi must be a finite vector matching train_pos.")
        context = dict(ctx or {})
        self._simulator = context.get("simulator")
        self.frequency_hz = self._frequency(context)
        if self._simulator is None:
            self._initialize_scene(context)
        else:
            self.mesh_source = Path(context.get("mesh_path", "mock.obj"))

        self.num_training_points = len(positions)
        self.calibration_indices = evenly_spaced_indices(len(positions), self.max_calibration_points)
        calibration_positions = positions[self.calibration_indices]
        calibration_rssi = measured[self.calibration_indices]
        self.num_calibration_points = len(calibration_positions)

        eps_values = np.linspace(*self.epsilon_bounds, self.coarse_epsilon_points)
        sigma_values = np.geomspace(*self.sigma_bounds, self.coarse_sigma_points)
        self.candidate_results = self._evaluate_grid(
            eps_values, sigma_values, calibration_positions, calibration_rssi, "coarse"
        )
        best = self._best(self.candidate_results)
        for round_index in range(self.refinement_rounds):
            eps_values = self._local_axis(
                best.relative_permittivity, eps_values, self.epsilon_bounds,
                self.refinement_points,
            )
            sigma_values = self._local_axis(
                best.conductivity, sigma_values, self.sigma_bounds,
                self.refinement_points, logarithmic=True,
            )
            refined = self._evaluate_grid(
                eps_values, sigma_values, calibration_positions, calibration_rssi,
                f"refinement_{round_index + 1}",
            )
            self.candidate_results.extend(refined)
            best = self._best(self.candidate_results)

        self.best_relative_permittivity = best.relative_permittivity
        self.best_conductivity = best.conductivity
        self.offset_db = best.offset_db
        self.training_rmse = best.training_rmse_db
        self.num_no_path_calibration_points = best.num_no_path
        if self._material is not None:
            self._material.relative_permittivity = self.best_relative_permittivity
            self._material.conductivity = self.best_conductivity
        self._fitted = True
        output_dir = context.get("output_dir")
        if output_dir is not None:
            self.save_calibration(Path(output_dir) / "sionna_calibration.json")
        return self

    def predict(self, positions) -> np.ndarray:
        if not self._fitted:
            raise RuntimeError("Sionna RT model must be fit before prediction.")
        checked = self._positions(positions, "positions")
        gains, no_path = self._simulate(
            checked, self.best_relative_permittivity, self.best_conductivity
        )
        self.last_num_no_path_predictions = int(no_path.sum())
        self.last_num_predictions = len(checked)
        return gains + self.offset_db

    @property
    def diagnostics(self) -> dict[str, Any]:
        try:
            version = importlib.metadata.version("sionna-rt")
        except importlib.metadata.PackageNotFoundError:
            version = None
        return {
            "model": "sionna_rt",
            "frequency_hz": self.frequency_hz,
            "max_depth": 2,
            "los": True,
            "specular_reflection": True,
            "diffraction": False,
            "edge_diffraction": False,
            "refraction": False,
            "diffuse_reflection": False,
            "best_relative_permittivity": self.best_relative_permittivity,
            "best_conductivity": self.best_conductivity,
            "offset_db": self.offset_db,
            "training_rmse_db": self.training_rmse,
            "num_training_points": self.num_training_points,
            "num_calibration_points": self.num_calibration_points,
            "calibration_selection": "deterministic evenly spaced trajectory indices",
            "num_no_path_calibration_points": self.num_no_path_calibration_points,
            "last_num_no_path_predictions": self.last_num_no_path_predictions,
            "last_num_predictions": self.last_num_predictions,
            "coordinate_transform": COORDINATE_TRANSFORM,
            "mesh_source": str(self.mesh_source) if self.mesh_source else None,
            "transformed_mesh": str(self.transformed_mesh) if self.transformed_mesh else None,
            "mesh_hash": self.mesh_hash,
            "sionna_version": version,
            "scientific_interpretation": (
                "One effective global room material plus one scalar link-budget offset; "
                "parameters are not physical measurements of every surface."
            ),
            "candidate_results": [asdict(result) for result in self.candidate_results],
        }

    def save_calibration(self, destination: str | Path) -> Path:
        path = Path(destination)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(self.diagnostics, indent=2), encoding="utf-8")
        self.artifact_path = path.resolve()
        return self.artifact_path
