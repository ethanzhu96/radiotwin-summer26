using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using Meta.XR.MRUtilityKit;

public class M0_RawTrajectory : MonoBehaviour
{
    public enum RenderFrameMode
    {
        SerializedParent,
        LiveMRUKRoom
    }

    [Header("CSV")]
    public string fileName = "rf_trajectory_log.csv";

    [Header("Coordinate Frame")]
    public RenderFrameMode renderFrameMode = RenderFrameMode.LiveMRUKRoom;
    public Transform coordinateFrameRoot;
    public bool inputPositionsAreMeshLocal = true;
    public bool parentToMRUKRoomAtRuntime = false;

    [Header("Alignment Nudge")]
    public Vector3 positionOffset = Vector3.zero;
    public float yawOffsetDegrees = 0f;

    [Header("Points")]
    public bool drawSamplePoints = false;
    public float pointRadius = 0.05f;
    public float minRssi = -70f;
    public float maxRssi = -40f;
    public float alpha = 0.85f;
    public int maxPoints = 0;

    [Header("Path")]
    public bool drawPathLine = true;
    public float lineWidth = 0.025f;
    public Color lineColor = Color.white;
    public bool colorLineByRssi = false;

    private class SignalSample
    {
        public Vector3 position;
        public float rssi;
    }

    private bool csvPositionsAreMeshLocal = false;
    private string csvAnchorName = "";
    private float lastAppliedYawOffsetDegrees;
    private Vector3 lastAppliedPositionOffset;
    private Transform lastResolvedCoordinateFrameRoot;

    void Start()
    {
        GenerateRawTrajectory();
        RememberAppliedAlignment();
    }

    void Update()
    {
        if (!Application.isPlaying)
        {
            return;
        }

        if (!Mathf.Approximately(yawOffsetDegrees, lastAppliedYawOffsetDegrees) ||
            positionOffset != lastAppliedPositionOffset ||
            ResolveCoordinateFrameRoot())
        {
            GenerateRawTrajectory();
            RememberAppliedAlignment();
        }
    }

    [ContextMenu("Regenerate Raw Trajectory")]
    public void GenerateRawTrajectory()
    {
        ClearChildren();
        ResolveCoordinateFrameRoot();

        string path = Path.Combine(Application.persistentDataPath, fileName);

        Debug.Log("M0_RawTrajectory persistent path: " + Application.persistentDataPath);
        Debug.Log("M0_RawTrajectory loading CSV from: " + path);

        if (!File.Exists(path))
        {
            Debug.LogError("M0_RawTrajectory: CSV not found at: " + path);
            DisablePathLine();
            return;
        }

        List<SignalSample> samples = LoadSamples(path);

        if (samples.Count == 0)
        {
            Debug.LogWarning("M0_RawTrajectory: No samples loaded.");
            DisablePathLine();
            return;
        }

        if (drawSamplePoints)
        {
            CreatePoints(samples);
        }

        UpdatePathLine(samples);

        RememberAppliedAlignment();
        Debug.Log(
            "M0_RawTrajectory: Generated " + samples.Count +
            " raw trajectory points. yawOffsetDegrees=" + yawOffsetDegrees.ToString("F2", CultureInfo.InvariantCulture) +
            ", positionOffset=" + positionOffset +
            ", coordinateFrameRoot=" + (coordinateFrameRoot != null ? coordinateFrameRoot.name : "none") +
            ", csvAnchorName=" + (string.IsNullOrEmpty(csvAnchorName) ? "none" : csvAnchorName)
        );

        if (renderFrameMode == RenderFrameMode.LiveMRUKRoom &&
            coordinateFrameRoot != null &&
            !string.IsNullOrEmpty(csvAnchorName) &&
            csvAnchorName != coordinateFrameRoot.name)
        {
            Debug.LogWarning(
                "M0_RawTrajectory: CSV was logged against '" + csvAnchorName +
                "' but current render frame is '" + coordinateFrameRoot.name +
                "'. If this is the live passthrough scene, M0 can be offset from the EffectMesh."
            );
        }
    }

