from pathlib import Path
import pandas as pd

RAW_PATH = Path("data/raw/rf_trajectory_log.csv")
OUT_PATH = Path("data/processed/rf_samples_clean.csv")

USEFUL_COLUMNS = {
    "timestamp_unix_ms": "timestamp_unix_ms",
    "world_pos_x": "x",
    "world_pos_y": "y",
    "world_pos_z": "z",
    "rssi_dbm": "rssi_dbm",
    "ssid": "ssid",
    "bssid": "bssid",
    "frequency_mhz": "frequency_mhz",
    "link_speed_mbps": "link_speed_mbps",
}

def main():
    if not RAW_PATH.exists():
        raise FileNotFoundError(f"Could not find raw file: {RAW_PATH}")

    df = pd.read_csv(RAW_PATH)

    missing = [col for col in USEFUL_COLUMNS if col not in df.columns]
    if missing:
        raise ValueError(f"Missing expected columns: {missing}")

    clean = df[list(USEFUL_COLUMNS.keys())].rename(columns=USEFUL_COLUMNS)

    # Drop rows without valid RSSI or position
    clean = clean.dropna(subset=["x", "y", "z", "rssi_dbm"])

    # Convert numeric columns safely
    numeric_cols = [
        "timestamp_unix_ms",
        "x",
        "y",
        "z",
        "rssi_dbm",
        "frequency_mhz",
        "link_speed_mbps",
    ]

    for col in numeric_cols:
        clean[col] = pd.to_numeric(clean[col], errors="coerce")

    clean = clean.dropna(subset=["x", "y", "z", "rssi_dbm"])

    OUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    clean.to_csv(OUT_PATH, index=False)

    print(f"Saved cleaned RF samples to: {OUT_PATH}")
    print(f"Rows: {len(clean)}")
    print()
    print(clean.head())

if __name__ == "__main__":
    main()