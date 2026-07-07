using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(VisualizationModeManager))]
public class VisualizationModeManagerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        VisualizationModeManager manager = (VisualizationModeManager)target;

        EditorGUILayout.Space(8f);
        EditorGUILayout.HelpBox(manager.LastRegenerateStatus, MessageType.Info);

        if (GUILayout.Button("Regenerate Current Mode"))
        {
            manager.RegenerateCurrentMode();
            EditorUtility.SetDirty(manager);
        }

        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("Switch And Regenerate", EditorStyles.boldLabel);

        DrawModeButton(manager, VisualizationModeManager.VisualizationMode.M0_RawTrajectory, "M0 Raw Trajectory");
        DrawModeButton(manager, VisualizationModeManager.VisualizationMode.M1_FloorTiles, "M1 Floor Tiles");
        DrawModeButton(manager, VisualizationModeManager.VisualizationMode.M2_HeightBars, "M2 Height Bars");
        DrawModeButton(manager, VisualizationModeManager.VisualizationMode.M3_Collapsed2D, "M3 Collapsed 2D");
        DrawModeButton(manager, VisualizationModeManager.VisualizationMode.M4_VoxelCloud, "M4 Voxel Cloud");
    }

    private static void DrawModeButton(
        VisualizationModeManager manager,
        VisualizationModeManager.VisualizationMode mode,
        string label
    )
    {
        if (GUILayout.Button(label))
        {
            manager.ApplyMode(mode);
            EditorUtility.SetDirty(manager);
        }
    }
}
