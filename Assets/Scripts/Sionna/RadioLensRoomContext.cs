using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Meta.XR.MRUtilityKit;
using UnityEngine;

[DefaultExecutionOrder(-450)]
public class RadioLensRoomContext : MonoBehaviour
{
    public Transform RoomAnchor { get; private set; }
    public string RoomId { get; private set; }
    public string LocalizationId { get; private set; }
    public string MeshVersion { get; private set; }
    public byte[] ObjBytes { get; private set; }
    public bool IsMeshRegistered { get; set; }
    public int Revision { get; private set; }
    public bool IsReady => RoomAnchor != null && !string.IsNullOrEmpty(RoomId) &&
        ObjBytes != null && ObjBytes.Length > 0 && !string.IsNullOrEmpty(MeshVersion);

    public event Action ContextChanged;

    private MRUK subscribedMruk;
    private Coroutine prepareRoutine;

    private IEnumerator Start()
    {
        while (MRUK.Instance == null) yield return null;
        Subscribe(MRUK.Instance);
        if (MRUK.Instance.IsInitialized && MRUK.Instance.GetCurrentRoom() != null) BeginPrepare();
    }

    private void Subscribe(MRUK mruk)
    {
        if (subscribedMruk == mruk) return;
        Unsubscribe();
        subscribedMruk = mruk;
        subscribedMruk.RegisterSceneLoadedCallback(BeginPrepare);
    }

    private void Unsubscribe()
    {
        if (subscribedMruk != null) subscribedMruk.SceneLoadedEvent.RemoveListener(BeginPrepare);
        subscribedMruk = null;
    }

    private void OnDestroy() { Unsubscribe(); }

    public void RefreshContext()
    {
        BeginPrepare();
    }

    private void BeginPrepare()
    {
        Revision++;
        RoomAnchor = null;
        RoomId = null;
        LocalizationId = Guid.NewGuid().ToString();
        MeshVersion = null;
        ObjBytes = null;
        IsMeshRegistered = false;
        ContextChanged?.Invoke();
        if (prepareRoutine != null) StopCoroutine(prepareRoutine);
        prepareRoutine = StartCoroutine(PrepareWhenGeometryReady(Revision));
    }

    private IEnumerator PrepareWhenGeometryReady(int revision)
    {
        float deadline = Time.unscaledTime + 30f;
        while (revision == Revision && Time.unscaledTime < deadline)
        {
            if (TryPrepare(out string error))
            {
                Debug.Log("[SionnaRoom] Ready room=" + RoomId + " localization=" + LocalizationId +
                    " mesh=" + MeshVersion + " bytes=" + ObjBytes.Length + ".");
                ContextChanged?.Invoke();
                prepareRoutine = null;
                yield break;
            }
            if (!string.IsNullOrEmpty(error) && error != "MRUK geometry is not ready.")
                Debug.LogWarning("[SionnaRoom] " + error);
            yield return new WaitForSecondsRealtime(.5f);
        }
        if (revision == Revision) Debug.LogError("[SionnaRoom] Timed out preparing current MRUK room geometry.");
        prepareRoutine = null;
    }

    private bool TryPrepare(out string error)
    {
        error = null;
        MRUKRoom room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
        if (room == null) { error = "MRUK room unavailable."; return false; }
        Guid uuid = room.Anchor.Uuid;
        if (uuid == Guid.Empty) { error = "MRUK room UUID unavailable."; return false; }
        if (!TryBuildRoomObj(room, room.transform, out byte[] bytes, out int meshCount, out int vertexCount, out error))
            return false;
        string version;
        try
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(bytes);
                StringBuilder hex = new StringBuilder(digest.Length * 2);
                for (int i = 0; i < digest.Length; i++) hex.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
                version = "sha256:" + hex;
            }
        }
        catch (Exception exception) { error = "Mesh SHA-256 failed: " + exception.Message; return false; }
        RoomAnchor = room.transform;
        RoomId = uuid.ToString();
        ObjBytes = bytes;
        MeshVersion = version;
        Debug.Log("[SionnaRoom] Exported " + meshCount + " MRUK meshes and " + vertexCount + " vertices in Room Anchor-local coordinates.");
        return true;
    }

    private static bool TryBuildRoomObj(MRUKRoom room, Transform roomAnchor, out byte[] bytes,
        out int meshCount, out int vertexCount, out string error)
    {
        bytes = null; meshCount = 0; vertexCount = 0; error = null;
        if (roomAnchor == null) { error = "MRUK Room Anchor missing."; return false; }
        MeshFilter[] filters = room.GetComponentsInChildren<MeshFilter>(false);
        HashSet<int> seen = new HashSet<int>();
        StringBuilder obj = new StringBuilder(65536);
        obj.AppendLine("# RadioLens Sionna MRUK room mesh");
        obj.AppendLine("# coordinate_frame=" + SionnaProtocol.CoordinateFrame);
        int vertexOffset = 0;
        for (int filterIndex = 0; filterIndex < filters.Length; filterIndex++)
        {
            MeshFilter filter = filters[filterIndex];
            if (!IsTrustedRoomMesh(filter, roomAnchor) || !seen.Add(filter.GetInstanceID())) continue;
            Mesh mesh = filter.sharedMesh;
            if (mesh == null || mesh.vertexCount == 0) continue;
            int[] triangles;
            try { triangles = mesh.triangles; }
            catch (Exception exception) { Debug.LogWarning("[SionnaRoom] Skipping unreadable mesh " + filter.name + ": " + exception.Message); continue; }
            if (triangles == null || triangles.Length < 3) continue;
            obj.Append("o ").Append(SanitizeName(filter.gameObject.name)).AppendLine();
            Vector3[] vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 roomLocal = roomAnchor.InverseTransformPoint(filter.transform.TransformPoint(vertices[i]));
                obj.Append("v ").Append(F(roomLocal.x)).Append(' ').Append(F(roomLocal.y)).Append(' ').Append(F(roomLocal.z)).AppendLine();
            }
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                obj.Append("f ").Append(triangles[i] + vertexOffset + 1).Append(' ')
                    .Append(triangles[i + 1] + vertexOffset + 1).Append(' ')
                    .Append(triangles[i + 2] + vertexOffset + 1).AppendLine();
            }
            vertexOffset += vertices.Length;
            vertexCount += vertices.Length;
            meshCount++;
        }
        if (meshCount == 0 || vertexCount == 0) { error = "MRUK geometry is not ready."; return false; }
        bytes = new UTF8Encoding(false).GetBytes(obj.ToString());
        return bytes.Length > 0;
    }

    private static bool IsTrustedRoomMesh(MeshFilter filter, Transform roomAnchor)
    {
        if (filter == null || !filter.gameObject.activeInHierarchy || filter.sharedMesh == null) return false;
        if (!filter.transform.IsChildOf(roomAnchor) && filter.transform != roomAnchor) return false;
        if (filter.GetComponentInParent<MRUKAnchor>() != null) return true;
        string name = filter.gameObject.name.ToUpperInvariant();
        return name.Contains("EFFECTMESH") || name.Contains("GLOBALMESH") || name.Contains("WALL") ||
            name.Contains("FLOOR") || name.Contains("CEILING");
    }

    private static string SanitizeName(string value) => string.IsNullOrEmpty(value) ? "MRUKMesh" : value.Replace(' ', '_');
    private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
}