    private void RememberAppliedAlignment()
    {
        lastAppliedYawOffsetDegrees = yawOffsetDegrees;
        lastAppliedPositionOffset = positionOffset;
    }

    private List<SignalSample> LoadSamples(string path)
    {
        List<SignalSample> samples = new List<SignalSample>();
        string[] lines = File.ReadAllLines(path);

        if (lines.Length <= 1)
        {
            Debug.LogWarning("M0_RawTrajectory: CSV has no data rows.");
            return samples;
        }

        string[] headers = SplitCsvLine(lines[0]);

        int xIndex = FindColumn(headers, "anchor_local_x", "local_pos_x", "pos_x", "world_pos_x");
        int yIndex = FindColumn(headers, "anchor_local_y", "local_pos_y", "pos_y", "world_pos_y");
        int zIndex = FindColumn(headers, "anchor_local_z", "local_pos_z", "pos_z", "world_pos_z");
        int rssiIndex = FindColumn(headers, "rssi_dbm", "rssi", "rssiDbm");
        int anchorSourceIndex = FindColumn(headers, "anchor_source");
        int anchorNameIndex = FindColumn(headers, "anchor_name");
        csvPositionsAreMeshLocal = false;
        csvAnchorName = "";

        if (xIndex < 0 || yIndex < 0 || zIndex < 0 || rssiIndex < 0)
        {
            Debug.LogError("M0_RawTrajectory: CSV missing required columns. Need anchor_local_x/anchor_local_y/anchor_local_z, pos_x/pos_y/pos_z, or world_pos_x/world_pos_y/world_pos_z and rssi_dbm.");
            Debug.LogError("M0_RawTrajectory headers found: " + string.Join(" | ", headers));
            return samples;
        }

        for (int row = 1; row < lines.Length; row++)
        {
            if (maxPoints > 0 && samples.Count >= maxPoints)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(lines[row]))
            {
                continue;
            }

            string[] cols = SplitCsvLine(lines[row]);
            int maxRequiredIndex = Mathf.Max(Mathf.Max(xIndex, yIndex), Mathf.Max(zIndex, rssiIndex));

            if (cols.Length <= maxRequiredIndex)
            {
                continue;
            }

            float x;
            float y;
            float z;
            float rssi;

            bool parsedX = TryParseFloat(cols[xIndex], out x);
            bool parsedY = TryParseFloat(cols[yIndex], out y);
            bool parsedZ = TryParseFloat(cols[zIndex], out z);
            bool parsedRssi = TryParseFloat(cols[rssiIndex], out rssi);

            if (!parsedX || !parsedY || !parsedZ || !parsedRssi)
            {
                continue;
            }

            if (anchorSourceIndex >= 0 && cols.Length > anchorSourceIndex)
            {
                string anchorSource = CleanHeader(cols[anchorSourceIndex]);

                if (anchorSource == "mruk")
                {
                    csvPositionsAreMeshLocal = true;
                }
            }

            if (anchorNameIndex >= 0 && cols.Length > anchorNameIndex && string.IsNullOrEmpty(csvAnchorName))
            {
                csvAnchorName = cols[anchorNameIndex].Trim().Replace("\"", "");
            }

            SignalSample sample = new SignalSample
            {
                position = new Vector3(x, y, z),
                rssi = rssi
            };

            samples.Add(sample);
        }

