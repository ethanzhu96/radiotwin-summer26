using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

public class M2HeightBarVisualizer : MonoBehaviour
{
    [Header("CSV")]
    public string fileName = "rf_trajectory_log.csv";

    [Header("Coordinate Frame")]
    public Transform coordinateFrameRoot;

    [Header("Grid Settings")]
    public float cellSize = 0.5f;
    public float floorY = 0.01f;

    [Header("Bar Settings")]
    public float minBarHeight = 0.05f;
    public float maxBarHeight = 1.5f;
    public float barWidthScale = 1f;
    public float alpha = 0.85f;

    [Header("Rendering")]
    public Material barMaterial;
    public bool clearOldBarsOnStart = true;

    private float gridMinRssi;
    private float gridMaxRssi;

    private class CellAccum
    {
        public float sum;
        public int count;

        public void Add(float value)
        {
            sum += value;
            count++;
        }

        public float Average()
        {
            if (count == 0)
            {
                return 0f;
            }

            return sum / count;
        }
    }

    IEnumerator Start()
    {
        yield return RoomAlignmentManager.WaitForPlaybackDecision();
        if (RoomAlignmentManager.Instance == null || !RoomAlignmentManager.Instance.AttachVisualization(transform, "M2"))
        {
            yield break;
        }
        coordinateFrameRoot = null;
        GenerateHeightBars();
    }

    [ContextMenu("Regenerate Height Bars")]
    public void GenerateHeightBars()
    {
        if (Application.isPlaying && (RoomAlignmentManager.Instance == null ||
            RoomAlignmentManager.Instance.State != RoomAlignmentManager.PlaybackState.Ready))
        {
            return;
        }
        ResolveCoordinateFrameRoot();

        if (clearOldBarsOnStart)
        {
            ClearChildren();
        }

        string path = Path.Combine(Application.persistentDataPath, fileName);

        Debug.Log("M2HeightBarVisualizer persistent path: " + Application.persistentDataPath);
        Debug.Log("M2HeightBarVisualizer loading CSV from: " + path);

        if (!File.Exists(path))
        {
            Debug.LogError("M2HeightBarVisualizer: CSV not found at: " + path);
            return;
        }

        Dictionary<Vector2Int, CellAccum> grid = LoadAndBinCsv(path);

        if (grid.Count == 0)
        {
            Debug.LogWarning("M2HeightBarVisualizer: No occupied grid cells. Cannot generate bars.");
            return;
        }

        CalculateGlobalRssiRange(grid);
        CreateBars(grid);

        Debug.Log(
            "M2HeightBarVisualizer: Generated " + grid.Count +
            " height bars from M1-style floor grid. RSSI range " +
            gridMinRssi.ToString("F1", CultureInfo.InvariantCulture) + " to " +
            gridMaxRssi.ToString("F1", CultureInfo.InvariantCulture) + " dBm."
        );
    }

    private Dictionary<Vector2Int, CellAccum> LoadAndBinCsv(string path)
    {
        Dictionary<Vector2Int, CellAccum> grid = new Dictionary<Vector2Int, CellAccum>();

        string[] lines = File.ReadAllLines(path);

        if (lines.Length <= 1)
        {
            Debug.LogWarning("M2HeightBarVisualizer: CSV has no data rows.");
            return grid;
        }

        string[] headers = SplitCsvLine(lines[0]);

        int xIndex = FindColumn(headers, "reference_local_pos_x");
        int zIndex = FindColumn(headers, "reference_local_pos_z");
        int rssiIndex = FindColumn(headers, "rssi_dbm", "rssi", "rssiDbm");

        if (xIndex < 0 || zIndex < 0 || rssiIndex < 0)
        {
            Debug.LogError(RoomAlignmentManager.LogPrefix + " M2 rejected legacy CSV: authoritative reference_local_pos_* columns are required.");
            Debug.LogError("M2HeightBarVisualizer headers found: " + string.Join(" | ", headers));
            return grid;
        }

        for (int row = 1; row < lines.Length; row++)
        {
            if (string.IsNullOrWhiteSpace(lines[row]))
            {
                continue;
            }

            string[] cols = SplitCsvLine(lines[row]);
            int maxRequiredIndex = Mathf.Max(Mathf.Max(xIndex, zIndex), rssiIndex);

            if (cols.Length <= maxRequiredIndex)
            {
                continue;
            }

            float x;
            float z;
            float rssi;

            bool parsedX = TryParseFloat(cols[xIndex], out x);
            bool parsedZ = TryParseFloat(cols[zIndex], out z);
            bool parsedRssi = TryParseFloat(cols[rssiIndex], out rssi);

            if (!parsedX || !parsedZ || !parsedRssi)
            {
                continue;
            }

            int gx = Mathf.FloorToInt(x / cellSize);
            int gz = Mathf.FloorToInt(z / cellSize);
            Vector2Int key = new Vector2Int(gx, gz);

            if (!grid.ContainsKey(key))
            {
                grid[key] = new CellAccum();
            }

            grid[key].Add(rssi);
        }

        return grid;
    }

