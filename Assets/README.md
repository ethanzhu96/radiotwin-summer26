# RadioTwin Quest Data Collector

RadioTwin Quest Data Collector is a Unity/Meta Quest project for collecting, analyzing, and visualizing Wi-Fi radio measurements in mixed reality. The app records headset pose and Android Wi-Fi telemetry on-device, stores the result as CSV, and can render the collected samples back into the room as either a 3D RSSI point cloud or a floor heatmap.

The repository also includes Python utilities and example datasets for cleaning raw Quest logs, plotting RF trajectories, and measuring the effective RSSI update rate exposed by Android Wi-Fi APIs.

## Project Status

This is a research/prototyping project. Most system boundaries are file-based: Unity scripts write CSV logs into the Quest app's persistent data directory, those logs can be pulled to `data/raw`, Python scripts transform them into `data/processed`, and Unity visualization scenes can load compatible CSV files back from the persistent data directory.

## Repository Layout

```text
Assets/
  Scripts/                         Unity runtime scripts
  Plugins/Android/                 Android manifest and permission library
  Scenes/Phase2_TelemetryLogger.unity
  ExportedMeshes/quest_room_mesh.obj
  RssiInstanceColorShader.shader
data/
  raw/                             Raw Quest RF trajectory logs
  raw/benchmark_logs/              Benchmark CSV/JSONL captures
  processed/                       Cleaned analysis-ready CSV files
outputs/
  figures/                         Generated plots
Packages/
  manifest.json                    Unity package dependencies
ProjectSettings/                   Unity project/build/XR settings
scripts/
  parse_wifi_pose_log.py           Clean raw Quest CSV into processed samples
  plot_rf_samples.py               Plot cleaned samples as a 3D trajectory
  analyze_rssi_update_rate.py      Analyze polling/update behavior in logs
```

## Requirements

- Unity `6000.4.10f1`
- Android build support for Unity
- Meta Quest headset with developer mode enabled
- Meta XR SDK Core `201.0.0`
- Meta XR MR Utility Kit `201.0.0`
- Oculus XR Plugin `4.5.4`
- Python 3 with:
  - `pandas`
  - `matplotlib`

The Unity dependencies are declared in `Packages/manifest.json`. Python dependencies are not currently pinned in a requirements file.

## Unity Project Configuration

Important project settings:

- Product name: `RadioTwin_Quest_DataCollector`
- Android application identifier: `com.DefaultCompany.RadioTwin_Quest_DataCollector`
- Bundle version: `0.1`
- Android min SDK: `32`
- Android target SDK: `34`
- Android target architecture: ARM64
- Enabled build scene: `Assets/Scenes/Phase2_TelemetryLogger.unity`

The project includes both Oculus/OpenXR settings and Meta XR/MRUK assets. The checked-in Oculus settings currently target Quest/Quest 2 compatibility; verify target device flags before release builds if deploying specifically to Quest 3 or Quest 3S.

## System Architecture

At a high level, the project has four layers:

1. Mixed-reality runtime
   - Unity runs on Quest.
   - XR tracking supplies headset position and orientation through a configured `centerEyeAnchor` transform.
   - MR Utility Kit can load room/scene understanding data for diagnostics or mesh export.

2. Telemetry capture
   - `WifiPoseLogger` samples the headset pose and Android Wi-Fi connection information at a fixed interval.
   - `PoseLogger` provides a simpler pose-only logger.
   - Logs are written as CSV files under `Application.persistentDataPath` on the device.

3. Visualization
   - `RssiPointCloudRenderer` reads an RF CSV and draws colored 3D point instances at sampled positions.
   - `RssiFloorHeatmap` bins RF samples into X/Z floor cells and creates colored floor tiles.
   - `RssiInstanceColorShader` supplies per-instance colors for GPU-instanced point rendering.

4. Offline analysis
   - `scripts/parse_wifi_pose_log.py` converts raw Quest logs into a smaller normalized schema.
   - `scripts/plot_rf_samples.py` creates a 3D RSSI trajectory plot.
   - `scripts/analyze_rssi_update_rate.py` estimates how often RSSI values actually change compared with the polling rate.

