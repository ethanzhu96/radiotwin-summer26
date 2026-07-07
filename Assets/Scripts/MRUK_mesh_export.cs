using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using Meta.XR.MRUtilityKit;

public class MRUKMeshExporter : MonoBehaviour
{
    public string fileName = "quest_room_mesh.obj";
    public float exportDelaySeconds = 5f;
    public float roomLoadTimeoutSeconds = 30f;
    public KeyCode exportKey = KeyCode.M;
    public bool exportRelativeToMRUKRoom = true;
    public string roomAnchorObjectName = "RoomAnchor";

    [Header("Status Display")]
    public bool showStatusDisplay = true;
    public Transform statusAnchor;
    public Vector3 statusDisplayLocalPosition = new Vector3(0.45f, 0.18f, 1.25f);
    public float statusTextSize = 0.016f;
    public float statusMessageSeconds = 4f;

    private bool exported = false;
    private TextMesh statusText;
    private string statusMessage = "";
    private float statusMessageUntil = 0f;
    private Color statusColor = Color.white;

    IEnumerator Start()
    {
        Debug.Log("MRUKMeshExporter started. Waiting for MRUK room mesh before export...");
        ShowStatusMessage("MESH WAIT", Color.yellow);
        yield return new WaitForSeconds(exportDelaySeconds);
        yield return WaitForRoomThenExport();
    }

    void Update()
    {
        UpdateStatusDisplay();

        if (Input.GetKeyDown(exportKey))
        {
            exported = false;
            ShowStatusMessage("MESH EXPORT", Color.yellow);
            ExportAllMeshes();
        }
    }

    IEnumerator WaitForRoomThenExport()
    {
        float startTime = Time.unscaledTime;

        while (Time.unscaledTime - startTime < roomLoadTimeoutSeconds)
        {
            if (MRUK.Instance != null && MRUK.Instance.GetCurrentRoom() != null && CountExportableVertices() > 0)
            {
                ExportAllMeshes();
                yield break;
            }

            yield return new WaitForSeconds(1f);
        }

        Debug.LogError("MRUKMeshExporter timed out waiting for MRUK room mesh. Make sure Scene API/room setup is loaded on the Quest.");
        ShowStatusMessage("MESH FAILED", Color.red);
    }

    [ContextMenu("Export All Meshes")]
    public void ExportAllMeshes()
    {
        if (exported) return;

        MeshFilter[] meshFilters = FindObjectsOfType<MeshFilter>();
        Transform exportAnchor = ResolveExportAnchor();

        StringBuilder obj = new StringBuilder();
        int vertexOffset = 0;
        int meshCount = 0;

        obj.AppendLine("# Quest Room Mesh Export");
        obj.AppendLine("# Exported from Unity scene");
        obj.AppendLine("# Coordinate frame: " + (exportAnchor != null ? exportAnchor.name : "Unity world"));

        foreach (MeshFilter mf in meshFilters)
        {
            if (!ShouldExportMeshFilter(mf))
            {
                continue;
            }

            Mesh mesh = mf.sharedMesh;

            if (mesh == null) continue;
            if (mesh.vertexCount == 0) continue;

            Transform t = mf.transform;

            obj.AppendLine("o " + mf.gameObject.name);

            foreach (Vector3 v in mesh.vertices)
            {
                Vector3 world = t.TransformPoint(v);
                Vector3 exported = exportAnchor != null ? exportAnchor.InverseTransformPoint(world) : world;
                obj.AppendLine($"v {exported.x} {exported.y} {exported.z}");
            }

            int[] triangles = mesh.triangles;

            for (int i = 0; i < triangles.Length; i += 3)
            {
                int a = triangles[i] + 1 + vertexOffset;
                int b = triangles[i + 1] + 1 + vertexOffset;
                int c = triangles[i + 2] + 1 + vertexOffset;

                obj.AppendLine($"f {a} {b} {c}");
            }

            vertexOffset += mesh.vertexCount;
            meshCount++;
        }

        if (meshCount == 0 || vertexOffset == 0)
        {
            Debug.LogError("MRUKMeshExporter found no mesh vertices to export. OBJ was not written.");
            ShowStatusMessage("MESH FAILED", Color.red);
            return;
        }

        string path = Path.Combine(Application.persistentDataPath, fileName);
        File.WriteAllText(path, obj.ToString());
        exported = true;
        ShowStatusMessage("MESH EXPORTED", Color.green);

        Debug.Log("EXPORTED ROOM MESH TO: " + path);
        Debug.Log("Exported mesh count: " + meshCount);
        Debug.Log("Total vertices: " + vertexOffset);
    }

