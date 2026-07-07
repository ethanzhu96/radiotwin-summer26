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
    public bool regenerateOnModeSwitch = true;
    public KeyCode regenerateKey = KeyCode.R;

    [Header("Status")]
    [SerializeField] private string lastRegenerateStatus = "Never regenerated.";

    public string LastRegenerateStatus => lastRegenerateStatus;

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
        if (Input.GetKeyDown(regenerateKey)) RegenerateCurrentMode();

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

        if (regenerateOnModeSwitch)
        {
            RegenerateCurrentMode();
        }
    }

    [ContextMenu("Regenerate Current Mode")]
    public void RegenerateCurrentMode()
    {
        bool regenerated = false;

        switch (currentMode)
        {
            case VisualizationMode.M0_RawTrajectory:
                regenerated = RegenerateM0();
                break;
            case VisualizationMode.M1_FloorTiles:
                regenerated = RegenerateM1();
                break;
            case VisualizationMode.M2_HeightBars:
                regenerated = RegenerateM2();
                break;
            case VisualizationMode.M3_Collapsed2D:
                regenerated = RegenerateM3();
                break;
            case VisualizationMode.M4_VoxelCloud:
                regenerated = RegenerateM4();
                break;
        }

        lastRegenerateStatus = regenerated
            ? "Regenerated " + currentMode + " at " + System.DateTime.Now.ToString("h:mm:ss tt")
            : "Failed to regenerate " + currentMode + " at " + System.DateTime.Now.ToString("h:mm:ss tt") + ". Check assigned mode parent.";

        Debug.Log("VisualizationModeManager: " + lastRegenerateStatus);
    }

    private bool RegenerateM0()
    {
        if (m0RawTrajectoryParent == null)
        {
            return false;
        }

        M0_RawTrajectory visualizer = m0RawTrajectoryParent.GetComponentInChildren<M0_RawTrajectory>(true);

        if (visualizer != null)
        {
            visualizer.GenerateRawTrajectory();
            return true;
        }

        return false;
    }

    private bool RegenerateM1()
    {
        if (m1FloorTilesParent == null)
        {
            return false;
        }

        RssiFloorHeatmap visualizer = m1FloorTilesParent.GetComponentInChildren<RssiFloorHeatmap>(true);

        if (visualizer != null)
        {
            visualizer.GenerateFloorHeatmap();
            return true;
        }

        return false;
    }

    private bool RegenerateM2()
    {
        if (m2HeightBarsParent == null)
        {
            return false;
        }

        M2HeightBarVisualizer visualizer = m2HeightBarsParent.GetComponentInChildren<M2HeightBarVisualizer>(true);

        if (visualizer != null)
        {
            visualizer.GenerateHeightBars();
            return true;
        }

        return false;
    }

    private bool RegenerateM3()
    {
        if (m3Collapsed2DParent == null)
        {
            return false;
        }

        M3Collapsed2DVisualizer visualizer = m3Collapsed2DParent.GetComponentInChildren<M3Collapsed2DVisualizer>(true);

        if (visualizer != null)
        {
            visualizer.GenerateCollapsed2D();
            return true;
        }

        return false;
    }

    private bool RegenerateM4()
    {
        if (m4VoxelCloudParent == null)
        {
            return false;
        }

        M4VoxelCloudVisualizer visualizer = m4VoxelCloudParent.GetComponentInChildren<M4VoxelCloudVisualizer>(true);

        if (visualizer != null)
        {
            visualizer.GenerateVoxelCloud();
            return true;
        }

        return false;
    }

    private void SetActiveSafe(GameObject obj, bool active)
    {
        if (obj != null)
        {
            obj.SetActive(active);
        }
    }
}
