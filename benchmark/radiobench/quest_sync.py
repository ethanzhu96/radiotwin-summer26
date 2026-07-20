import os
from pathlib import Path
import subprocess


def sync_file_from_quest(
    adb_path: str | Path,
    package_name: str,
    file_name: str,
    destination: str | Path,
) -> Path:
    """Atomically replace destination with a Quest persistent-data export."""
    adb = Path(adb_path).expanduser()
    if not adb.is_file():
        raise FileNotFoundError(f"ADB executable does not exist: {adb}")
    if not package_name or any(character.isspace() for character in package_name):
        raise ValueError("quest_sync.package_name must be a valid non-empty package name.")

    target = Path(destination)
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = target.with_name(target.name + ".quest-download.tmp")
    if not file_name or Path(file_name).name != file_name:
        raise ValueError("Quest sync file_name must be a plain file name.")
    remote = f"/sdcard/Android/data/{package_name}/files/{file_name}"

    state = subprocess.run(
        [str(adb), "get-state"],
        capture_output=True,
        text=True,
        check=False,
    )
    if state.returncode != 0 or state.stdout.strip() != "device":
        detail = state.stderr.strip() or state.stdout.strip() or "no connected device"
        raise RuntimeError(f"Quest is not available through ADB: {detail}")

    try:
        pull = subprocess.run(
            [str(adb), "pull", remote, str(temporary)],
            capture_output=True,
            text=True,
            check=False,
        )
        if pull.returncode != 0 or not temporary.is_file():
            detail = pull.stderr.strip() or pull.stdout.strip() or "unknown ADB error"
            raise RuntimeError(f"Could not pull Quest file {file_name}: {detail}")
        if temporary.stat().st_size == 0:
            raise RuntimeError("The RF CSV pulled from the Quest is empty.")
        os.replace(temporary, target)
    finally:
        temporary.unlink(missing_ok=True)

    return target


def sync_rf_csv_from_quest(
    adb_path: str | Path,
    package_name: str,
    destination: str | Path,
) -> Path:
    return sync_file_from_quest(
        adb_path, package_name, "rf_trajectory_log.csv", destination
    )
