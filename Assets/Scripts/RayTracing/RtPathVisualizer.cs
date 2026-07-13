using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class RtPathVisualizer : MonoBehaviour
{
    [SerializeField] private Material pathMaterial;
    [SerializeField] private Transform visualParent;
    [SerializeField] private TextMeshPro label;
    [SerializeField] private float pathLineWidth = 0.025f;
    [SerializeField] private float markerDiameter = 0.08f;
    [SerializeField] private Color losColor = Color.green;
    [SerializeField] private Color spec1Color = Color.cyan;
    [SerializeField] private Color spec2Color = Color.blue;
    [SerializeField] private Color diff1Color = Color.magenta;

    private readonly List<GameObject> visuals = new List<GameObject>();
    private Material runtimeMaterial;

    public void ShowCell(RtFloorField.RtCell cell, Vector3 selectedFloorWorld)
    {
        if (cell == null)
        {
            SetLabel("No RT cell");
            return;
        }

        ShowPaths(
            cell.paths,
            cell.rxWorld,
            cell.predictedRssiDb,
            "Grid " + cell.index,
            selectedFloorWorld);
    }

    public void ShowPaths(
        IReadOnlyList<RtPath> paths,
        Vector3 receiverWorld,
        float predictedRssiDb,
        string selectionName,
        Vector3? selectionGuideStartWorld = null)
    {
        Clear();
        if (paths != null)
        {
            for (int i = 0; i < paths.Count; i++)
            {
                DrawPath(paths[i], i);
            }
        }

        DrawMarker(receiverWorld, "RT_ReceiverMarker", Color.white);
        if (selectionGuideStartWorld.HasValue &&
            Vector3.Distance(selectionGuideStartWorld.Value, receiverWorld) > 0.03f)
        {
            DrawSelectionGuide(selectionGuideStartWorld.Value, receiverWorld);
        }

        int pathCount = paths != null ? paths.Count : 0;
        EnsureFloatingLabel(receiverWorld);
        SetLabel(selectionName + "\nRelative RSSI " + predictedRssiDb.ToString("F1") +
            " dB\nPaths " + pathCount);
    }

    public void ShowMessage(string message)
    {
        SetLabel(message);
    }

    public void Clear()
    {
        for (int i = 0; i < visuals.Count; i++)
        {
            if (visuals[i] != null)
            {
                visuals[i].SetActive(false);
                Destroy(visuals[i]);
            }
        }
        visuals.Clear();
    }

    private void OnDisable()
    {
        Clear();
    }

    private void DrawPath(RtPath path, int index)
    {
        if (path.points == null || path.points.Length < 2)
        {
            return;
        }

        GameObject lineObject = new GameObject("RT_Path_" + path.kind + "_" + index);
        lineObject.transform.SetParent(GetVisualParent(), false);
        SetIgnoreRaycastLayer(lineObject);
        visuals.Add(lineObject);

        LineRenderer line = lineObject.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = path.points.Length;
        line.widthMultiplier = pathLineWidth;
        line.sharedMaterial = GetMaterial();
        Color color = GetPathColor(path.kind);
        line.startColor = color;
        line.endColor = color;
        line.SetPositions(path.points);

        for (int i = 1; i < path.points.Length - 1; i++)
        {
            DrawMarker(path.points[i], "RT_Bounce_" + index + "_" + i, color);
        }
    }

    private void DrawMarker(Vector3 worldPosition, string markerName, Color color)
    {
        GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.name = markerName;
        marker.transform.SetParent(GetVisualParent(), true);
        marker.transform.position = worldPosition;
        marker.transform.localScale = Vector3.one * markerDiameter;
        SetIgnoreRaycastLayer(marker);
        Collider collider = marker.GetComponent<Collider>();
        if (collider != null)
        {
            Destroy(collider);
        }
        Renderer renderer = marker.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.sharedMaterial = GetMaterial();
            renderer.material.color = color;
        }
        visuals.Add(marker);
    }

    private void DrawSelectionGuide(Vector3 startWorld, Vector3 endWorld)
    {
        GameObject guideObject = new GameObject("RT_SelectionHeightGuide");
        guideObject.transform.SetParent(GetVisualParent(), false);
        SetIgnoreRaycastLayer(guideObject);
        visuals.Add(guideObject);

        LineRenderer guide = guideObject.AddComponent<LineRenderer>();
        guide.useWorldSpace = true;
        guide.positionCount = 2;
        guide.widthMultiplier = pathLineWidth * 0.35f;
        guide.sharedMaterial = GetMaterial();
        Color guideColor = new Color(1f, 1f, 1f, 0.55f);
        guide.startColor = guideColor;
        guide.endColor = guideColor;
        guide.SetPosition(0, startWorld);
        guide.SetPosition(1, endWorld);
    }

    private void EnsureFloatingLabel(Vector3 receiverWorld)
    {
        if (label == null)
        {
            GameObject labelObject = new GameObject("RT_PathStatusLabel");
            labelObject.transform.SetParent(GetVisualParent(), true);
            SetIgnoreRaycastLayer(labelObject);
            label = labelObject.AddComponent<TextMeshPro>();
            label.alignment = TextAlignmentOptions.Center;
            label.fontSize = 2f;
            label.rectTransform.sizeDelta = new Vector2(3f, 1.2f);
            labelObject.transform.localScale = Vector3.one * 0.1f;
            visuals.Add(labelObject);
        }

        label.transform.position = receiverWorld + Vector3.up * 0.18f;
        Camera camera = Camera.main;
        if (camera != null)
        {
            Vector3 awayFromCamera = label.transform.position - camera.transform.position;
            if (awayFromCamera.sqrMagnitude > 0.0001f)
            {
                label.transform.rotation = Quaternion.LookRotation(awayFromCamera.normalized, Vector3.up);
            }
        }
    }

    private Transform GetVisualParent()
    {
        if (visualParent != null)
        {
            return visualParent;
        }

        RoomAlignmentManager manager = RoomAlignmentManager.Instance;
        if (manager != null && manager.DatasetRoot != null)
        {
            Transform rayTracing = manager.DatasetRoot.Find("RayTracing");
            if (rayTracing == null)
            {
                rayTracing = new GameObject("RayTracing").transform;
                rayTracing.SetParent(manager.DatasetRoot, false);
            }
            visualParent = rayTracing.Find("PathVisualizations");
            if (visualParent == null)
            {
                visualParent = new GameObject("PathVisualizations").transform;
                visualParent.SetParent(rayTracing, false);
            }
        }
        else
        {
            visualParent = transform;
        }
        return visualParent;
    }

    private Material GetMaterial()
    {
        if (pathMaterial != null)
        {
            return pathMaterial;
        }
        if (runtimeMaterial == null)
        {
            runtimeMaterial = new Material(Shader.Find("Sprites/Default"))
            {
                name = "RtPathVisualizer_RuntimeMaterial"
            };
        }
        return runtimeMaterial;
    }

    private Color GetPathColor(RtPath.Kind kind)
    {
        switch (kind)
        {
            case RtPath.Kind.Spec1: return spec1Color;
            case RtPath.Kind.Spec2: return spec2Color;
            case RtPath.Kind.Diff1: return diff1Color;
            default: return losColor;
        }
    }

    private void SetLabel(string value)
    {
        if (label != null)
        {
            label.text = value;
        }
    }

    private static void SetIgnoreRaycastLayer(GameObject target)
    {
        int layer = LayerMask.NameToLayer("Ignore Raycast");
        if (layer >= 0)
        {
            target.layer = layer;
        }
    }
}
