from dataclasses import dataclass, field
from pathlib import Path
from typing import Any
import json
import warnings

import numpy as np
import pandas as pd


@dataclass
class Dataset:
    positions: np.ndarray
    rssi_dbm: np.ndarray
    timestamps: np.ndarray | None
    metadata: dict[str, Any] = field(default_factory=dict)

    @property
    def sample_count(self) -> int:
        return int(self.rssi_dbm.shape[0])


@dataclass
class DatasetSplit:
    train_positions: np.ndarray
    train_rssi: np.ndarray
    test_positions: np.ndarray
    test_rssi: np.ndarray
    train_indices: np.ndarray
    test_indices: np.ndarray
    test_start: int
    test_end: int


@dataclass(frozen=True)
class TxAnchorMetadata:
    position: np.ndarray
    room_uuid: str | None


def load_tx_anchor(path: str | Path) -> TxAnchorMetadata:
    tx_path = Path(path)
    message = (
        "Simplified RT requires a valid TX anchor position. Place the router "
        "anchor and record/export TX metadata before running RT."
    )
    if not tx_path.is_file():
        raise FileNotFoundError(f"{message} Missing file: {tx_path}")
    try:
        with tx_path.open("r", encoding="utf-8") as stream:
            payload = json.load(stream)
        position = np.asarray([payload[axis] for axis in ("x", "y", "z")], dtype=float)
    except (OSError, json.JSONDecodeError, KeyError, TypeError, ValueError) as error:
        raise ValueError(f"{message} Invalid file {tx_path}: {error}") from error
    if position.shape != (3,) or not np.all(np.isfinite(position)):
        raise ValueError(f"{message} TX coordinates must be finite numeric x/y/z values.")
    room_uuid = payload.get("room_uuid")
    return TxAnchorMetadata(
        position=position,
        room_uuid=str(room_uuid).strip() if room_uuid else None,
    )


def _position_columns(columns: pd.Index) -> tuple[str, str, str]:
    canonical = ("x", "y", "z")
    quest = (
        "reference_local_pos_x",
        "reference_local_pos_y",
        "reference_local_pos_z",
    )
    if all(column in columns for column in canonical):
        return canonical
    if all(column in columns for column in quest):
        return quest
    raise ValueError(
        "RF CSV is missing position columns. Expected x/y/z or the current Quest "
        "reference_local_pos_x/reference_local_pos_y/reference_local_pos_z columns."
    )


def load_rf_csv(path: str | Path, target_bssid: str | None = None) -> Dataset:
    csv_path = Path(path)
    if not csv_path.is_file():
        raise FileNotFoundError(f"RF CSV does not exist: {csv_path}")

    frame = pd.read_csv(csv_path)
    if "rssi_dbm" not in frame.columns:
        raise ValueError("RF CSV is missing required column: rssi_dbm")

    position_columns = _position_columns(frame.columns)
    required = [*position_columns, "rssi_dbm"]
    input_rows = len(frame)

    for column in required:
        frame[column] = pd.to_numeric(frame[column], errors="coerce")
    frame = frame.dropna(subset=required).copy()
    invalid_rows_dropped = input_rows - len(frame)

    if "timestamp_unix_ms" in frame.columns:
        frame["timestamp_unix_ms"] = pd.to_numeric(
            frame["timestamp_unix_ms"], errors="coerce"
        )
        invalid_timestamps = int(frame["timestamp_unix_ms"].isna().sum())
        if invalid_timestamps:
            warnings.warn(
                f"{invalid_timestamps} rows have invalid timestamps; their original "
                "relative order is retained after timestamped rows.",
                stacklevel=2,
            )
        frame = frame.sort_values(
            "timestamp_unix_ms", kind="stable", na_position="last"
        )

    if target_bssid:
        if "bssid" not in frame.columns:
            raise ValueError("target_bssid is configured, but the CSV has no bssid column.")
        normalized_target = target_bssid.strip().casefold()
        frame = frame[
            frame["bssid"].astype(str).str.strip().str.casefold() == normalized_target
        ].copy()
        if frame.empty:
            raise ValueError(f"No usable rows matched target_bssid={target_bssid!r}.")

    bssids: list[str] = []
    if "bssid" in frame.columns:
        bssids = sorted(
            value
            for value in frame["bssid"].dropna().astype(str).str.strip().unique()
            if value
        )
        if not target_bssid and len(bssids) > 1:
            warnings.warn(
                "Multiple BSSIDs are present without target_bssid filtering; mixing APs "
                "invalidates an RSSI predictor benchmark.",
                stacklevel=2,
            )

    if frame.empty:
        raise ValueError("No usable RF samples remain after cleaning and filtering.")

    timestamps = (
        frame["timestamp_unix_ms"].to_numpy(copy=True)
        if "timestamp_unix_ms" in frame.columns
        else None
    )
    metadata: dict[str, Any] = {
        "path": str(csv_path.resolve()),
        "input_rows": input_rows,
        "invalid_rows_dropped": invalid_rows_dropped,
        "position_columns": position_columns,
        "bssids": bssids,
    }
    for optional_column in ("ssid", "frequency_mhz"):
        if optional_column in frame.columns:
            metadata[optional_column] = sorted(
                frame[optional_column].dropna().astype(str).unique().tolist()
            )

    return Dataset(
        positions=frame.loc[:, position_columns].to_numpy(dtype=float, copy=True),
        rssi_dbm=frame["rssi_dbm"].to_numpy(dtype=float, copy=True),
        timestamps=timestamps,
        metadata=metadata,
    )


def contiguous_segment_split(
    dataset: Dataset,
    segment_start_fraction: float,
    test_fraction: float,
) -> DatasetSplit:
    if not 0.0 <= segment_start_fraction < 1.0:
        raise ValueError("segment_start_fraction must be in [0, 1).")
    if not 0.0 < test_fraction < 1.0:
        raise ValueError("test_fraction must be in (0, 1).")
    if segment_start_fraction + test_fraction > 1.0:
        raise ValueError("The held-out segment extends beyond the end of the trajectory.")

    sample_count = dataset.sample_count
    test_start = int(np.floor(sample_count * segment_start_fraction))
    test_length = int(np.floor(sample_count * test_fraction))
    test_end = test_start + test_length
    if test_length < 2:
        raise ValueError(
            f"The held-out segment has {test_length} samples; at least 2 are required."
        )

    all_indices = np.arange(sample_count, dtype=int)
    test_indices = all_indices[test_start:test_end]
    train_indices = np.concatenate(
        (all_indices[:test_start], all_indices[test_end:])
    )
    if train_indices.size < 2:
        raise ValueError(
            f"The training set has {train_indices.size} samples; at least 2 are required."
        )

    return DatasetSplit(
        train_positions=dataset.positions[train_indices],
        train_rssi=dataset.rssi_dbm[train_indices],
        test_positions=dataset.positions[test_indices],
        test_rssi=dataset.rssi_dbm[test_indices],
        train_indices=train_indices,
        test_indices=test_indices,
        test_start=test_start,
        test_end=test_end,
    )
