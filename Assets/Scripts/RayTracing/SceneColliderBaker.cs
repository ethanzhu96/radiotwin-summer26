using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;

/*
Inspector setup:
- Add this script to an active GameObject in the scene.
- Optional: assign Scene Roots to EffectMesh, RoomAnchor, or any reconstructed room mesh root.
- If Scene Roots is empty, it tries the live MRUK room first, then a GameObject named RoomAnchor.
- Set RT Layer Name to the layer your RaycastSmokeTest mask uses, usually RTScene.
- Press the context menu "Bake Scene Colliders" or leave Bake On Start enabled.
*/
public class SceneColliderBaker : MonoBehaviour
{
    [Header("Scene Roots")]
    public Transform[] sceneRoots;
    public bool autoUseLiveMRUKRoom = true;
    public string fallbackRoomAnchorName = "RoomAnchor";
    public bool includeInactive = false;

    [Header("Collider Setup")]
    public bool bakeOnStart = true;
    public bool waitForMRUKRoom = true;
    public bool addMissingMeshColliders = true;
    public bool assignLayer = true;
    public string rtLayerName = "RTScene";
    public float meshWaitTimeoutSeconds = 8f;

    [Header("Debug")]
    public int bakedMeshCount;
    public int bakedColliderCount;

    private Coroutine bakeRoutine;

    void Start()
    {
        if (bakeOnStart)
        {
            BakeSceneColliders();
        }
    }

    [ContextMenu("Bake Scene Colliders")]
    public void BakeSceneColliders()
    {
        if (bakeRoutine != null)
        {
            StopCoroutine(bakeRoutine);
        }

        bakeRoutine = StartCoroutine(BakeWhenReady());
    }

    private IEnumerator BakeWhenReady()
    {
        if (waitForMRUKRoom && autoUseLiveMRUKRoom)
        {
            while (MRUK.Instance == null || MRUK.Instance.GetCurrentRoom() == null)
            {
                yield return null;
            }
        }

        if (waitForMRUKRoom)
        {
            float timer = 0f;

            while (CountResolvableMeshFilters() == 0 && timer < meshWaitTimeoutSeconds)
            {
                timer += Time.deltaTime;
                yield return null;
            }
        }

        BakeNow();
        bakeRoutine = null;
    }

    private int CountResolvableMeshFilters()
    {
        List<Transform> roots = ResolveSceneRoots();
        int count = 0;

        for (int i = 0; i < roots.Count; i++)
        {
            if (roots[i] == null)
            {
                continue;
            }

            count += roots[i].GetComponentsInChildren<MeshFilter>(includeInactive).Length;
        }

        return count;
    }

    private void BakeNow()
    {
        bakedMeshCount = 0;
        bakedColliderCount = 0;

        int rtLayer = LayerMask.NameToLayer(rtLayerName);
        if (assignLayer && rtLayer < 0)
        {
            Debug.LogWarning("SceneColliderBaker: layer '" + rtLayerName + "' does not exist. Colliders will keep their current layer.");
        }

        List<Transform> roots = ResolveSceneRoots();

        for (int i = 0; i < roots.Count; i++)
        {
            BakeRoot(roots[i], rtLayer);
        }

        Debug.Log("SceneColliderBaker: baked " + bakedColliderCount + " MeshColliders from " + bakedMeshCount + " MeshFilters.");
    }

    private List<Transform> ResolveSceneRoots()
    {
        List<Transform> roots = new List<Transform>();

        if (sceneRoots != null)
        {
            for (int i = 0; i < sceneRoots.Length; i++)
            {
                if (sceneRoots[i] != null && !roots.Contains(sceneRoots[i]))
                {
                    roots.Add(sceneRoots[i]);
                }
            }
        }

        if (roots.Count == 0 && autoUseLiveMRUKRoom && MRUK.Instance != null && MRUK.Instance.GetCurrentRoom() != null)
        {
            roots.Add(MRUK.Instance.GetCurrentRoom().transform);
        }

        if (roots.Count == 0)
        {
            GameObject fallback = GameObject.Find(fallbackRoomAnchorName);
            if (fallback != null)
            {
                roots.Add(fallback.transform);
            }
        }

        return roots;
    }

    private void BakeRoot(Transform root, int rtLayer)
    {
        if (root == null)
        {
            return;
        }

        MeshFilter[] meshFilters = root.GetComponentsInChildren<MeshFilter>(includeInactive);

        for (int i = 0; i < meshFilters.Length; i++)
        {
            MeshFilter meshFilter = meshFilters[i];

            if (meshFilter.sharedMesh == null)
            {
                continue;
            }

            bakedMeshCount++;

            if (assignLayer && rtLayer >= 0)
            {
                meshFilter.gameObject.layer = rtLayer;
            }

            if (!addMissingMeshColliders)
            {
                continue;
            }

            MeshCollider meshCollider = meshFilter.GetComponent<MeshCollider>();
            if (meshCollider == null)
            {
                meshCollider = meshFilter.gameObject.AddComponent<MeshCollider>();
            }

            meshCollider.sharedMesh = meshFilter.sharedMesh;
            meshCollider.convex = false;
            bakedColliderCount++;
        }
    }
}