```text
Quest headset tracking + Android Wi-Fi APIs
                |
                v
        WifiPoseLogger.cs
                |
                v
Application.persistentDataPath/*.csv
                |
      adb pull / manual copy
                |
                v
        data/raw/*.csv
                |
                v
scripts/parse_wifi_pose_log.py
                |
                v
data/processed/rf_samples_clean.csv
                |
        +-------+--------+
        |                |
        v                v
plot_rf_samples.py   Unity visualizers
        |                |
        v                v
outputs/figures/     MR point cloud / heatmap
```

## Runtime Data Flow

### Capture Path

`WifiPoseLogger` is the main capture component. On start, it creates a CSV file in Unity's persistent data directory and writes a header. On every `sampleIntervalSeconds`, it records:

- UTC Unix timestamp in milliseconds
- headset world position
- headset world rotation quaternion
- optional anchor transform position and rotation
- SSID
- BSSID
- RSSI in dBm
- Wi-Fi frequency in MHz
- link speed in Mbps

On Android builds, Wi-Fi information is retrieved through `AndroidJavaObject` calls to Android's `WifiManager.getConnectionInfo()`.

Location permissions are requested at runtime because Android gates access to Wi-Fi identifiers such as SSID and BSSID behind location permission. The Android manifests also declare Wi-Fi and location permissions.

### Offline Processing Path

Raw files from the Quest can be copied into `data/raw`. The parser script expects `data/raw/rf_trajectory_log.csv`, validates the raw capture schema, selects useful columns, renames pose columns to `x`, `y`, and `z`, coerces numeric values, and writes `data/processed/rf_samples_clean.csv`.

### Visualization Path

Unity visualization scripts expect a CSV in `Application.persistentDataPath`, typically named `rf_trajectory_log.csv`. They support both:

- raw capture columns: `world_pos_x`, `world_pos_y`, `world_pos_z`, `rssi_dbm`
- simplified columns: `pos_x`, `pos_y`, `pos_z`, `rssi_dbm`

The Python plotter expects the processed schema in `data/processed/rf_samples_clean.csv`.

## Unity APIs

The runtime API surface is primarily MonoBehaviour public fields plus CSV file contracts.

### `WifiPoseLogger`

File: `Assets/Scripts/WifiPoseLogger.cs`

Captures Quest headset pose and Android Wi-Fi state into a CSV log.

Public fields:

| Field | Type | Default | Description |
| --- | --- | --- | --- |
| `centerEyeAnchor` | `Transform` | none | Required. The headset/camera transform whose pose is logged. |
| `anchorTransform` | `Transform` | none | Optional. A reference anchor transform logged alongside headset pose. |
| `fileName` | `string` | `iperf_1hz.csv` | Output CSV file name under `Application.persistentDataPath`. |
| `sampleIntervalSeconds` | `float` | `1.0` | Capture period in seconds, using unscaled Unity time. |

Output columns:

```text
timestamp_unix_ms,
world_pos_x,world_pos_y,world_pos_z,
world_rot_x,world_rot_y,world_rot_z,world_rot_w,
anchor_pos_x,anchor_pos_y,anchor_pos_z,
anchor_rot_x,anchor_rot_y,anchor_rot_z,anchor_rot_w,
ssid,bssid,rssi_dbm,frequency_mhz,link_speed_mbps
```

Notes:

- In the Unity Editor, Wi-Fi values remain placeholders because Android Wi-Fi access is compiled only for Android player builds.
- Missing Wi-Fi data uses sentinel values such as `UNKNOWN`, `-999`, or `-1`.
- The script overwrites the target file on `Start()`.

### `PoseLogger`

File: `Assets/Scripts/PoseLogger.cs`

Captures only headset pose at a higher default rate.

Public fields:

| Field | Type | Default | Description |
| --- | --- | --- | --- |
| `centerEyeAnchor` | `Transform` | none | Required headset/camera transform. |
| `fileName` | `string` | `pose_log.csv` | Output CSV file under `Application.persistentDataPath`. |
| `sampleIntervalSeconds` | `float` | `0.1` | Capture period in seconds. |

Output columns:

