from dataclasses import dataclass
from pathlib import Path
import re
import warnings

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np


@dataclass(frozen=True)
class RadioMapResult:
    x_grid: np.ndarray
    z_grid: np.ndarray
    rssi_dbm: np.ndarray
    height_m: float
    png_path: Path
    npz_path: Path
    debug_png_path: Path | None

    @property
    def shape(self) -> tuple[int, int]:
        return self.rssi_dbm.shape


def _axis_values(minimum: float, maximum: float, resolution: float) -> np.ndarray:
    point_count = max(2, int(np.ceil((maximum - minimum) / resolution)) + 1)
    return np.linspace(minimum, maximum, point_count)


def _safe_model_name(model_name: str) -> str:
    slug = re.sub(r"[^a-z0-9]+", "_", model_name.casefold()).strip("_")
    return slug or "radio_model"


def generate_radio_map(
    model,
    measurement_positions,
    train_indices,
    test_indices,
    height_m: float,
    grid_resolution_m: float,
    bounds_padding_m: float,
    model_name: str,
    output_dir: str | Path,
    vmin_dbm: float,
    vmax_dbm: float,
    save_debug_autoscaled: bool = False,
    allow_missing_predictions: bool = False,
) -> RadioMapResult:
    """Predict and save an x-z radio map in Unity/MRUK room-local coordinates."""
    positions = np.asarray(measurement_positions, dtype=float)
    if positions.ndim != 2 or positions.shape[1] != 3 or positions.shape[0] == 0:
        raise ValueError("measurement_positions must have shape [N, 3] and be non-empty.")
    if not np.all(np.isfinite(positions)):
        raise ValueError("measurement_positions contains NaN or infinite values.")
    if not np.isfinite(height_m):
        raise ValueError("radiomap.eval_height_m must be finite.")
    if not np.isfinite(grid_resolution_m) or grid_resolution_m <= 0:
        raise ValueError("radiomap.grid_resolution_m must be positive and finite.")
    if not np.isfinite(bounds_padding_m) or bounds_padding_m < 0:
        raise ValueError("radiomap.bounds_padding_m cannot be negative.")
    if not np.isfinite(vmin_dbm) or not np.isfinite(vmax_dbm) or vmin_dbm >= vmax_dbm:
        raise ValueError("radiomap color limits must be finite with vmin_dbm < vmax_dbm.")

    x_min = float(np.min(positions[:, 0]) - bounds_padding_m)
    x_max = float(np.max(positions[:, 0]) + bounds_padding_m)
    z_min = float(np.min(positions[:, 2]) - bounds_padding_m)
    z_max = float(np.max(positions[:, 2]) + bounds_padding_m)
    x_values = _axis_values(x_min, x_max, grid_resolution_m)
    z_values = _axis_values(z_min, z_max, grid_resolution_m)
    x_grid, z_grid = np.meshgrid(x_values, z_values, indexing="xy")
    point_count = int(x_grid.size)
    if point_count > 250_000:
        warnings.warn(
            f"Radio-map resolution creates a large grid of {point_count:,} points.",
            stacklevel=2,
        )

    grid_positions = np.column_stack(
        (
            x_grid.ravel(),
            np.full(point_count, height_m, dtype=float),
            z_grid.ravel(),
        )
    )
    predictions = np.asarray(model.predict(grid_positions), dtype=float)
    if predictions.shape != (point_count,):
        raise ValueError(
            f"Model returned prediction shape {predictions.shape}; expected {(point_count,)}."
        )
    finite_predictions = predictions[np.isfinite(predictions)]
    if not allow_missing_predictions and finite_predictions.size != predictions.size:
        raise ValueError("Radio-map predictions contain NaN or infinite values.")
    if finite_predictions.size == 0:
        raise ValueError("Radio-map has no finite model predictions.")
    if finite_predictions.min() < -150 or finite_predictions.max() > 20:
        warnings.warn(
            "Radio-map predictions extend outside a broad plausible RSSI range "
            f"({finite_predictions.min():.2f} to {finite_predictions.max():.2f} dBm).",
            stacklevel=2,
        )
    rssi_grid = predictions.reshape(x_grid.shape)

    destination = Path(output_dir)
    destination.mkdir(parents=True, exist_ok=True)
    model_slug = _safe_model_name(model_name)
    height_label = f"{height_m:.2f}"
    png_path = destination / f"{model_slug}_radiomap_h{height_label}m.png"
    npz_path = destination / f"{model_slug}_radiomap_h{height_label}m.npz"
    debug_png_path = (
        destination / f"{model_slug}_radiomap_h{height_label}m_debug_autoscaled.png"
        if save_debug_autoscaled
        else None
    )

    np.savez_compressed(
        npz_path,
        x_grid=x_grid,
        z_grid=z_grid,
        rssi_dbm=rssi_grid,
        height_m=np.asarray(height_m),
        x_values=x_values,
        z_values=z_values,
        vmin_dbm=np.asarray(vmin_dbm),
        vmax_dbm=np.asarray(vmax_dbm),
        model_name=np.asarray(model_name),
    )

    train_indices = np.asarray(train_indices, dtype=int)
    test_indices = np.asarray(test_indices, dtype=int)

    def save_figure(path: Path, color_min: float, color_max: float, debug: bool) -> None:
        figure, axis = plt.subplots(figsize=(8, 7), constrained_layout=True)
        heatmap = axis.pcolormesh(
            x_grid,
            z_grid,
            np.ma.masked_invalid(rssi_grid),
            shading="auto",
            cmap="viridis",
            vmin=color_min,
            vmax=color_max,
        )
        axis.plot(
            positions[:, 0], positions[:, 2], color="white", linewidth=0.8,
            alpha=0.65, label="Measured trajectory",
        )
        axis.scatter(
            positions[train_indices, 0], positions[train_indices, 2], s=18,
            facecolors="white", edgecolors="black", linewidths=0.5,
            label="Training samples", zorder=3,
        )
        axis.scatter(
            positions[test_indices, 0], positions[test_indices, 2], s=42,
            marker="x", color="red", linewidths=1.5,
            label="Held-out test samples", zorder=4,
        )
        axis.set_xlabel("x (m)")
        axis.set_ylabel("z (m)")
        title = f"{model_name} Radio Map — y = {height_m:.2f} m"
        if debug:
            title += "\nDEBUG AUTOSCALED"
        axis.set_title(title)
        axis.set_aspect("equal", adjustable="box")
        axis.legend(loc="best", fontsize="small")
        colorbar = figure.colorbar(heatmap, ax=axis)
        colorbar.set_label("Predicted RSSI (dBm)")
        figure.savefig(path, dpi=180)
        plt.close(figure)

    save_figure(png_path, vmin_dbm, vmax_dbm, debug=False)
    if debug_png_path is not None:
        finite_grid = rssi_grid[np.isfinite(rssi_grid)]
        prediction_min = float(finite_grid.min())
        prediction_max = float(finite_grid.max())
        if np.isclose(prediction_min, prediction_max):
            debug_min = prediction_min - 0.5
            debug_max = prediction_max + 0.5
        else:
            debug_min = prediction_min
            debug_max = prediction_max
        save_figure(debug_png_path, debug_min, debug_max, debug=True)

    return RadioMapResult(
        x_grid=x_grid,
        z_grid=z_grid,
        rssi_dbm=rssi_grid,
        height_m=height_m,
        png_path=png_path.resolve(),
        npz_path=npz_path.resolve(),
        debug_png_path=debug_png_path.resolve() if debug_png_path is not None else None,
    )
