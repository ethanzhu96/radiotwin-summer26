from pathlib import Path
import warnings

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

from .base import RadioModel


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
