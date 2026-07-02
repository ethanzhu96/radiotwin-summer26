using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

public class M3Collapsed2DVisualizer : MonoBehaviour
{
    public enum HeightAveragingMode
    {
        EqualWeight,
        SemanticHeightWeighted
    }

    [Header("CSV")]
    public string fileName = "rf_trajectory_log.csv";

    [Header("Voxel Field")]
    public float voxelSize = 0.5f;
    public float verticalMinY = 0f;
    public float verticalMaxY = 2f;
    public float maxNearestSampleDistance = 2f;
    public int maxVoxels = 5000;

    [Header("Collapse")]
    public HeightAveragingMode averagingMode = HeightAveragingMode.EqualWeight;
    public float semanticHeightCenter = 1.1f;
    public float semanticHeightSigma = 0.45f;
    public float floorY = 0.01f;

    [Header("RSSI")]
    public float minRssi = -70f;
    public float maxRssi = -40f;
    public float alpha = 1f;

    [Header("Rendering")]
    public Material tileMaterial;
    public bool clearOldTilesOnStart = true;

    private class SignalSample
    {
        public Vector3 position;
        public float rssi;
    }

    private class VoxelValue
    {
        public Vector3Int cell;
        public Vector3 center;
        public float rssi;
    }

    private class ColumnAccum
    {
        public float weightedSum;
        public float weightSum;
        public int count;

        public void Add(float value, float weight)
        {
            weightedSum += value * weight;
            weightSum += weight;
            count++;
        }

        public float Average()
        {
            if (weightSum <= 0f)
            {
                return 0f;
            }

            return weightedSum / weightSum;
        }
    }

    void Start()
    {
        GenerateCollapsed2D();
    }

    [ContextMenu("Regenerate Collapsed 2D")]
    public void GenerateCollapsed2D()
    {
        if (clearOldTilesOnStart)
        {
            ClearChildren();
        }

        string path = Path.Combine(Application.persistentDataPath, fileName);

        Debug.Log("M3Collapsed2DVisualizer persistent path: " + Application.persistentDataPath);
        Debug.Log("M3Collapsed2DVisualizer loading CSV from: " + path);

        if (!File.Exists(path))
        {
            Debug.LogError("M3Collapsed2DVisualizer: CSV not found at: " + path);
            return;
        }

        List<SignalSample> samples = LoadSamples(path);

        if (samples.Count == 0)
        {
            Debug.LogWarning("M3Collapsed2DVisualizer: No samples loaded.");
            return;
        }

        List<VoxelValue> voxels = RawSamplesToVoxelField(samples);

        if (voxels.Count == 0)
        {
            Debug.LogWarning("M3Collapsed2DVisualizer: No voxels generated.");
            return;
        }

        Dictionary<Vector2Int, ColumnAccum> collapsedGrid = CollapseVoxelFieldToFloorGrid(voxels);
        CreateTiles(collapsedGrid);

        Debug.Log(
            "M3Collapsed2DVisualizer: Collapsed " + voxels.Count +
            " voxels into " + collapsedGrid.Count +
            " floor tiles using " + averagingMode + "."
        );
    }

    private List<SignalSample> LoadSamples(string path)
    {
        List<SignalSample> samples = new List<SignalSample>();
        string[] lines = File.ReadAllLines(path);

        if (lines.Length <= 1)
        {
            Debug.LogWarning("M3Collapsed2DVisualizer: CSV has no data rows.");
            return samples;
        }

        string[] headers = SplitCsvLine(lines[0]);

        int xIndex = FindColumn(headers, "world_pos_x", "pos_x");
        int yIndex = FindColumn(headers, "world_pos_y", "pos_y");
        int zIndex = FindColumn(headers, "world_pos_z", "pos_z");
        int rssiIndex = FindColumn(headers, "rssi_dbm", "rssi", "rssiDbm");

        if (xIndex < 0 || yIndex < 0 || zIndex < 0 || rssiIndex < 0)
        {
            Debug.LogError("M3Collapsed2DVisualizer: CSV missing required columns. Need world_pos_x/world_pos_y/world_pos_z or pos_x/pos_y/pos_z and rssi_dbm.");
            Debug.LogError("M3Collapsed2DVisualizer headers found: " + string.Join(" | ", headers));
            return samples;
        }

        for (int row = 1; row < lines.Length; row++)
        {
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

            SignalSample sample = new SignalSample
            {
                position = new Vector3(x, y, z),
                rssi = rssi
            };

            samples.Add(sample);
        }

        return samples;
    }