    private void CalculateGlobalRssiRange(Dictionary<Vector2Int, CellAccum> grid)
    {
        gridMinRssi = float.PositiveInfinity;
        gridMaxRssi = float.NegativeInfinity;

        foreach (KeyValuePair<Vector2Int, CellAccum> kvp in grid)
        {
            float avgRssi = kvp.Value.Average();
            gridMinRssi = Mathf.Min(gridMinRssi, avgRssi);
            gridMaxRssi = Mathf.Max(gridMaxRssi, avgRssi);
        }
    }

    private void CreateBars(Dictionary<Vector2Int, CellAccum> grid)
    {
        foreach (KeyValuePair<Vector2Int, CellAccum> kvp in grid)
        {
            Vector2Int cell = kvp.Key;
            float avgRssi = kvp.Value.Average();

            float x = (cell.x + 0.5f) * cellSize;
            float z = (cell.y + 0.5f) * cellSize;

            CreateBar(cell, new Vector3(x, floorY, z), avgRssi);
        }
    }

    private void CreateBar(Vector2Int cell, Vector3 floorCenter, float rssi)
    {
        float t = NormalizeRssi(rssi);
        float height = Mathf.Lerp(minBarHeight, maxBarHeight, t);

        GameObject bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
        bar.name = "M2_BAR_" + cell.x + "_" + cell.y + "_avg_" + rssi.ToString("F1", CultureInfo.InvariantCulture);

        bar.transform.SetParent(transform, false);
        bar.transform.localPosition = ToModeLocalPosition(new Vector3(floorCenter.x, floorY + height / 2f, floorCenter.z));
        bar.transform.localRotation = ToModeLocalRotation(Quaternion.identity);
        bar.transform.localScale = new Vector3(
            cellSize * barWidthScale,
            height,
            cellSize * barWidthScale
        );

        Collider collider = bar.GetComponent<Collider>();
        if (collider != null)
        {
            Destroy(collider);
        }

        Renderer renderer = bar.GetComponent<Renderer>();
        if (renderer == null)
        {
            return;
        }

        Material mat = CreateBarMaterial();
        mat.color = RssiToColor(rssi);
        renderer.material = mat;
    }

    private Material CreateBarMaterial()
    {
        if (barMaterial != null)
        {
            return new Material(barMaterial);
        }

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

    private float NormalizeRssi(float rssi)
    {
        if (Mathf.Approximately(gridMinRssi, gridMaxRssi))
        {
            return 0.5f;
        }

        return Mathf.InverseLerp(gridMinRssi, gridMaxRssi, rssi);
    }

    private Color RssiToColor(float rssi)
    {
        float t = NormalizeRssi(rssi);
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

    private Vector3 ToModeLocalPosition(Vector3 coordinateFrameLocalPosition)
    {
        if (coordinateFrameRoot == null)
        {
            return coordinateFrameLocalPosition;
        }

        Vector3 worldPosition = coordinateFrameRoot.TransformPoint(coordinateFrameLocalPosition);
        return transform.InverseTransformPoint(worldPosition);
    }

    private void ResolveCoordinateFrameRoot()
    {
        if (transform.parent != null && transform.parent.name == "DatasetRoot")
        {
            coordinateFrameRoot = null;
            return;
        }

        if (coordinateFrameRoot != null)
        {
            return;
        }

        GameObject roomAnchor = GameObject.Find("RoomAnchor");

        if (roomAnchor != null)
        {
            coordinateFrameRoot = roomAnchor.transform;
            Debug.Log("M2HeightBarVisualizer: Auto-assigned coordinateFrameRoot to RoomAnchor.");
        }
    }

    private Quaternion ToModeLocalRotation(Quaternion coordinateFrameLocalRotation)
    {
        if (coordinateFrameRoot == null)
        {
            return coordinateFrameLocalRotation;
        }

        Quaternion worldRotation = coordinateFrameRoot.rotation * coordinateFrameLocalRotation;
        return Quaternion.Inverse(transform.rotation) * worldRotation;
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
