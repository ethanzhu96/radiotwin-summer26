from pathlib import Path
import json
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

    from radiobench.data import contiguous_segment_split, load_rf_csv, load_tx_anchor
    from radiobench.mesh import SceneMesh
    from radiobench.metrics import (
        calculate_covered_metrics,
        calculate_metrics,
        format_metrics,
    )
    from radiobench.models.gp import GaussianProcessRadioModel
    from radiobench.models.simple_rt import SimpleRTModel, save_los_diagnostic
    from radiobench.quest_sync import sync_file_from_quest
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


def print_bounds(label: str, bounds: np.ndarray) -> None:
    print(label)
    for index, axis in enumerate("xyz"):
        print(f"  {axis}: {bounds[0, index]:.3f} -> {bounds[1, index]:.3f} m")


def warn_if_geometry_misaligned(tx: np.ndarray, rx: np.ndarray, mesh_bounds: np.ndarray) -> None:
    extent = np.maximum(mesh_bounds[1] - mesh_bounds[0], 1.0)
    margin = np.maximum(extent * 0.25, 1.0)
    lower = mesh_bounds[0] - margin
    upper = mesh_bounds[1] + margin
    if np.any(tx < lower) or np.any(tx > upper):
        warnings.warn(
            "TX is wildly outside the mesh bounds; Simplified RT results may have a "
            "coordinate-frame or stale-export mismatch.",
            stacklevel=2,
        )
    outside_rx = np.any((rx < lower) | (rx > upper), axis=1)
    if np.any(outside_rx):
        warnings.warn(
            f"{int(outside_rx.sum())} RX samples are wildly outside the mesh bounds; "
            "Simplified RT alignment may be invalid.",
            stacklevel=2,
        )


def load_metadata_room_uuid(path: Path) -> str | None:
    if not path.is_file():
        return None
    with path.open("r", encoding="utf-8") as stream:
        payload = json.load(stream)
    room_uuid = payload.get("roomUuid")
    return str(room_uuid).strip() if room_uuid else None


def print_comparison(gp_metrics, simple_metrics) -> None:
    print("\n" + "=" * 86)
    print("MODEL COMPARISON - TEST SET")
    print("=" * 86)
    print("Model                     RMSE    MAE    Bias   MaxErr      r   Coverage")
    print("-" * 86)
    print(
        f"{'Gaussian Process':25} {gp_metrics.rmse:6.2f} {gp_metrics.mae:6.2f} "
        f"{gp_metrics.bias:7.2f} {gp_metrics.max_abs_error:8.2f} "
        f"{gp_metrics.pearson_r:6.3f}   100.0%"
    )
    metrics = simple_metrics.metrics
    print(
        f"{'Simple RT (LOS-only)':25} {metrics.rmse:6.2f} {metrics.mae:6.2f} "
        f"{metrics.bias:7.2f} {metrics.max_abs_error:8.2f} "
        f"{metrics.pearson_r:6.3f} {simple_metrics.coverage * 100:7.1f}%"
    )
    print("Simple RT metrics are on valid LOS-covered test samples only.")
    print("=" * 86)


