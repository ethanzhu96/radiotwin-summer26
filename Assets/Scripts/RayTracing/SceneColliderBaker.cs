using System.Collections;
using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;

[System.Serializable]
public struct RtSurfaceTriangle
{
    public Vector3 pointAWorld;
    public Vector3 pointBWorld;
    public Vector3 pointCWorld;
    public Vector3 normalWorld;
    public float area;
}

public class SceneColliderBaker : MonoBehaviour
{
    [Header("Collider Setup")]
    [SerializeField] private bool bakeOnStart = true;
    [SerializeField] private bool includeInactive;
    [SerializeField] private string rtLayerName = "RTScene";
    [SerializeField] private int maximumReflectionTriangles = 128;
    [SerializeField] private float minimumReflectionTriangleArea = 0.02f;
    [SerializeField] private WedgeEdgeExtractor wedgeExtractor;

    [Header("Debug")]
    [SerializeField] private int meshFiltersFound;
    [SerializeField] private int existingCollidersReused;
    [SerializeField] private int meshCollidersAdded;
    [SerializeField] private int invalidMeshesSkipped;

    private readonly List<MeshCollider> preparedColliders = new List<MeshCollider>();
    private readonly List<RtSurfaceTriangle> surfaceTriangles = new List<RtSurfaceTriangle>();
    private Coroutine bakeRoutine;