```text
timestamp_unix_ms,pos_x,pos_y,pos_z,rot_x,rot_y,rot_z,rot_w
```

### `RssiPointCloudRenderer`

File: `Assets/Scripts/RssiPointCloudRenderer.cs`

Loads RF samples and renders each sample as a GPU-instanced mesh with RSSI-based color.

Public fields:

| Field | Type | Default | Description |
| --- | --- | --- | --- |
| `fileName` | `string` | `rf_trajectory_log.csv` | Input CSV file under `Application.persistentDataPath`. |
| `pointMesh` | `Mesh` | none | Mesh used for each point, commonly a sphere. |
| `pointMaterial` | `Material` | none | Instanced material using `Custom/RssiInstancedColor`. |
| `pointScale` | `float` | `0.25` | Uniform scale applied to every point instance. |
| `spawnDebugObjects` | `bool` | `true` | Creates regular Unity spheres for the first rows to help debug placement. |
| `maxDebugObjects` | `int` | `5` | Maximum debug spheres. |
| `minRssi` | `float` | `-90` | Lower RSSI normalization bound. |
| `maxRssi` | `float` | `-30` | Upper RSSI normalization bound. |

Accepted input columns:

```text
pos_x,pos_y,pos_z,rssi_dbm
```

or:

```text
world_pos_x,world_pos_y,world_pos_z,rssi_dbm
```

Rendering details:

- Draws in batches of `1023` instances, matching Unity's `Graphics.DrawMeshInstanced` limit.
- Maps weak-to-strong RSSI with a blue, cyan, green, yellow, red ramp.
- Uses a `MaterialPropertyBlock` and per-instance `_Color` values.

### `RssiFloorHeatmap`

File: `Assets/Scripts/RssiFloorHeatmap.cs`

Loads RF samples, bins them into X/Z grid cells, averages RSSI per cell, and creates colored floor quads.

Public fields:

| Field | Type | Default | Description |
| --- | --- | --- | --- |
| `fileName` | `string` | `rf_trajectory_log.csv` | Input CSV file under `Application.persistentDataPath`. |
| `cellSize` | `float` | `0.5` | Width/depth of each grid cell in Unity world units. |
| `floorY` | `float` | `0.01` | Height at which heatmap quads are placed. |
| `minRssi` | `float` | `-70` | Lower RSSI normalization bound. |
| `maxRssi` | `float` | `-40` | Upper RSSI normalization bound. |
| `tileMaterial` | `Material` | none | Optional base material cloned for each tile. |
| `clearOldTilesOnStart` | `bool` | `true` | Removes existing child tiles before generating new ones. |

Accepted input columns:

```text
pos_x,pos_z,rssi_dbm
```

or:

```text
world_pos_x,world_pos_z,rssi_dbm
```

### `MRUKDiagnostic`

File: `Assets/Scripts/MRUKDiagnostic.cs`

Debug helper that waits for MRUK scene data, checks `MRUK.Instance`, reports the current room, and logs discovered `MRUKAnchor` and `MeshFilter` objects.

Use this when validating that the headset has room setup/scene data available and that MRUK is successfully loading it.

### `MRUKMeshExporter`

File: `Assets/Scripts/MRUK_mesh_export.cs`

Exports all scene `MeshFilter` geometry to an OBJ file after a delay.

Public fields:

| Field | Type | Default | Description |
| --- | --- | --- | --- |
| `fileName` | `string` | `quest_room_mesh.obj` | Output OBJ file under `Application.persistentDataPath`. |
| `exportDelaySeconds` | `float` | `5` | Delay before collecting meshes and writing the OBJ. |

Output:

```text
Application.persistentDataPath/quest_room_mesh.obj
```

The repository includes an example exported mesh at `Assets/ExportedMeshes/quest_room_mesh.obj`.

## Android Permission Layer

Android permissions are declared in two places:

- `Assets/Plugins/Android/LauncherManifest.xml`
- `Assets/Plugins/Android/wifi_permissions.androidlib/AndroidManifest.xml`

Declared permissions:

