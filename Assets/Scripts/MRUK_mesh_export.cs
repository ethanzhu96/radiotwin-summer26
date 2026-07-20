using System.Collections;
using System;
using System.IO;
using System.Text;
using UnityEngine;
using Meta.XR.MRUtilityKit;

public class MRUKMeshExporter : MonoBehaviour
{
    public string fileName = "quest_room_mesh.obj";
    public string metadataFileName = RoomAlignmentManager.DefaultMetadataFileName;
    public string trajectoryFileName = "rf_trajectory_log.csv";
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
        Transform exportAnchor = ResolveExportAnchor(
            out Guid roomUuid,
            out Guid referenceAnchorUuid,
            out string referenceAnchorLabel,
            out Quaternion referenceFrameLocalRotation);

        if (exportAnchor == null || roomUuid == Guid.Empty || referenceAnchorUuid == Guid.Empty)
        {
            Debug.LogError(RoomAlignmentManager.LogPrefix + " Mesh export aborted: a valid initialized MRUK room UUID is required.");
            ShowStatusMessage("ROOM REQUIRED", Color.red);
            return;
        }

        if (!ExistingMetadataMatchesFrame(roomUuid, referenceAnchorUuid))
        {
            ShowStatusMessage("ROOM MISMATCH", Color.red);
            return;
        }
        referenceFrameLocalRotation = GetMetadataFrameRotationOrDefault(referenceFrameLocalRotation);
        Quaternion referenceFrameWorldRotation = exportAnchor.rotation * referenceFrameLocalRotation;

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
                Vector3 exported = Quaternion.Inverse(referenceFrameWorldRotation) * (world - exportAnchor.position);
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
        UpdateDatasetMetadata(roomUuid, referenceAnchorUuid, referenceAnchorLabel, referenceFrameLocalRotation);
        exported = true;
        ShowStatusMessage("MESH EXPORTED", Color.green);

        Debug.Log(RoomAlignmentManager.LogPrefix + " Mesh path: " + path);
        Debug.Log(RoomAlignmentManager.LogPrefix + " Saved room UUID: " + roomUuid);
        Debug.Log(RoomAlignmentManager.LogPrefix + " Saved reference anchor UUID: " + referenceAnchorUuid);
        Debug.Log(RoomAlignmentManager.LogPrefix + " OBJ vertices saved in matched MRUK scene-anchor-local coordinates.");
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

        if (objectName.StartsWith("M0_RSSI_POINT_", StringComparison.Ordinal))
        {
            return false;
        }

