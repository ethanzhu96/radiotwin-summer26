from pathlib import Path
import sys
import warnings


def _dependency_error(error: ModuleNotFoundError) -> None:
    package_names = {
        "yaml": "PyYAML",
        "sklearn": "scikit-learn",
    }
    package = package_names.get(error.name or "", error.name or "unknown")
    raise SystemExit(
        f"Missing benchmark dependency: {package}. Install dependencies with:\n"
        f"  py -3 -m pip install -r benchmark/requirements.txt"
    ) from error


try:
    import numpy as np
    import yaml

    from radiobench.data import contiguous_segment_split, load_rf_csv
    from radiobench.metrics import calculate_metrics, format_metrics
    from radiobench.models.gp import GaussianProcessRadioModel
    from radiobench.quest_sync import sync_rf_csv_from_quest
    from radiobench.radiomap import generate_radio_map
except ModuleNotFoundError as error:
    _dependency_error(error)


BENCHMARK_DIR = Path(__file__).resolve().parent
PROJECT_ROOT = BENCHMARK_DIR.parent


def load_config() -> dict:
    config_path = BENCHMARK_DIR / "config.yaml"
    with config_path.open("r", encoding="utf-8") as stream:
        config = yaml.safe_load(stream)
    if not isinstance(config, dict):
        raise ValueError(f"Configuration must contain a YAML mapping: {config_path}")
    return config


def resolve_data_path(configured_path: str) -> Path:
    path = Path(configured_path).expanduser()
    return path if path.is_absolute() else PROJECT_ROOT / path


def print_sanity_warnings(dataset) -> None:
    print("\nWARNING: This dataset is intended for pipeline validation only.")
    print("Results should not be treated as scientific benchmark results.")
    if dataset.sample_count < 40:
        warnings.warn(
            f"Very small dataset: only {dataset.sample_count} usable samples (fewer than 40).",
            stacklevel=2,
        )
    if np.unique(dataset.rssi_dbm).size == 1:
        warnings.warn("All samples have the same RSSI value.", stacklevel=2)
    spans = np.ptp(dataset.positions, axis=0)
    narrow_axes = ["xyz"[index] for index, span in enumerate(spans) if span < 0.25]
    if narrow_axes:
        warnings.warn(
            "Very narrow spatial coverage on axis/axes: " + ", ".join(narrow_axes),
            stacklevel=2,
        )


def resolve_radiomap_height(dataset, radiomap_config: dict) -> tuple[float, float, float, float]:
    y_values = dataset.positions[:, 1]
    y_min = float(np.min(y_values))
    y_median = float(np.median(y_values))
    y_max = float(np.max(y_values))
    configured_height = radiomap_config.get("eval_height_m")
    mode = radiomap_config.get("eval_height_mode", "median_measurement")
    if configured_height is not None:
        chosen_height = float(configured_height)
    elif mode == "median_measurement":
        chosen_height = y_median
    else:
        raise ValueError(
            "radiomap.eval_height_mode must be 'median_measurement', or "
            "radiomap.eval_height_m must provide a manual height."
        )
    if not np.isfinite(chosen_height):
        raise ValueError("The chosen radio-map evaluation height is not finite.")
    if chosen_height < y_min - 0.2 or chosen_height > y_max + 0.2:
        warnings.warn(
            f"Requested evaluation height {chosen_height:.2f} m is outside the "
            f"observed measurement height range [{y_min:.2f}, {y_max:.2f}] m. "
            "This map is vertical extrapolation and should not be treated as "
            "quantitatively validated.",
            stacklevel=2,
        )
    return chosen_height, y_min, y_median, y_max