```xml
<uses-permission android:name="android.permission.ACCESS_WIFI_STATE" />
<uses-permission android:name="android.permission.CHANGE_WIFI_STATE" />
<uses-permission android:name="android.permission.ACCESS_FINE_LOCATION" />
<uses-permission android:name="android.permission.ACCESS_COARSE_LOCATION" />
```

`WifiPoseLogger` also requests fine and coarse location permissions at runtime on Android builds.

## Data Schemas

### Raw RF Trajectory CSV

Produced by `WifiPoseLogger` and expected at `data/raw/rf_trajectory_log.csv` for offline parsing.

| Column | Type | Description |
| --- | --- | --- |
| `timestamp_unix_ms` | integer | UTC Unix timestamp in milliseconds. |
| `world_pos_x` | float | Headset world X position. |
| `world_pos_y` | float | Headset world Y position. |
| `world_pos_z` | float | Headset world Z position. |
| `world_rot_x` | float | Headset rotation quaternion X. |
| `world_rot_y` | float | Headset rotation quaternion Y. |
| `world_rot_z` | float | Headset rotation quaternion Z. |
| `world_rot_w` | float | Headset rotation quaternion W. |
| `anchor_pos_x` | float | Optional anchor world X position. |
| `anchor_pos_y` | float | Optional anchor world Y position. |
| `anchor_pos_z` | float | Optional anchor world Z position. |
| `anchor_rot_x` | float | Optional anchor rotation quaternion X. |
| `anchor_rot_y` | float | Optional anchor rotation quaternion Y. |
| `anchor_rot_z` | float | Optional anchor rotation quaternion Z. |
| `anchor_rot_w` | float | Optional anchor rotation quaternion W. |
| `ssid` | string | Connected Wi-Fi SSID. |
| `bssid` | string | Connected access point BSSID. |
| `rssi_dbm` | integer | Received signal strength indicator in dBm. |
| `frequency_mhz` | integer | Wi-Fi channel frequency in MHz. |
| `link_speed_mbps` | integer | Android-reported link speed in Mbps. |

### Processed RF Samples CSV

Produced by `scripts/parse_wifi_pose_log.py` at `data/processed/rf_samples_clean.csv`.

| Column | Type | Description |
| --- | --- | --- |
| `timestamp_unix_ms` | integer | UTC Unix timestamp in milliseconds. |
| `x` | float | Headset world X position. |
| `y` | float | Headset world Y position. |
| `z` | float | Headset world Z position. |
| `rssi_dbm` | integer/float | RSSI in dBm. |
| `ssid` | string | Connected SSID. |
| `bssid` | string | Connected BSSID. |
| `frequency_mhz` | integer/float | Wi-Fi frequency. |
| `link_speed_mbps` | integer/float | Link speed. |

### Benchmark Logs

`data/raw/benchmark_logs` contains captured experiments:

- `rf_trajectory_log.csv`: Quest Wi-Fi/pose samples.
- `rssi_near_far_test.csv`: RF samples for near/far signal testing.
- `rf_with_iperf.csv`: RF samples associated with an iperf run.
- `iperf_1hz.csv`: iperf event records stored as CSV.
- `iperf_1hz.jsonl`: iperf event records stored as JSON lines.

`analyze_rssi_update_rate.py` operates on CSV files in this directory that include `timestamp_unix_ms` and `rssi_dbm`.

## Python Utilities

Run scripts from the repository root.

### Clean Raw Quest Logs

```powershell
python scripts\parse_wifi_pose_log.py
```

Input:

```text
data/raw/rf_trajectory_log.csv
```

Output:

```text
data/processed/rf_samples_clean.csv
```

### Plot RF Samples

```powershell
python scripts\plot_rf_samples.py
```

Input:

```text
data/processed/rf_samples_clean.csv
```

Output:

```text
outputs/figures/rf_trajectory_3d.png
```

The plot uses:

- X axis: Unity world X
- Y axis: Unity world Z, shown as room depth
- Z axis: Unity world Y, shown as height
- Color: `rssi_dbm`

### Analyze RSSI Update Rate

```powershell
python scripts\analyze_rssi_update_rate.py
```

Input:

```text
data/raw/benchmark_logs/*.csv
```