    int CountExportableVertices()
    {
        int count = 0;
        MeshFilter[] meshFilters = FindObjectsOfType<MeshFilter>();

        foreach (MeshFilter mf in meshFilters)
        {
            if (ShouldExportMeshFilter(mf) && mf.sharedMesh != null)
            {
                count += mf.sharedMesh.vertexCount;
            }
        }

        return count;
    }

    bool ShouldExportMeshFilter(MeshFilter meshFilter)
    {
        if (meshFilter == null || meshFilter.sharedMesh == null)
        {
            return false;
        }

        if (meshFilter.GetComponentInParent<MRUKAnchor>() != null)
        {
            return true;
        }

        string objectName = meshFilter.gameObject.name;
        string parentName = meshFilter.transform.parent != null ? meshFilter.transform.parent.name : "";

        return objectName.Contains("EffectMesh") ||
            objectName.Contains("WALL") ||
            objectName.Contains("FLOOR") ||
            objectName.Contains("CEILING") ||
            parentName.Contains("EffectMesh") ||
            parentName.Contains("WALL") ||
            parentName.Contains("FLOOR") ||
            parentName.Contains("CEILING");
    }

    Transform ResolveExportAnchor()
    {
        if (exportRelativeToMRUKRoom && MRUK.Instance != null && MRUK.Instance.GetCurrentRoom() != null)
        {
            Transform roomTransform = MRUK.Instance.GetCurrentRoom().transform;
            Debug.Log("MRUKMeshExporter exporting relative to MRUK room anchor: " + roomTransform.name);
            return roomTransform;
        }

        GameObject roomAnchor = GameObject.Find(roomAnchorObjectName);

        if (roomAnchor != null)
        {
            Debug.Log("MRUKMeshExporter exporting relative to RoomAnchor object: " + roomAnchor.name);
            return roomAnchor.transform;
        }

        Debug.LogWarning("MRUKMeshExporter did not find a room anchor. Exporting in Unity world coordinates.");
        return null;
    }

    void EnsureStatusDisplay()
    {
        if (!showStatusDisplay || statusText != null)
        {
            return;
        }

        Transform anchor = ResolveStatusAnchor();

        if (anchor == null)
        {
            return;
        }

        GameObject statusObject = new GameObject("MRUKMeshExporter_StatusDisplay");
        statusObject.transform.SetParent(anchor, false);
        statusObject.transform.localPosition = statusDisplayLocalPosition;
        statusObject.transform.localRotation = Quaternion.identity;

        statusText = statusObject.AddComponent<TextMesh>();
        statusText.anchor = TextAnchor.UpperRight;
        statusText.alignment = TextAlignment.Right;
        statusText.fontSize = 64;
        statusText.characterSize = statusTextSize;
        statusText.color = statusColor;
    }

    Transform ResolveStatusAnchor()
    {
        if (statusAnchor != null)
        {
            return statusAnchor;
        }

        Camera mainCamera = Camera.main;

        if (mainCamera != null)
        {
            statusAnchor = mainCamera.transform;
        }

        return statusAnchor;
    }

    void UpdateStatusDisplay()
    {
        EnsureStatusDisplay();

        if (statusText == null)
        {
            return;
        }

        statusText.transform.localPosition = statusDisplayLocalPosition;
        statusText.transform.localRotation = Quaternion.identity;
        statusText.characterSize = statusTextSize;

        if (Time.unscaledTime < statusMessageUntil && !string.IsNullOrEmpty(statusMessage))
        {
            statusText.text = statusMessage;
            statusText.color = statusColor;
        }
        else
        {
            statusText.text = "";
        }
    }

    void ShowStatusMessage(string message, Color color)
    {
        statusMessage = message;
        statusColor = color;
        statusMessageUntil = Time.unscaledTime + statusMessageSeconds;
        UpdateStatusDisplay();
    }
}