def main() -> int:
    config = load_config()
    data_config = config.get("data", {})
    split_config = config.get("split", {})
    gp_config = config.get("gp", {})
    sync_config = config.get("quest_sync", {})
    radiomap_config = config.get("radiomap", {})
    if not data_config.get("rf_csv"):
        raise ValueError("config.yaml must define data.rf_csv.")
    if split_config.get("mode", "segment") != "segment":
        raise ValueError("Only split.mode='segment' is supported in the GP benchmark.")

    rf_csv_path = resolve_data_path(data_config["rf_csv"])
    if sync_config.get("enabled", False):
        adb_path = sync_config.get("adb_path")
        package_name = sync_config.get("package_name")
        if not adb_path or not package_name:
            raise ValueError(
                "quest_sync.enabled requires quest_sync.adb_path and package_name."
            )
        print("Syncing latest RF trajectory from Quest...")
        sync_rf_csv_from_quest(adb_path, package_name, rf_csv_path)
        print(f"Updated local RF CSV: {rf_csv_path}")

    dataset = load_rf_csv(
        rf_csv_path,
        target_bssid=data_config.get("target_bssid"),
    )
    split = contiguous_segment_split(
        dataset,
        float(split_config.get("segment_start_fraction", 0.6)),
        float(split_config.get("test_fraction", 0.2)),
    )

    print("=" * 72)
    print("Radiotwin Offline Benchmark")
    print("=" * 72)
    print("\nDataset:")
    print(f"  Path:       {dataset.metadata['path']}")
    print(f"  Coordinates:{' ' + '/'.join(dataset.metadata['position_columns'])}")
    print(f"  Input rows: {dataset.metadata['input_rows']}")
    print(f"  Dropped:    {dataset.metadata['invalid_rows_dropped']}")
    print(f"  Samples:    {dataset.sample_count}")
    print(f"  Train:      {split.train_indices.size}")
    print(f"  Test:       {split.test_indices.size}")
    print(f"  Held-out contiguous segment: [{split.test_start}, {split.test_end})")
    print(f"  Held-out row indices: {split.test_indices.tolist()}")
    print(
        "  RSSI dBm:   "
        f"min={dataset.rssi_dbm.min():.1f}, max={dataset.rssi_dbm.max():.1f}, "
        f"mean={dataset.rssi_dbm.mean():.2f}"
    )
    if dataset.metadata.get("bssids"):
        print("  BSSID(s):   " + ", ".join(dataset.metadata["bssids"]))
    if dataset.metadata.get("frequency_mhz"):
        print("  Frequency:  " + ", ".join(dataset.metadata["frequency_mhz"]) + " MHz")
    print_sanity_warnings(dataset)

    model = GaussianProcessRadioModel(
        matern_nu=float(gp_config.get("matern_nu", 1.5)),
        n_restarts_optimizer=int(gp_config.get("n_restarts_optimizer", 5)),
        random_state=int(gp_config.get("random_state", 42)),
    )
    model.fit(split.train_positions, split.train_rssi)
    train_metrics = calculate_metrics(
        split.train_rssi, model.predict(split.train_positions)
    )
    test_metrics = calculate_metrics(
        split.test_rssi, model.predict(split.test_positions)
    )

    print(f"\nModel: {model.name}\n")
    print(format_metrics("TRAIN", train_metrics))
    print()
    print(format_metrics("TEST", test_metrics))
    print("\nLearned kernel:")
    print(model.learned_kernel)

    map_height, y_min, y_median, y_max = resolve_radiomap_height(
        dataset, radiomap_config
    )
    print("\nMeasurement height statistics:")
    print(f"  Samples:  {dataset.sample_count}")
    print(f"  min y:    {y_min:.3f} m")
    print(f"  median y: {y_median:.3f} m")
    print(f"  max y:    {y_max:.3f} m")
    print("\nPrimary radio-map evaluation height:")
    print(f"  y = {map_height:.3f} m")

    output_dir = resolve_data_path(
        radiomap_config.get("output_dir", "benchmark/outputs")
    )
    map_result = generate_radio_map(
        model=model,
        measurement_positions=dataset.positions,
        train_indices=split.train_indices,
        test_indices=split.test_indices,
        height_m=map_height,
        grid_resolution_m=float(
            radiomap_config.get("grid_resolution_m", 0.10)
        ),
        bounds_padding_m=float(radiomap_config.get("bounds_padding_m", 0.20)),
        model_name=model.name,
        output_dir=output_dir,
        vmin_dbm=float(radiomap_config.get("vmin_dbm", -90)),
        vmax_dbm=float(radiomap_config.get("vmax_dbm", -30)),
        save_debug_autoscaled=bool(
            radiomap_config.get("save_debug_autoscaled", False)
        ),
    )
    print("\nRadio-map bounds:")
    print(f"  x: {map_result.x_grid.min():.2f} -> {map_result.x_grid.max():.2f} m")
    print(f"  z: {map_result.z_grid.min():.2f} -> {map_result.z_grid.max():.2f} m")
    print(f"  y: {map_result.height_m:.2f} m")
    print("\nRadio map:")
    print(f"  Height:       {map_result.height_m:.2f} m")
    print(f"  Grid:         {map_result.shape[1]} x {map_result.shape[0]}")
    print(f"  Points:       {map_result.rssi_dbm.size}")
    prediction_min = float(map_result.rssi_dbm.min())
    prediction_max = float(map_result.rssi_dbm.max())
    prediction_mean = float(map_result.rssi_dbm.mean())
    print("  Predicted map RSSI:")
    print(f"    min:         {prediction_min:.2f} dBm")
    print(f"    max:         {prediction_max:.2f} dBm")
    print(f"    mean:        {prediction_mean:.2f} dBm")
    print(f"    range:       {prediction_max - prediction_min:.2f} dB")
    print(f"  PNG:          {map_result.png_path}")
    print(f"  NPZ:          {map_result.npz_path}")
    if map_result.debug_png_path is not None:
        print(f"  Debug PNG:    {map_result.debug_png_path}")
    print("=" * 72)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (FileNotFoundError, ValueError) as error:
        raise SystemExit(f"Benchmark configuration/data error: {error}") from error
