from pathlib import Path
import pandas as pd

LOG_DIR = Path("data/raw/benchmark_logs")

def analyze_file(path: Path):
    df = pd.read_csv(path)

    required = ["timestamp_unix_ms", "rssi_dbm"]
    missing = [c for c in required if c not in df.columns]
    if missing:
        print(f"\n{path.name}: missing columns {missing}")
        return

    df = df.dropna(subset=["timestamp_unix_ms", "rssi_dbm"]).copy()
    df["timestamp_unix_ms"] = pd.to_numeric(df["timestamp_unix_ms"], errors="coerce")
    df["rssi_dbm"] = pd.to_numeric(df["rssi_dbm"], errors="coerce")
    df = df.dropna(subset=["timestamp_unix_ms", "rssi_dbm"])

    if len(df) < 2:
        print(f"\n{path.name}: not enough data")
        return

    df = df.sort_values("timestamp_unix_ms")

    start_ms = df["timestamp_unix_ms"].iloc[0]
    end_ms = df["timestamp_unix_ms"].iloc[-1]
    duration_s = (end_ms - start_ms) / 1000.0

    total_rows = len(df)
    observed_poll_hz = total_rows / duration_s if duration_s > 0 else 0

    # True when RSSI differs from previous row
    df["rssi_changed"] = df["rssi_dbm"].ne(df["rssi_dbm"].shift())

    # Exclude first row because it always counts as "changed"
    change_rows = df[df["rssi_changed"]].copy()
    num_changes = max(len(change_rows) - 1, 0)

    unique_rssi = df["rssi_dbm"].nunique()
    min_rssi = df["rssi_dbm"].min()
    max_rssi = df["rssi_dbm"].max()
    mean_rssi = df["rssi_dbm"].mean()

    if len(change_rows) > 1:
        change_times = change_rows["timestamp_unix_ms"].to_numpy()
        gaps_s = (change_times[1:] - change_times[:-1]) / 1000.0
        mean_change_gap_s = gaps_s.mean()
        median_change_gap_s = pd.Series(gaps_s).median()
        effective_rssi_hz = 1.0 / mean_change_gap_s if mean_change_gap_s > 0 else 0
    else:
        mean_change_gap_s = None
        median_change_gap_s = None
        effective_rssi_hz = 0

    repeated_fraction = 1.0 - (num_changes / max(total_rows - 1, 1))

    print(f"\n=== {path.name} ===")
    print(f"Rows: {total_rows}")
    print(f"Duration: {duration_s:.2f} sec")
    print(f"Observed polling rate: {observed_poll_hz:.2f} Hz")
    print(f"RSSI changes: {num_changes}")
    print(f"Unique RSSI values: {unique_rssi}")
    print(f"RSSI range: {min_rssi:.0f} to {max_rssi:.0f} dBm")
    print(f"Mean RSSI: {mean_rssi:.2f} dBm")
    print(f"Repeated/stale-looking fraction: {repeated_fraction:.2%}")

    if mean_change_gap_s is not None:
        print(f"Mean time between RSSI changes: {mean_change_gap_s:.2f} sec")
        print(f"Median time between RSSI changes: {median_change_gap_s:.2f} sec")
        print(f"Effective RSSI update rate: {effective_rssi_hz:.2f} Hz")
    else:
        print("Mean time between RSSI changes: no changes detected")
        print("Effective RSSI update rate: 0 Hz")

def main():
    if not LOG_DIR.exists():
        raise FileNotFoundError(f"Missing folder: {LOG_DIR}")

    csv_files = sorted(LOG_DIR.glob("*.csv"))

    if not csv_files:
        print(f"No CSV files found in {LOG_DIR}")
        return

    for path in csv_files:
        analyze_file(path)

if __name__ == "__main__":
    main()