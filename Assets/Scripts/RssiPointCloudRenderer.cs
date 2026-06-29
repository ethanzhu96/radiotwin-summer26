using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

public class RssiPointCloudRenderer : MonoBehaviour
{
    [Header("CSV")]
    public string fileName = "rf_trajectory_log.csv";

    [Header("Rendering")]
    public Mesh pointMesh;
    public Material pointMaterial;
    public float pointScale = 0.25f;

    [Header("Debug")]
    public bool spawnDebugObjects = true;
    public int maxDebugObjects = 5;

    [Header("RSSI Normalization")]
    public float minRssi = -90f;
    public float maxRssi = -30f;

    private readonly List<Matrix4x4> matrices = new List<Matrix4x4>();
    private readonly List<Vector4> colors = new List<Vector4>();

    private MaterialPropertyBlock propertyBlock;

    private const int BatchSize = 1023;

    void Start()
    {
        propertyBlock = new MaterialPropertyBlock();

        if (pointMaterial != null)
        {
            pointMaterial.enableInstancing = true;
            Debug.Log("Enabled GPU instancing on point material.");
        }
        else
        {
            Debug.LogError("Point Material is NULL. Assign RssiPointMaterial on RFPointCloudManager.");
        }

        if (pointMesh == null)
        {
            Debug.LogError("Point Mesh is NULL. Assign the Sphere mesh on RFPointCloudManager.");
        }

        string path = Path.Combine(Application.persistentDataPath, fileName);

        Debug.Log("Persistent path: " + Application.persistentDataPath);
        Debug.Log("Loading RF CSV from: " + path);

        if (!File.Exists(path))
        {
            Debug.LogError("CSV not found: " + path);
            return;
        }

        LoadCsv(path);

        Debug.Log($"Loaded {matrices.Count} RSSI points.");
    }

    void Update()
{
    if (pointMesh == null || pointMaterial == null || matrices.Count == 0)
        return;

    for (int start = 0; start < matrices.Count; start += BatchSize)
    {
        int count = Mathf.Min(BatchSize, matrices.Count - start);

        Matrix4x4[] matrixBatch = new Matrix4x4[count];
        Vector4[] colorBatch = new Vector4[count];

        for (int i = 0; i < count; i++)
        {
            matrixBatch[i] = matrices[start + i];
            colorBatch[i] = colors[start + i];
        }

        propertyBlock.Clear();
        propertyBlock.SetVectorArray("_Color", colorBatch);

        Graphics.DrawMeshInstanced(
            pointMesh,
            0,
            pointMaterial,
            matrixBatch,
            count,
            propertyBlock
        );
    }
}

    void LoadCsv(string path)
    {
        string[] lines = File.ReadAllLines(path);

        if (lines.Length <= 1)
        {
            Debug.LogWarning("CSV has no data rows.");
            return;
        }

        string[] headers = lines[0].Split(',');

        int ix = Array.IndexOf(headers, "pos_x");
        int iy = Array.IndexOf(headers, "pos_y");
        int iz = Array.IndexOf(headers, "pos_z");
        int irssi = Array.IndexOf(headers, "rssi_dbm");

        // Fallback for older CSV format
        if (ix < 0) ix = Array.IndexOf(headers, "world_pos_x");
        if (iy < 0) iy = Array.IndexOf(headers, "world_pos_y");
        if (iz < 0) iz = Array.IndexOf(headers, "world_pos_z");

        if (ix < 0 || iy < 0 || iz < 0 || irssi < 0)
        {
            Debug.LogError("CSV missing required columns. Need pos_x,pos_y,pos_z,rssi_dbm or world_pos_x/world_pos_y/world_pos_z/rssi_dbm.");
            Debug.LogError("Headers found: " + string.Join(" | ", headers));
            return;
        }

        for (int row = 1; row < lines.Length; row++)
        {
            if (string.IsNullOrWhiteSpace(lines[row]))
                continue;

            string[] cols = lines[row].Split(',');

            int maxRequiredIndex = Mathf.Max(Mathf.Max(ix, iy), Mathf.Max(iz, irssi));

            if (cols.Length <= maxRequiredIndex)
            {
                Debug.LogWarning($"Skipping row {row}: not enough columns.");
                continue;
            }

            float x = ParseFloat(cols[ix]);
            float y = ParseFloat(cols[iy]);
            float z = ParseFloat(cols[iz]);
            float rssi = ParseFloat(cols[irssi]);

            Vector3 pos = new Vector3(x, y, z);

            Debug.Log($"Point loaded at {pos} with RSSI {rssi}");

            if (spawnDebugObjects && row <= maxDebugObjects)
            {
                SpawnDebugSphere(pos, row);
            }

            Matrix4x4 matrix = Matrix4x4.TRS(
                pos,
                Quaternion.identity,
                Vector3.one * pointScale
            );

            matrices.Add(matrix);
            colors.Add(RssiToColor(rssi));
        }
    }

    void SpawnDebugSphere(Vector3 pos, int row)
    {
        GameObject debugSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        debugSphere.name = "DEBUG_RSSI_POINT_" + row;

        debugSphere.transform.position = pos;
        debugSphere.transform.localScale = Vector3.one * 0.4f;

        Renderer renderer = debugSphere.GetComponent<Renderer>();

        if (renderer != null && pointMaterial != null)
        {
            renderer.material = pointMaterial;
        }

        Debug.LogError($"Spawned debug sphere at {pos}");
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

    Vector4 RssiToColor(float rssi)
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