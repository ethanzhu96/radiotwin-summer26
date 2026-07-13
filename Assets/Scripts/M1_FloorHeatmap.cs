using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

public class RssiFloorHeatmap : MonoBehaviour
{
    [Header("CSV")]
    public string fileName = "rf_trajectory_log.csv";

    [Header("Coordinate Frame")]
    public Transform coordinateFrameRoot;

    [Header("Grid Settings")]
    public float cellSize = 0.5f;
    public float floorY = 0.01f;

    [Header("RSSI Normalization")]
    public float minRssi = -70f;
    public float maxRssi = -40f;

    [Header("Tile Rendering")]
    public Material tileMaterial;
    public bool clearOldTilesOnStart = true;

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
            if (count == 0) return 0f;
            return sum / count;
        }
    }

    IEnumerator Start()
    {
        yield return RoomAlignmentManager.WaitForPlaybackDecision();
        if (RoomAlignmentManager.Instance == null || !RoomAlignmentManager.Instance.AttachVisualization(transform, "M1"))
        {
            yield break;
        }
        coordinateFrameRoot = null;
        GenerateFloorHeatmap();
    }

    [ContextMenu("Regenerate Floor Heatmap")]
    public void GenerateFloorHeatmap()
    {
        if (Application.isPlaying && (RoomAlignmentManager.Instance == null ||
            RoomAlignmentManager.Instance.State != RoomAlignmentManager.PlaybackState.Ready))
        {
            return;
        }
        ResolveCoordinateFrameRoot();

        if (clearOldTilesOnStart)
        {
            ClearOldTiles();
        }

        string path = Path.Combine(Application.persistentDataPath, fileName);

        Debug.Log("Heatmap persistent path: " + Application.persistentDataPath);
        Debug.Log("Loading heatmap CSV from: " + path);

        if (!File.Exists(path))
        {
            Debug.LogError("Heatmap CSV not found: " + path);
            return;
        }

        Dictionary<Vector2Int, CellAccum> grid = LoadAndBinCsv(path);

        Debug.Log($"Generated heatmap with {grid.Count} occupied grid cells.");

        CreateTiles(grid);
    }

    Dictionary<Vector2Int, CellAccum> LoadAndBinCsv(string path)
    {
        Dictionary<Vector2Int, CellAccum> grid = new Dictionary<Vector2Int, CellAccum>();

        string[] lines = File.ReadAllLines(path);

        if (lines.Length <= 1)
        {
            Debug.LogWarning("CSV has no data rows.");
            return grid;
        }

        string[] headers = lines[0].Split(',');

        int ix = FindColumn(headers, "reference_local_pos_x");
        int iz = FindColumn(headers, "reference_local_pos_z");
        int irssi = Array.IndexOf(headers, "rssi_dbm");

        if (ix < 0 || iz < 0 || irssi < 0)
        {
            Debug.LogError(RoomAlignmentManager.LogPrefix + " M1 rejected legacy CSV: authoritative reference_local_pos_* columns are required.");
            Debug.LogError("Headers found: " + string.Join(" | ", headers));
            return grid;
        }

        for (int row = 1; row < lines.Length; row++)
        {
            if (string.IsNullOrWhiteSpace(lines[row]))
                continue;

            string[] cols = lines[row].Split(',');

            int maxRequiredIndex = Mathf.Max(Mathf.Max(ix, iz), irssi);
            if (cols.Length <= maxRequiredIndex)
                continue;

            float x = ParseFloat(cols[ix]);
            float z = ParseFloat(cols[iz]);
            float rssi = ParseFloat(cols[irssi]);

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

    void CreateTiles(Dictionary<Vector2Int, CellAccum> grid)
    {
        foreach (KeyValuePair<Vector2Int, CellAccum> kvp in grid)
        {
            Vector2Int cell = kvp.Key;
            float avgRssi = kvp.Value.Average();

            float x = (cell.x + 0.5f) * cellSize;
            float z = (cell.y + 0.5f) * cellSize;

            GameObject tile = GameObject.CreatePrimitive(PrimitiveType.Quad);
            tile.name = $"RSSI_TILE_{cell.x}_{cell.y}_avg_{avgRssi:F1}";

            tile.transform.SetParent(transform, false);
            tile.transform.localPosition = ToModeLocalPosition(new Vector3(x, floorY, z));
            tile.transform.localRotation = ToModeLocalRotation(Quaternion.Euler(90f, 0f, 0f));
            tile.transform.localScale = new Vector3(cellSize, cellSize, 1f);

            Collider collider = tile.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }

            Renderer renderer = tile.GetComponent<Renderer>();

            if (renderer != null)
            {
                Material mat;

                if (tileMaterial != null)
                {
                    mat = new Material(tileMaterial);
                }
                else
                {
                    mat = new Material(Shader.Find("Standard"));
                }

                mat.color = RssiToColor(avgRssi);
                renderer.material = mat;
            }
        }
    }

    void ClearOldTiles()
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

    int FindColumn(string[] headers, params string[] possibleNames)
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

    string CleanHeader(string s)
    {
        return s.Trim().Replace("\"", "").ToLowerInvariant();
    }

    Vector3 ToModeLocalPosition(Vector3 coordinateFrameLocalPosition)
    {
        if (coordinateFrameRoot == null)
        {
            return coordinateFrameLocalPosition;
        }

        Vector3 worldPosition = coordinateFrameRoot.TransformPoint(coordinateFrameLocalPosition);
        return transform.InverseTransformPoint(worldPosition);
    }

    void ResolveCoordinateFrameRoot()
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
            Debug.Log("RssiFloorHeatmap: Auto-assigned coordinateFrameRoot to RoomAnchor.");
        }
    }

    Quaternion ToModeLocalRotation(Quaternion coordinateFrameLocalRotation)
    {
        if (coordinateFrameRoot == null)
        {
            return coordinateFrameLocalRotation;
        }

        Quaternion worldRotation = coordinateFrameRoot.rotation * coordinateFrameLocalRotation;
        return Quaternion.Inverse(transform.rotation) * worldRotation;
    }

    float ParseFloat(string s)
    {
        s = s.Replace("\"", "").Trim();

        if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
        {
            return value;
        }

        Debug.LogWarning("Failed to parse float from: " + s);
        return 0f;
    }

    Color RssiToColor(float rssi)
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

        c.a = 1.0f;
        return c;
    }
}