    public bool IsReady { get; private set; }
    public int PreparedColliderCount => preparedColliders.Count;
    public IReadOnlyList<RtSurfaceTriangle> SurfaceTriangles => surfaceTriangles;
    public IReadOnlyList<RtWedgeEdge> Wedges => wedgeExtractor != null
        ? wedgeExtractor.Wedges
        : System.Array.Empty<RtWedgeEdge>();
    public int EnabledColliderCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < preparedColliders.Count; i++)
            {
                if (preparedColliders[i] != null && preparedColliders[i].enabled &&
                    preparedColliders[i].gameObject.activeInHierarchy)
                {
                    count++;
                }
            }
            return count;
        }
    }

    private void Start()
    {
        if (bakeOnStart)
        {
            BakeSceneColliders();
        }
    }

    [ContextMenu("Bake Scene Colliders")]
    public void BakeSceneColliders()
    {
        if (!isActiveAndEnabled)
        {
            Debug.LogWarning("[SceneColliderBaker] Component is disabled; collider preparation cannot start.");
            return;
        }

        if (bakeRoutine != null)
        {
            StopCoroutine(bakeRoutine);
        }
        IsReady = false;
        bakeRoutine = StartCoroutine(BakeWhenAlignmentReady());
    }

    private IEnumerator BakeWhenAlignmentReady()
    {
        RoomAlignmentManager manager = RoomAlignmentManager.EnsureInstance();
        while (manager.State == RoomAlignmentManager.PlaybackState.WaitingForMRUK)
        {
            yield return null;
        }

        if (manager.State != RoomAlignmentManager.PlaybackState.Ready ||
            manager.MatchedRoom == null || manager.DatasetRoot == null)
        {
            Debug.LogError("[SceneColliderBaker] UUID-matched room alignment was not ready. Collider preparation aborted.");
            bakeRoutine = null;
            yield break;
        }

        yield return null;
        BakeMatchedRoom(manager.MatchedRoom, manager.DatasetRoot);
        bakeRoutine = null;
    }

    private void BakeMatchedRoom(MRUKRoom matchedRoom, Transform datasetRoot)
    {
        ResetCounts();
        int rtLayer = LayerMask.NameToLayer(rtLayerName);
        if (rtLayer < 0)
        {
            Debug.LogError("[SceneColliderBaker] Layer '" + rtLayerName +
                "' does not exist. Add RTScene in Project Settings > Tags and Layers, then rebuild.");
            return;
        }

        Debug.Log("[SceneColliderBaker] Matched room: " + matchedRoom.name +
            " UUID=" + matchedRoom.Anchor.Uuid);

        MeshFilter[] meshFilters = matchedRoom.GetComponentsInChildren<MeshFilter>(includeInactive);
        meshFiltersFound = meshFilters.Length;
        List<MeshFilter> validRoomMeshes = new List<MeshFilter>();
        for (int i = 0; i < meshFilters.Length; i++)
        {
            MeshFilter meshFilter = meshFilters[i];
            if (!IsRuntimeRoomGeometry(meshFilter, matchedRoom, datasetRoot))
            {
                invalidMeshesSkipped++;
                continue;
            }

            Mesh mesh = meshFilter.sharedMesh;
            if (mesh == null || mesh.vertexCount < 3 || mesh.subMeshCount == 0 || mesh.GetIndexCount(0) < 3)
            {
                invalidMeshesSkipped++;
                continue;
            }

            MeshCollider meshCollider = meshFilter.GetComponent<MeshCollider>();
            if (meshCollider == null)
            {
                meshCollider = meshFilter.gameObject.AddComponent<MeshCollider>();
                meshCollidersAdded++;
            }
            else
            {
                existingCollidersReused++;
            }

            meshCollider.sharedMesh = mesh;
            meshCollider.convex = false;
            meshCollider.enabled = true;
            meshFilter.gameObject.layer = rtLayer;
            if (!preparedColliders.Contains(meshCollider))
            {
                preparedColliders.Add(meshCollider);
            }
            validRoomMeshes.Add(meshFilter);
        }

        CacheSurfaceTriangles(validRoomMeshes);
        if (wedgeExtractor == null)
        {
            wedgeExtractor = GetComponent<WedgeEdgeExtractor>();
        }
        if (wedgeExtractor == null)
        {
            wedgeExtractor = gameObject.AddComponent<WedgeEdgeExtractor>();
        }
        wedgeExtractor.Extract(validRoomMeshes);

        IsReady = preparedColliders.Count > 0;
        Debug.Log("[SceneColliderBaker] Mesh filters found: " + meshFiltersFound);
        Debug.Log("[SceneColliderBaker] Existing MeshColliders reused: " + existingCollidersReused);
        Debug.Log("[SceneColliderBaker] MeshColliders added: " + meshCollidersAdded);
        Debug.Log("[SceneColliderBaker] Invalid meshes skipped: " + invalidMeshesSkipped);
        Debug.Log("[SceneColliderBaker] RTScene layer index: " + rtLayer);
        Debug.Log("[SceneColliderBaker] Reflection triangles cached: " + surfaceTriangles.Count);
        Debug.Log("[SceneColliderBaker] Wedge candidates cached: " + Wedges.Count);
        if (IsReady)
        {
            Debug.Log("[SceneColliderBaker] Collider preparation complete. Prepared=" + preparedColliders.Count + ".");
        }
        else
        {
            Debug.LogError("[SceneColliderBaker] No valid runtime MRUK room meshes were found under the UUID-matched room.");
        }
    }

    private static bool IsRuntimeRoomGeometry(MeshFilter meshFilter, MRUKRoom matchedRoom, Transform datasetRoot)
    {
        if (meshFilter == null || !meshFilter.gameObject.scene.IsValid())
        {
            return false;
        }
        if (datasetRoot != null && (meshFilter.transform == datasetRoot || meshFilter.transform.IsChildOf(datasetRoot)))
        {
            return false;
        }

        MRUKAnchor anchor = meshFilter.GetComponentInParent<MRUKAnchor>();
        return anchor != null && anchor.GetComponentInParent<MRUKRoom>() == matchedRoom;
    }

    private void ResetCounts()
    {
        preparedColliders.Clear();
        surfaceTriangles.Clear();
        meshFiltersFound = 0;
        existingCollidersReused = 0;
        meshCollidersAdded = 0;
        invalidMeshesSkipped = 0;
        IsReady = false;
    }

    private void CacheSurfaceTriangles(IReadOnlyList<MeshFilter> meshFilters)
    {
        surfaceTriangles.Clear();
        List<RtSurfaceTriangle> candidates = new List<RtSurfaceTriangle>();
        for (int meshIndex = 0; meshIndex < meshFilters.Count; meshIndex++)
        {
            MeshFilter meshFilter = meshFilters[meshIndex];
            try
            {
                Vector3[] vertices = meshFilter.sharedMesh.vertices;
                int[] triangles = meshFilter.sharedMesh.triangles;
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    Vector3 a = meshFilter.transform.TransformPoint(vertices[triangles[i]]);
                    Vector3 b = meshFilter.transform.TransformPoint(vertices[triangles[i + 1]]);
                    Vector3 c = meshFilter.transform.TransformPoint(vertices[triangles[i + 2]]);
                    Vector3 cross = Vector3.Cross(b - a, c - a);
                    float area = cross.magnitude * 0.5f;
                    if (area < minimumReflectionTriangleArea)
                    {
                        continue;
                    }
                    candidates.Add(new RtSurfaceTriangle
                    {
                        pointAWorld = a,
                        pointBWorld = b,
                        pointCWorld = c,
                        normalWorld = cross.normalized,
                        area = area
                    });
                }
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning("[SceneColliderBaker] Could not cache triangles for '" +
                    meshFilter.name + "': " + exception.Message);
            }
        }

        candidates.Sort((left, right) => right.area.CompareTo(left.area));
        int count = Mathf.Min(maximumReflectionTriangles, candidates.Count);
        for (int i = 0; i < count; i++)
        {
            surfaceTriangles.Add(candidates[i]);
        }
    }

    public string GetColliderSummary(int maxEntries)
    {
        List<string> entries = new List<string>();
        int count = Mathf.Min(maxEntries, preparedColliders.Count);
        for (int i = 0; i < count; i++)
        {
            MeshCollider collider = preparedColliders[i];
            if (collider != null)
            {
                entries.Add(collider.name + "(" + LayerMask.LayerToName(collider.gameObject.layer) +
                    ", enabled=" + collider.enabled + ")");
            }
        }
        return entries.Count > 0 ? string.Join(", ", entries) : "none";
    }
}