For each compatible CSV, the script reports:

- total rows
- capture duration
- observed polling rate
- number of RSSI changes
- number of unique RSSI values
- RSSI range and mean
- repeated/stale-looking fraction
- mean/median time between RSSI changes
- effective RSSI update rate

## Common Workflows

### Build and Run on Quest

1. Open the project in Unity `6000.4.10f1`.
2. Install/resolve packages through Unity Package Manager.
3. Open `Assets/Scenes/Phase2_TelemetryLogger.unity`.
4. Confirm the scene has a `WifiPoseLogger` component with `centerEyeAnchor` assigned.
5. Switch build target to Android.
6. Build and run to the Quest headset.
7. Grant location permissions when prompted.
8. Walk through the target space while connected to the Wi-Fi network being measured.

The app writes logs to Unity's persistent data path for the Android package:

```text
/sdcard/Android/data/com.DefaultCompany.RadioTwin_Quest_DataCollector/files/
```

Example ADB pull command:

```powershell
adb pull /sdcard/Android/data/com.DefaultCompany.RadioTwin_Quest_DataCollector/files/rf_trajectory_log.csv data\raw\rf_trajectory_log.csv
```

If the scene uses the default `WifiPoseLogger.fileName` value, pull `iperf_1hz.csv` instead or rename the field in the Inspector before building.

### Process and Plot a Capture

```powershell
python scripts\parse_wifi_pose_log.py
python scripts\plot_rf_samples.py
```

Open the generated figure:

```text
outputs/figures/rf_trajectory_3d.png
```

### Visualize a Capture in Quest

1. Copy a compatible RF CSV to the app persistent data directory on the Quest.
2. Name it to match the visualizer's `fileName` field, usually `rf_trajectory_log.csv`.
3. Open or build a scene containing `RssiPointCloudRenderer` or `RssiFloorHeatmap`.
4. Assign required materials/meshes in the Inspector.
5. Run the scene.

Example ADB push:

```powershell
adb push data\raw\rf_trajectory_log.csv /sdcard/Android/data/com.DefaultCompany.RadioTwin_Quest_DataCollector/files/rf_trajectory_log.csv
```

## Implementation Notes

- `WifiPoseLogger` uses `Time.unscaledDeltaTime`, so capture timing is independent of Unity time scale.
- `PoseLogger` uses `Time.deltaTime`.
- Both loggers overwrite their output file when the component starts.
- CSV parsing in Unity currently uses simple comma splitting. This is acceptable for the current numeric-heavy schemas, but quoted strings containing commas would require a true CSV parser.
- RSSI color normalization is configurable separately for point cloud and heatmap renderers.
- `RssiPointCloudRenderer` allocates batch arrays each frame. For very large captures, caching batches would reduce per-frame allocations.
- `RssiFloorHeatmap` creates one material instance per tile so each tile can have an independent color.

## Known Limitations

- Wi-Fi telemetry only works on Android device builds, not in the Unity Editor.
- Android's reported RSSI may update more slowly than the script polls it; use `analyze_rssi_update_rate.py` to quantify this for a capture.
- The default Android package name still uses `DefaultCompany`.
- Python dependencies are not pinned.
- There is no automated Unity test suite or CI configuration in this repository.

## Key Files

- `Assets/Scripts/WifiPoseLogger.cs`: main Wi-Fi plus pose logger.
- `Assets/Scripts/RssiPointCloudRenderer.cs`: 3D RSSI point cloud renderer.
- `Assets/Scripts/RssiFloorHeatmap.cs`: floor-projected RSSI heatmap renderer.
- `Assets/Scripts/MRUKDiagnostic.cs`: MRUK scene loading diagnostics.
- `Assets/Scripts/MRUK_mesh_export.cs`: OBJ room mesh exporter.
- `Assets/RssiInstanceColorShader.shader`: instanced per-point color shader.
- `scripts/parse_wifi_pose_log.py`: raw-to-processed RF CSV transform.
- `scripts/plot_rf_samples.py`: 3D RF trajectory plot.
- `scripts/analyze_rssi_update_rate.py`: RSSI update-rate analysis.