    private List<VoxelValue> RawSamplesToVoxelField(List<SignalSample> samples)
    {
        List<VoxelValue> voxels = new List<VoxelValue>();

        if (voxelSize <= 0f)
        {
            Debug.LogError("M3Collapsed2DVisualizer: voxelSize must be greater than 0.");
            return voxels;
        }

        float minX = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float minZ = float.PositiveInfinity;
        float maxZ = float.NegativeInfinity;

        foreach (SignalSample sample in samples)
        {
            minX = Mathf.Min(minX, sample.position.x);
            maxX = Mathf.Max(maxX, sample.position.x);
            minZ = Mathf.Min(minZ, sample.position.z);
            maxZ = Mathf.Max(maxZ, sample.position.z);
        }

        int minGX = Mathf.FloorToInt(minX / voxelSize);
        int maxGX = Mathf.FloorToInt(maxX / voxelSize);
        int minGY = Mathf.FloorToInt(Mathf.Min(verticalMinY, verticalMaxY) / voxelSize);
        int maxGY = Mathf.FloorToInt(Mathf.Max(verticalMinY, verticalMaxY) / voxelSize);
        int minGZ = Mathf.FloorToInt(minZ / voxelSize);
        int maxGZ = Mathf.FloorToInt(maxZ / voxelSize);

        for (int gx = minGX; gx <= maxGX; gx++)
        {
            for (int gy = minGY; gy <= maxGY; gy++)
            {
                for (int gz = minGZ; gz <= maxGZ; gz++)
                {
                    if (maxVoxels > 0 && voxels.Count >= maxVoxels)
                    {
                        Debug.LogWarning("M3Collapsed2DVisualizer: Hit maxVoxels limit of " + maxVoxels + ".");
                        return voxels;
                    }

                    Vector3 center = new Vector3(
                        (gx + 0.5f) * voxelSize,
                        (gy + 0.5f) * voxelSize,
                        (gz + 0.5f) * voxelSize
                    );

                    SignalSample nearest = FindNearestSample(center, samples, out float nearestDistance);

                    if (nearest == null || nearestDistance > maxNearestSampleDistance)
                    {
                        continue;
                    }

                    VoxelValue voxel = new VoxelValue
                    {
                        cell = new Vector3Int(gx, gy, gz),
                        center = center,
                        rssi = nearest.rssi
                    };

                    voxels.Add(voxel);
                }
            }
        }

        return voxels;
    }

    private Dictionary<Vector2Int, ColumnAccum> CollapseVoxelFieldToFloorGrid(List<VoxelValue> voxels)
    {
        Dictionary<Vector2Int, ColumnAccum> grid = new Dictionary<Vector2Int, ColumnAccum>();

        foreach (VoxelValue voxel in voxels)
        {
            Vector2Int column = new Vector2Int(voxel.cell.x, voxel.cell.z);

            if (!grid.ContainsKey(column))
            {
                grid[column] = new ColumnAccum();
            }

            grid[column].Add(voxel.rssi, HeightWeight(voxel.center.y));
        }

        return grid;
    }

    private float HeightWeight(float y)
    {
        if (averagingMode == HeightAveragingMode.EqualWeight)
        {
            return 1f;
        }

        float sigma = Mathf.Max(semanticHeightSigma, 0.001f);
        float normalizedDistance = (y - semanticHeightCenter) / sigma;
        return Mathf.Exp(-0.5f * normalizedDistance * normalizedDistance);
    }

    private SignalSample FindNearestSample(Vector3 position, List<SignalSample> samples, out float nearestDistance)
    {
        SignalSample nearest = null;
        float nearestSqrDistance = float.PositiveInfinity;

        foreach (SignalSample sample in samples)
        {
            float sqrDistance = (sample.position - position).sqrMagnitude;

            if (sqrDistance < nearestSqrDistance)
            {
                nearestSqrDistance = sqrDistance;
                nearest = sample;
            }
        }

        nearestDistance = Mathf.Sqrt(nearestSqrDistance);
        return nearest;
    }

    private void CreateTiles(Dictionary<Vector2Int, ColumnAccum> grid)
    {
        foreach (KeyValuePair<Vector2Int, ColumnAccum> kvp in grid)
        {
            Vector2Int cell = kvp.Key;
            float collapsedRssi = kvp.Value.Average();

            float x = (cell.x + 0.5f) * voxelSize;
            float z = (cell.y + 0.5f) * voxelSize;

            GameObject tile = GameObject.CreatePrimitive(PrimitiveType.Quad);
            tile.name = "M3_COLLAPSED_TILE_" + cell.x + "_" + cell.y + "_avg_" + collapsedRssi.ToString("F1", CultureInfo.InvariantCulture);

            tile.transform.SetParent(transform, false);
            tile.transform.position = new Vector3(x, floorY, z);
            tile.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            tile.transform.localScale = new Vector3(voxelSize, voxelSize, 1f);

            Collider collider = tile.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }

            Renderer renderer = tile.GetComponent<Renderer>();
            if (renderer != null)
            {
                Material mat = CreateTileMaterial();
                mat.color = RssiToColor(collapsedRssi);
                renderer.material = mat;
            }
        }
    }

    private Material CreateTileMaterial()
    {
        if (tileMaterial != null)
        {
            return new Material(tileMaterial);
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
