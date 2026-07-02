using UnityEngine;

public class VisualizationModeManager : MonoBehaviour
{
    public enum VisualizationMode
    {
        M0_RawTrajectory = 0,
        M1_FloorTiles = 1,
        M2_HeightBars = 2,
        M3_Collapsed2D = 3,
        M4_VoxelCloud = 4
    }

    [Header("Mode Parents")]
    public GameObject m0RawTrajectoryParent;
    public GameObject m1FloorTilesParent;
    public GameObject m2HeightBarsParent;
    public GameObject m3Collapsed2DParent;
    public GameObject m4VoxelCloudParent;

    [Header("Current Mode")]
    public VisualizationMode currentMode = VisualizationMode.M1_FloorTiles;

    void Start()
    {
        ApplyMode(currentMode);
    }

    void Update()
    {
        // Keyboard testing in Unity Editor
        if (Input.GetKeyDown(KeyCode.Alpha0)) ApplyMode(VisualizationMode.M0_RawTrajectory);
        if (Input.GetKeyDown(KeyCode.Alpha1)) ApplyMode(VisualizationMode.M1_FloorTiles);
        if (Input.GetKeyDown(KeyCode.Alpha2)) ApplyMode(VisualizationMode.M2_HeightBars);
        if (Input.GetKeyDown(KeyCode.Alpha3)) ApplyMode(VisualizationMode.M3_Collapsed2D);
        if (Input.GetKeyDown(KeyCode.Alpha4)) ApplyMode(VisualizationMode.M4_VoxelCloud);

        // press B to cycle between M modes.
        if (OVRInput.GetDown(OVRInput.Button.Two))
        {
            CycleMode();
        }
    }

    public void CycleMode()
    {
        int next = ((int)currentMode + 1) % 5;
        ApplyMode((VisualizationMode)next);
    }

    public void ApplyMode(VisualizationMode mode)
    {
        currentMode = mode;

        SetActiveSafe(m0RawTrajectoryParent, mode == VisualizationMode.M0_RawTrajectory);
        SetActiveSafe(m1FloorTilesParent, mode == VisualizationMode.M1_FloorTiles);
        SetActiveSafe(m2HeightBarsParent, mode == VisualizationMode.M2_HeightBars);
        SetActiveSafe(m3Collapsed2DParent, mode == VisualizationMode.M3_Collapsed2D);
        SetActiveSafe(m4VoxelCloudParent, mode == VisualizationMode.M4_VoxelCloud);

        Debug.Log("Visualization mode set to: " + mode);
    }

    private void SetActiveSafe(GameObject obj, bool active)
    {
        if (obj != null)
        {
            obj.SetActive(active);
        }
    }
}