        return objectName.Contains("EffectMesh") ||
            objectName.Contains("WALL") ||
            objectName.Contains("FLOOR") ||
            objectName.Contains("CEILING") ||
            parentName.Contains("EffectMesh") ||
            parentName.Contains("WALL") ||
            parentName.Contains("FLOOR") ||
            parentName.Contains("CEILING");
    }

    Transform ResolveExportAnchor(
        out Guid roomUuid,
        out Guid referenceAnchorUuid,
        out string referenceAnchorLabel,
        out Quaternion referenceFrameLocalRotation)
    {
        roomUuid = Guid.Empty;
        referenceAnchorUuid = Guid.Empty;
        referenceAnchorLabel = "";
        referenceFrameLocalRotation = Quaternion.identity;
        if (exportRelativeToMRUKRoom && MRUK.Instance != null && MRUK.Instance.IsInitialized && MRUK.Instance.GetCurrentRoom() != null)
        {
            MRUKRoom room = MRUK.Instance.GetCurrentRoom();
            roomUuid = room.Anchor.Uuid;
            if (roomUuid == Guid.Empty)
            {
                Debug.LogError(RoomAlignmentManager.LogPrefix + " Current room anchor UUID is empty.");
                return null;
            }
            if (!RoomAlignmentManager.TryGetCaptureReferenceAnchor(room, out MRUKAnchor referenceAnchor))
            {
                Debug.LogError(RoomAlignmentManager.LogPrefix + " No valid global-mesh or floor scene anchor is available for mesh export.");
                return null;
            }
            referenceAnchorUuid = referenceAnchor.Anchor.Uuid;
            referenceAnchorLabel = referenceAnchor.Label.ToString();
            referenceFrameLocalRotation = RoomAlignmentManager.GetUprightReferenceLocalRotation(referenceAnchor);
            Debug.Log(RoomAlignmentManager.LogPrefix + " Exporting mesh relative to scene anchor " + referenceAnchor.name +
                " roomUUID=" + roomUuid + " anchorUUID=" + referenceAnchorUuid + " label=" + referenceAnchorLabel);
            return referenceAnchor.transform;
        }
        Debug.LogError(RoomAlignmentManager.LogPrefix + " MRUK is not initialized or no current room is available. World-space fallback is disabled.");
        return null;
    }

    void UpdateDatasetMetadata(
        Guid roomUuid,
        Guid referenceAnchorUuid,
        string referenceAnchorLabel,
        Quaternion referenceFrameLocalRotation)
    {
        string metadataPath = Path.Combine(Application.persistentDataPath, metadataFileName);
        RoomAlignmentMetadata metadata = null;
        if (File.Exists(metadataPath))
        {
            metadata = JsonUtility.FromJson<RoomAlignmentMetadata>(File.ReadAllText(metadataPath));
            if (metadata != null && !string.IsNullOrWhiteSpace(metadata.roomUuid) &&
                (!Guid.TryParse(metadata.roomUuid, out Guid metadataUuid) || metadataUuid != roomUuid))
            {
                Debug.LogError(RoomAlignmentManager.LogPrefix + " Mesh room UUID does not match trajectory metadata. Mesh metadata was not updated.");
                return;
            }
        }
        if (metadata == null)
        {
            metadata = new RoomAlignmentMetadata
            {
                roomUuid = roomUuid.ToString(),
                trajectoryFile = trajectoryFileName,
                captureTimestampUtc = DateTime.UtcNow.ToString("o")
            };
        }
        metadata.formatVersion = 3;
        metadata.coordinateSpace = RoomAlignmentManager.CoordinateSpace;
        metadata.roomUuid = roomUuid.ToString();
        metadata.referenceAnchorUuid = referenceAnchorUuid.ToString();
        metadata.referenceAnchorLabel = referenceAnchorLabel;
        metadata.referenceFrameLocalRotX = referenceFrameLocalRotation.x;
        metadata.referenceFrameLocalRotY = referenceFrameLocalRotation.y;
        metadata.referenceFrameLocalRotZ = referenceFrameLocalRotation.z;
        metadata.referenceFrameLocalRotW = referenceFrameLocalRotation.w;
        if (string.IsNullOrWhiteSpace(metadata.trajectoryFile))
        {
            metadata.trajectoryFile = trajectoryFileName;
        }
        metadata.meshFile = fileName;
        File.WriteAllText(metadataPath, JsonUtility.ToJson(metadata, true));
        Debug.Log(RoomAlignmentManager.LogPrefix + " Metadata path: " + metadataPath);
    }

    bool ExistingMetadataMatchesFrame(Guid roomUuid, Guid referenceAnchorUuid)
    {
        string metadataPath = Path.Combine(Application.persistentDataPath, metadataFileName);
        if (!File.Exists(metadataPath))
        {
            return true;
        }

        RoomAlignmentMetadata metadata = JsonUtility.FromJson<RoomAlignmentMetadata>(File.ReadAllText(metadataPath));
        if (metadata == null || string.IsNullOrWhiteSpace(metadata.roomUuid))
        {
            return true;
        }

        if (!Guid.TryParse(metadata.roomUuid, out Guid metadataUuid) || metadataUuid != roomUuid)
        {
            Debug.LogError(RoomAlignmentManager.LogPrefix + " Mesh export aborted: current room UUID " + roomUuid +
                " does not match trajectory metadata UUID " + metadata.roomUuid + ".");
            return false;
        }

        if (metadata.formatVersion >= 3 && !string.IsNullOrWhiteSpace(metadata.referenceAnchorUuid) &&
            (!Guid.TryParse(metadata.referenceAnchorUuid, out Guid metadataAnchorUuid) || metadataAnchorUuid != referenceAnchorUuid))
        {
            Debug.LogError(RoomAlignmentManager.LogPrefix + " Mesh export aborted: current reference anchor UUID " +
                referenceAnchorUuid + " does not match trajectory metadata anchor UUID " + metadata.referenceAnchorUuid + ".");
            return false;
        }

        return true;
    }

    Quaternion GetMetadataFrameRotationOrDefault(Quaternion fallback)
    {
        string metadataPath = Path.Combine(Application.persistentDataPath, metadataFileName);
        if (!File.Exists(metadataPath))
        {
            return fallback;
        }

        RoomAlignmentMetadata metadata = JsonUtility.FromJson<RoomAlignmentMetadata>(File.ReadAllText(metadataPath));
        if (metadata == null || metadata.formatVersion < 3)
        {
            return fallback;
        }

        Quaternion saved = new Quaternion(
            metadata.referenceFrameLocalRotX,
            metadata.referenceFrameLocalRotY,
            metadata.referenceFrameLocalRotZ,
            metadata.referenceFrameLocalRotW);
        return Quaternion.Dot(saved, saved) >= 0.9f ? Quaternion.Normalize(saved) : fallback;
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
