from pathlib import Path
import pandas as pd
import matplotlib.pyplot as plt

INPUT_PATH = Path("data/processed/rf_samples_clean.csv")
OUTPUT_PATH = Path("outputs/figures/rf_trajectory_3d.png")

def main():
    if not INPUT_PATH.exists():
        raise FileNotFoundError(f"Could not find cleaned file: {INPUT_PATH}")

    df = pd.read_csv(INPUT_PATH)

    required = ["x", "y", "z", "rssi_dbm"]
    missing = [col for col in required if col not in df.columns]
    if missing:
        raise ValueError(f"Missing columns: {missing}")

    OUTPUT_PATH.parent.mkdir(parents=True, exist_ok=True)

    fig = plt.figure(figsize=(9, 7))
    ax = fig.add_subplot(111, projection="3d")

    points = ax.scatter(
        df["x"],
        df["z"],   # using z as horizontal room depth
        df["y"],   # using y as height
        c=df["rssi_dbm"],
        s=25,
    )

    ax.set_title("Quest 3 WiFi RSSI Trajectory")
    ax.set_xlabel("World X")
    ax.set_ylabel("World Z")
    ax.set_zlabel("World Y / Height")

    cbar = fig.colorbar(points, ax=ax)
    cbar.set_label("RSSI (dBm)")

    plt.tight_layout()
    plt.savefig(OUTPUT_PATH, dpi=200)
    print(f"Saved plot to: {OUTPUT_PATH}")

if __name__ == "__main__":
    main()