using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

public class M4VoxelCloudVisualizer : MonoBehaviour
{
    [Header("CSV")]
    public string fileName = "rf_trajectory_log.csv";

    [Header("Coordinate Frame")]
    public Transform coordinateFrameRoot;

    [Header("Voxel Grid")]
    public float voxelSize = 0.5f;
    public float verticalMinY = 0f;
    public float verticalMaxY = 2f;
    public int maxVoxels = 5000;
    public float maxNearestSampleDistance = 2f;

    [Header("RSSI")]
    public float minRssi = -70f;
    public float maxRssi = -40f;
    public float alpha = 0.35f;

    private class SignalSample
    {
        public Vector3 position;
        public float rssi;
    }

    IEnumerator Start()
    {
        yield return RoomAlignmentManager.WaitForPlaybackDecision();
        if (RoomAlignmentManager.Instance == null || !RoomAlignmentManager.Instance.AttachVisualization(transform, "M4"))
        {
            yield break;
        }
        coordinateFrameRoot = null;
        GenerateVoxelCloud();
    }

    [ContextMenu("Regenerate Voxel Cloud")]
    public void GenerateVoxelCloud()
    {
        if (Application.isPlaying && (RoomAlignmentManager.Instance == null ||
            RoomAlignmentManager.Instance.State != RoomAlignmentManager.PlaybackState.Ready))
        {
            return;
        }
        ClearChildren();
        ResolveCoordinateFrameRoot();

        string path = Path.Combine(Application.persistentDataPath, fileName);

        Debug.Log("M4VoxelCloudVisualizer persistent path: " + Application.persistentDataPath);
        Debug.Log("M4VoxelCloudVisualizer loading CSV from: " + path);

        if (!File.Exists(path))
        {
            Debug.LogError("M4VoxelCloudVisualizer: CSV not found at: " + path);
            return;
        }

        List<SignalSample> samples = LoadSamples(path);

        if (samples.Count == 0)
        {
            Debug.LogWarning("M4VoxelCloudVisualizer: No samples loaded.");
            return;
        }

        CreateVoxelGrid(samples);
    }

    private List<SignalSample> LoadSamples(string path)
    {
        List<SignalSample> samples = new List<SignalSample>();
        string[] lines = File.ReadAllLines(path);

        if (lines.Length <= 1)
        {
            Debug.LogWarning("M4VoxelCloudVisualizer: CSV has no data rows.");
            return samples;
        }

        string[] headers = SplitCsvLine(lines[0]);

        int xIndex = FindColumn(headers, "reference_local_pos_x");
        int yIndex = FindColumn(headers, "reference_local_pos_y");
        int zIndex = FindColumn(headers, "reference_local_pos_z");
        int rssiIndex = FindColumn(headers, "rssi_dbm", "rssi", "rssiDbm");

        if (xIndex < 0 || yIndex < 0 || zIndex < 0 || rssiIndex < 0)
        {
            Debug.LogError(RoomAlignmentManager.LogPrefix + " M4 rejected legacy CSV: authoritative reference_local_pos_* columns are required.");
            Debug.LogError("M4VoxelCloudVisualizer headers found: " + string.Join(" | ", headers));
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

    private void CreateVoxelGrid(List<SignalSample> samples)
    {
        if (voxelSize <= 0f)
        {
            Debug.LogError("M4VoxelCloudVisualizer: voxelSize must be greater than 0.");
            return;
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

        minX = Mathf.Floor(minX / voxelSize) * voxelSize;
        maxX = Mathf.Ceil(maxX / voxelSize) * voxelSize;
        float minY = Mathf.Min(verticalMinY, verticalMaxY);
        float maxY = Mathf.Max(verticalMinY, verticalMaxY);
        minZ = Mathf.Floor(minZ / voxelSize) * voxelSize;
        maxZ = Mathf.Ceil(maxZ / voxelSize) * voxelSize;

        int voxelCount = 0;

        for (float x = minX; x <= maxX; x += voxelSize)
        {
            for (float y = minY; y <= maxY; y += voxelSize)
            {
                for (float z = minZ; z <= maxZ; z += voxelSize)
                {
                    if (maxVoxels > 0 && voxelCount >= maxVoxels)
                    {
                        Debug.LogWarning("M4VoxelCloudVisualizer: Hit maxVoxels limit of " + maxVoxels + ".");
                        Debug.Log("M4VoxelCloudVisualizer: Generated " + voxelCount + " voxels.");
                        return;
                    }

                    Vector3 center = new Vector3(
                        x + voxelSize * 0.5f,
                        y + voxelSize * 0.5f,
                        z + voxelSize * 0.5f
                    );

                    SignalSample nearest = FindNearestSample(center, samples, out float nearestDistance);

                    if (nearest == null || nearestDistance > maxNearestSampleDistance)
                    {
                        continue;
                    }

                    CreateVoxel(center, nearest.rssi);
                    voxelCount++;
                }
            }
        }

        Debug.Log("M4VoxelCloudVisualizer: Generated " + voxelCount + " voxels.");
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

    private void CreateVoxel(Vector3 center, float rssi)
    {
        GameObject voxel = GameObject.CreatePrimitive(PrimitiveType.Cube);
        voxel.name = "M4_VOXEL_RSSI_" + rssi.ToString("F1", CultureInfo.InvariantCulture);

        voxel.transform.SetParent(transform, false);
        voxel.transform.localPosition = ToModeLocalPosition(center);
        voxel.transform.localRotation = ToModeLocalRotation(Quaternion.identity);
        voxel.transform.localScale = Vector3.one * voxelSize;

        Collider collider = voxel.GetComponent<Collider>();
        if (collider != null)
        {
            Destroy(collider);
        }

        Renderer renderer = voxel.GetComponent<Renderer>();
        if (renderer != null)
        {
            Material mat = CreateTransparentMaterial();
            mat.color = RssiToColor(rssi);
            renderer.material = mat;
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
            Debug.Log("M4VoxelCloudVisualizer: Auto-assigned coordinateFrameRoot to RoomAnchor.");
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