        return samples;
    }

    private void CreatePoints(List<SignalSample> samples)
    {
        for (int i = 0; i < samples.Count; i++)
        {
            SignalSample sample = samples[i];

            GameObject point = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            point.name = "M0_RSSI_POINT_" + i + "_" + sample.rssi.ToString("F1", CultureInfo.InvariantCulture);

            point.transform.SetParent(transform, false);
            point.transform.localPosition = ToModeLocalPosition(sample.position);
            point.transform.localScale = Vector3.one * pointRadius * 2f;

            Collider collider = point.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }

            Renderer renderer = point.GetComponent<Renderer>();
            if (renderer != null)
            {
                Material mat = CreateTransparentMaterial();
                mat.color = RssiToColor(sample.rssi);
                renderer.material = mat;
            }
        }
    }

    private void UpdatePathLine(List<SignalSample> samples)
    {
        LineRenderer lineRenderer = GetComponent<LineRenderer>();

        if (!drawPathLine)
        {
            if (lineRenderer != null)
            {
                lineRenderer.enabled = false;
            }

            return;
        }

        if (lineRenderer == null)
        {
            lineRenderer = gameObject.AddComponent<LineRenderer>();
        }

        lineRenderer.enabled = true;
        lineRenderer.useWorldSpace = false;
        lineRenderer.positionCount = samples.Count;
        lineRenderer.widthMultiplier = Mathf.Max(lineWidth, 0.001f);
        lineRenderer.material = CreateTransparentMaterial();
        lineRenderer.material.color = GetLineColor();

        Gradient gradient = new Gradient();
        GradientColorKey[] colorKeys = colorLineByRssi ? BuildLineColorKeys(samples) : BuildSolidLineColorKeys();
        GradientAlphaKey[] alphaKeys = new GradientAlphaKey[]
        {
            new GradientAlphaKey(alpha, 0f),
            new GradientAlphaKey(alpha, 1f)
        };
        gradient.SetKeys(colorKeys, alphaKeys);
        lineRenderer.colorGradient = gradient;

        for (int i = 0; i < samples.Count; i++)
        {
            lineRenderer.SetPosition(i, ToModeLocalPosition(samples[i].position));
        }
    }

    private Vector3 ToModeLocalPosition(Vector3 coordinateFrameLocalPosition)
    {
        Vector3 nudgedFramePosition =
            Quaternion.Euler(0f, yawOffsetDegrees, 0f) * coordinateFrameLocalPosition + positionOffset;

        if (inputPositionsAreMeshLocal || csvPositionsAreMeshLocal)
        {
            if (renderFrameMode == RenderFrameMode.LiveMRUKRoom && coordinateFrameRoot != null)
            {
                Vector3 worldPosition = coordinateFrameRoot.TransformPoint(nudgedFramePosition);
                return transform.InverseTransformPoint(worldPosition);
            }

            return nudgedFramePosition;
        }

        if (coordinateFrameRoot == null)
        {
            return nudgedFramePosition;
        }

        Vector3 legacyWorldPosition = coordinateFrameRoot.TransformPoint(nudgedFramePosition);
        return transform.InverseTransformPoint(legacyWorldPosition);
    }

    private bool ResolveCoordinateFrameRoot()
    {
        Transform previousRoot = coordinateFrameRoot;

        if (renderFrameMode == RenderFrameMode.LiveMRUKRoom &&
            MRUK.Instance != null &&
            MRUK.Instance.GetCurrentRoom() != null)
        {
            coordinateFrameRoot = MRUK.Instance.GetCurrentRoom().transform;

            if (parentToMRUKRoomAtRuntime && Application.isPlaying && transform.parent != coordinateFrameRoot)
            {
                Debug.LogWarning(
                    "M0_RawTrajectory: parentToMRUKRoomAtRuntime is deprecated for stable placement. " +
                    "Leave it disabled; M0 now converts room-local CSV points through coordinateFrameRoot without reparenting."
                );
            }

            bool changedToMRUK = previousRoot != coordinateFrameRoot || lastResolvedCoordinateFrameRoot != coordinateFrameRoot;
            lastResolvedCoordinateFrameRoot = coordinateFrameRoot;
            return changedToMRUK;
        }

        if (renderFrameMode == RenderFrameMode.LiveMRUKRoom)
        {
            lastResolvedCoordinateFrameRoot = coordinateFrameRoot;
            return previousRoot != coordinateFrameRoot;
        }

        if (coordinateFrameRoot != null)
        {
            lastResolvedCoordinateFrameRoot = coordinateFrameRoot;
            return previousRoot != coordinateFrameRoot;
        }

        GameObject roomAnchor = GameObject.Find("RoomAnchor");

        if (roomAnchor != null)
        {
            coordinateFrameRoot = roomAnchor.transform;
            Debug.Log("M0_RawTrajectory: Auto-assigned coordinateFrameRoot to RoomAnchor.");
        }

        bool changed = previousRoot != coordinateFrameRoot || lastResolvedCoordinateFrameRoot != coordinateFrameRoot;
        lastResolvedCoordinateFrameRoot = coordinateFrameRoot;
        return changed;
    }

    private GradientColorKey[] BuildLineColorKeys(List<SignalSample> samples)
    {
        int keyCount = Mathf.Min(samples.Count, 8);
        GradientColorKey[] keys = new GradientColorKey[keyCount];

        if (keyCount == 1)
        {
            keys[0] = new GradientColorKey(RssiToColor(samples[0].rssi), 0f);
            return keys;
        }

        for (int i = 0; i < keyCount; i++)
        {
            float time = i / (float)(keyCount - 1);
            int sampleIndex = Mathf.RoundToInt(time * (samples.Count - 1));
            keys[i] = new GradientColorKey(RssiToColor(samples[sampleIndex].rssi), time);
        }

        return keys;
    }

    private GradientColorKey[] BuildSolidLineColorKeys()
    {
        Color c = GetLineColor();

        return new GradientColorKey[]
        {
            new GradientColorKey(c, 0f),
            new GradientColorKey(c, 1f)
        };
    }

    private Color GetLineColor()
    {
        Color c = lineColor;
        c.a = alpha;
        return c;
    }

    private void DisablePathLine()
    {
        LineRenderer lineRenderer = GetComponent<LineRenderer>();

        if (lineRenderer != null)
        {
            lineRenderer.enabled = false;
            lineRenderer.positionCount = 0;
        }
    }

    private Material CreateTransparentMaterial()
    {
        Material mat = new Material(Shader.Find("Standard"));

        if (alpha < 1f)
        {
            mat.SetFloat("_Mode", 3);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = 3000;
        }

        return mat;
    }

    private Color RssiToColor(float rssi)
    {
        float t = Mathf.InverseLerp(minRssi, maxRssi, rssi);
        Color c;

        if (t < 0.25f)
        {
            c = Color.Lerp(Color.blue, Color.cyan, t / 0.25f);
        }
        else if (t < 0.5f)
        {
            c = Color.Lerp(Color.cyan, Color.green, (t - 0.25f) / 0.25f);
        }
        else if (t < 0.75f)
        {
            c = Color.Lerp(Color.green, Color.yellow, (t - 0.5f) / 0.25f);
        }
        else
        {
            c = Color.Lerp(Color.yellow, Color.red, (t - 0.75f) / 0.25f);
        }

        c.a = alpha;
        return c;
    }

    private void ClearChildren()
    {
        List<GameObject> children = new List<GameObject>();

        foreach (Transform child in transform)
        {
            children.Add(child.gameObject);
        }

        foreach (GameObject child in children)
        {
            if (Application.isPlaying)
            {
                Destroy(child);
            }
            else
            {
                DestroyImmediate(child);
            }
        }
    }

    private int FindColumn(string[] headers, params string[] possibleNames)
    {
        for (int i = 0; i < headers.Length; i++)
        {
            string cleanedHeader = CleanHeader(headers[i]);

            foreach (string possibleName in possibleNames)
            {
                if (cleanedHeader == CleanHeader(possibleName))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private string CleanHeader(string s)
    {
        return s.Trim().Replace("\"", "").ToLowerInvariant();
    }

    private bool TryParseFloat(string s, out float value)
    {
        s = s.Trim().Replace("\"", "");

        return float.TryParse(
            s,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value
        );
    }

    private string[] SplitCsvLine(string line)
    {
        List<string> result = new List<string>();
        bool inQuotes = false;
        string current = "";

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '"')
            {
                inQuotes = !inQuotes;
                current += c;
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current);
                current = "";
            }
            else
            {
                current += c;
            }
        }

        result.Add(current);

        return result.ToArray();
    }
}