def main() -> int:
    config = load_config()
    data_config = config.get("data", {})
    split_config = config.get("split", {})
    gp_config = config.get("gp", {})
    simple_rt_config = config.get("simple_rt", {})
    sync_config = config.get("quest_sync", {})
    radiomap_config = config.get("radiomap", {})
    if not data_config.get("rf_csv"):
        raise ValueError("config.yaml must define data.rf_csv.")
    if split_config.get("mode", "segment") != "segment":
        raise ValueError("Only split.mode='segment' is supported in the GP benchmark.")

    rf_csv_path = resolve_data_path(data_config["rf_csv"])
    tx_anchor_path = resolve_data_path(data_config.get("tx_anchor_json", "tx_anchor.json"))
    mesh_path = resolve_data_path(data_config.get("room_mesh_obj", "quest_room_mesh.obj"))
    metadata_path = resolve_data_path(
        data_config.get("dataset_metadata_json", "rf_dataset_metadata.json")
    )
    if sync_config.get("enabled", False):
        adb_path = sync_config.get("adb_path")
        package_name = sync_config.get("package_name")
        if not adb_path or not package_name:
            raise ValueError(
                "quest_sync.enabled requires quest_sync.adb_path and package_name."
            )
        sync_targets = [("rf_trajectory_log.csv", rf_csv_path)]
        if simple_rt_config.get("enabled", True):
            sync_targets.extend(
                [
                    ("tx_anchor.json", tx_anchor_path),
                    ("quest_room_mesh.obj", mesh_path),
                    ("rf_dataset_metadata.json", metadata_path),
                ]
            )
        print("Syncing latest benchmark exports from Quest...")
        for file_name, destination in sync_targets:
            sync_file_from_quest(adb_path, package_name, file_name, destination)
            print(f"  Updated: {destination}")

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

    simple_model = None
    simple_test_metrics = None
    simple_all_los = None
    diagnostic_path = None
    if simple_rt_config.get("enabled", True):
        tx_metadata = load_tx_anchor(tx_anchor_path)
        metadata_room_uuid = load_metadata_room_uuid(metadata_path)
        if (
            tx_metadata.room_uuid
            and metadata_room_uuid
            and tx_metadata.room_uuid.casefold() != metadata_room_uuid.casefold()
        ):
            raise ValueError(
                "TX room UUID does not match RF dataset metadata room UUID. "
                "Re-export a consistent TX/trajectory/mesh dataset."
            )
        scene_mesh = SceneMesh(
            mesh_path,
            ray_epsilon_m=float(simple_rt_config.get("ray_epsilon_m", 0.01)),
        )
        tx = tx_metadata.position
        rx_bounds = np.vstack((dataset.positions.min(axis=0), dataset.positions.max(axis=0)))
        print("\nSimplified RT coordinate diagnostics:")
        print(f"TX room-local: x={tx[0]:.3f}, y={tx[1]:.3f}, z={tx[2]:.3f} m")
        print_bounds("RX bounds:", rx_bounds)
        print_bounds("Mesh bounds:", scene_mesh.bounds)
        print(
            f"Mesh geometry: {scene_mesh.vertex_count:,} vertices, "
            f"{scene_mesh.face_count:,} triangles"
        )
        print(
            f"OBJ objects: {scene_mesh.included_object_count} room EffectMesh included, "
            f"{scene_mesh.excluded_object_count} non-room visualization objects excluded"
        )
        warn_if_geometry_misaligned(tx, dataset.positions, scene_mesh.bounds)

        simple_model = SimpleRTModel(simple_rt_config)
        simple_model.fit(
            split.train_positions,
            split.train_rssi,
            ctx={"mesh": scene_mesh, "tx": tx, "cfg": simple_rt_config},
        )
        simple_train_predictions = simple_model.predict(split.train_positions)
        simple_test_predictions = simple_model.predict(split.test_positions)
        simple_train_metrics = calculate_covered_metrics(
            split.train_rssi, simple_train_predictions
        )
        simple_test_metrics = calculate_covered_metrics(
            split.test_rssi, simple_test_predictions
        )
        train_los = np.isfinite(simple_train_predictions)
        test_los = np.isfinite(simple_test_predictions)
        simple_all_los = simple_model.los_mask(dataset.positions)
        distances = simple_model.distances(dataset.positions)
        print("\nSimple RT LOS-only calibration:")
        print(f"  path-loss exponent n: {simple_model.path_loss_exponent:.3f}")
        print(f"  TX/global offset:     {simple_model.tx_offset_dbm:.3f} dBm")
        print(f"  LOS train samples:    {int(train_los.sum())}")
        print(f"  blocked train:        {int((~train_los).sum())}")
        print("\nLOS diagnostics:")
        print(f"  total RX samples:     {dataset.sample_count}")
        print(f"  LOS:                  {int(simple_all_los.sum())}")
        print(f"  blocked:              {int((~simple_all_los).sum())}")
        print(f"  LOS fraction:         {simple_all_los.mean() * 100:.1f}%")
        print(f"  train LOS/blocked:    {int(train_los.sum())}/{int((~train_los).sum())}")
        print(f"  test LOS/blocked:     {int(test_los.sum())}/{int((~test_los).sum())}")
        print(f"  test coverage:        {simple_test_metrics.coverage * 100:.1f}%")
        print(
            "  TX-RX distance m:     "
            f"min={distances.min():.3f}, median={np.median(distances):.3f}, "
            f"max={distances.max():.3f}"
        )
        if simple_all_los.sum() == 0:
            warnings.warn("Simple RT found 0% LOS; check mesh/TX/RX alignment.", stacklevel=2)
        elif simple_all_los.all():
            warnings.warn(
                "Simple RT found 100% LOS; verify this is plausible for the room and mesh.",
                stacklevel=2,
            )
        diagnostic_path = save_los_diagnostic(
            resolve_data_path("benchmark/outputs/simple_rt_los_diagnostic.png"),
            dataset.positions,
            simple_all_los,
            tx,
        )
        print(f"  diagnostic PNG:       {diagnostic_path}")
        print_comparison(test_metrics, simple_test_metrics)

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

    if simple_model is not None:
        simple_map_result = generate_radio_map(
            model=simple_model,
            measurement_positions=dataset.positions,
            train_indices=split.train_indices,
            test_indices=split.test_indices,
            height_m=map_height,
            grid_resolution_m=float(radiomap_config.get("grid_resolution_m", 0.10)),
            bounds_padding_m=float(radiomap_config.get("bounds_padding_m", 0.20)),
            model_name=simple_model.name,
            output_dir=output_dir,
            vmin_dbm=float(radiomap_config.get("vmin_dbm", -90)),
            vmax_dbm=float(radiomap_config.get("vmax_dbm", -30)),
            save_debug_autoscaled=bool(
                radiomap_config.get("save_debug_autoscaled", False)
            ),
            allow_missing_predictions=True,
        )
        finite_map = simple_map_result.rssi_dbm[
            np.isfinite(simple_map_result.rssi_dbm)
        ]
        print("\nSimple RT LOS-only radio map:")
        print(f"  Height:       {simple_map_result.height_m:.2f} m")
        print(f"  Grid:         {simple_map_result.shape[1]} x {simple_map_result.shape[0]}")
        print(
            f"  LOS coverage: {finite_map.size}/{simple_map_result.rssi_dbm.size} "
            f"({finite_map.size / simple_map_result.rssi_dbm.size * 100:.1f}%)"
        )
        print(
            f"  Predicted:    {finite_map.min():.2f} to {finite_map.max():.2f} dBm"
        )
        print(f"  PNG:          {simple_map_result.png_path}")
        print(f"  NPZ:          {simple_map_result.npz_path}")
        if simple_map_result.debug_png_path is not None:
            print(f"  Debug PNG:    {simple_map_result.debug_png_path}")
    print("=" * 72)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (FileNotFoundError, RuntimeError, ValueError) as error:
        raise SystemExit(f"Benchmark configuration/data error: {error}") from error
