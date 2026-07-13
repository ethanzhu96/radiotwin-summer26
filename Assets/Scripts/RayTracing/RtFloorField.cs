using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;

/*
Inspector setup:
- Add this to an active GameObject, usually RT_Debug.
- Assign Tracer to SimpleRayTracer.
- Assign Coordinate Frame Root to RoomAnchor or the live room frame used by M1.
- Assign Tx Transform to a temporary/static Tx object for now.
- Use the context menu "Generate RT Field" to compute tiles on demand.
*/
public class RtFloorField : MonoBehaviour
{
    public class RtCell
    {
        public Vector2Int index;
        public Vector3 rxWorld;
        public float predictedRssiDb;
        public List<RtPath> paths;
        public GameObject tileObject;
    }

    public SimpleRayTracer tracer;

    [Header("Coordinate Frame")]
    public bool useLiveMRUKRoom = true;
    public Transform coordinateFrameRoot;
    public string fallbackRoomAnchorName = "RoomAnchor";

    [Header("Tx / Grid")]
    public Transform txTransform;
    public float cellSize = 0.5f;
    public float ueHeight = 1.7f;
    public float floorY = 0f;
    public Vector2 xRange = new Vector2(-3f, 3f);
    public Vector2 zRange = new Vector2(-3f, 3f);
    public Material tileMaterial;
    public Transform tileParent;

    [Header("Runtime Tx Auto-Find")]
    public bool autoFindRuntimeTx = true;
    public TxLineOfSightVisualizer txLineOfSightVisualizer;
    public string[] txMarkerNamePrefixes =
    {
        "TX_LOS_ANCHOR_",
        "TX_SPATIAL_ANCHOR_",
        "TX_MARKER_"
    };

    [Header("Toggle")]
    public bool enableRightThumbstickToggle = true;
    public OVRInput.Button toggleButton = OVRInput.Button.SecondaryThumbstick;
    public bool startVisible = false;

    private readonly Dictionary<Vector2Int, RtCell> cells = new Dictionary<Vector2Int, RtCell>();
    private bool isVisible;

    public Transform CurrentTxTransform => txTransform;
    public bool HasCells => cells.Count > 0;

    void Start()
    {
        isVisible = startVisible;
        SetTilesVisible(isVisible);
    }

    void Update()
    {
        if (enableRightThumbstickToggle && OVRInput.GetDown(toggleButton))
        {
            ToggleField();
        }
    }

    void OnDisable()
    {
        SetTilesVisible(false);
    }

    [ContextMenu("Generate RT Field")]
    public void GenerateField()
    {
        ResolveCoordinateFrameRoot();
        ResolveRuntimeTx();

        if (tracer == null)
        {
            Debug.LogWarning("RtFloorField: tracer is missing.");
            return;
        }

        if (coordinateFrameRoot == null)
        {
            Debug.LogWarning("RtFloorField: coordinateFrameRoot is missing. Live MRUK room and fallback RoomAnchor were not found.");
            return;
        }

        if (txTransform == null)
        {
            Debug.LogWarning("RtFloorField: txTransform is missing.");
            return;
        }

        ClearOldTiles();

        float safeCellSize = Mathf.Max(cellSize, 0.001f);
        int minGx = Mathf.FloorToInt(xRange.x / safeCellSize);
        int maxGx = Mathf.FloorToInt(xRange.y / safeCellSize);
        int minGz = Mathf.FloorToInt(zRange.x / safeCellSize);
        int maxGz = Mathf.FloorToInt(zRange.y / safeCellSize);

        for (int gx = minGx; gx <= maxGx; gx++)
        {
            for (int gz = minGz; gz <= maxGz; gz++)
            {
                Vector2Int index = new Vector2Int(gx, gz);
                Vector3 floorLocal = GridIndexToFloorLocal(index, safeCellSize);

                if (floorLocal.x < xRange.x || floorLocal.x > xRange.y ||
                    floorLocal.z < zRange.x || floorLocal.z > zRange.y)
                {
                    continue;
                }

                Vector3 rxLocal = new Vector3(floorLocal.x, floorY + ueHeight, floorLocal.z);
                Vector3 rxWorld = coordinateFrameRoot.TransformPoint(rxLocal);
                List<RtPath> paths = tracer.Trace(txTransform.position, rxWorld, out float rssi);

                RtCell cell = new RtCell
                {
                    index = index,
                    rxWorld = rxWorld,
                    predictedRssiDb = rssi,
                    paths = paths,
                    tileObject = CreateTile(index, floorLocal, rssi)
                };

                cells[index] = cell;
            }
        }

        Debug.Log("RtFloorField: generated " + cells.Count + " LOS floor cells using frame " + coordinateFrameRoot.name + ".");
        SetTilesVisible(isVisible);
    }

    [ContextMenu("Toggle RT Field")]
    public void ToggleField()
    {
        isVisible = !isVisible;

        if (isVisible && cells.Count == 0)
        {
            GenerateField();
            isVisible = true;
        }

        SetTilesVisible(isVisible);
        Debug.Log("RtFloorField: " + (isVisible ? "shown" : "hidden"));
    }

    public bool TryGetCell(Vector2Int index, out RtCell cell)
    {
        return cells.TryGetValue(index, out cell);
    }

    public Vector2Int WorldToGridIndex(Vector3 worldPoint)
    {
        Vector3 localPoint = coordinateFrameRoot != null
            ? coordinateFrameRoot.InverseTransformPoint(worldPoint)
            : worldPoint;

        float safeCellSize = Mathf.Max(cellSize, 0.001f);
        int gx = Mathf.FloorToInt(localPoint.x / safeCellSize);
        int gz = Mathf.FloorToInt(localPoint.z / safeCellSize);
        return new Vector2Int(gx, gz);
    }

    private Vector3 GridIndexToFloorLocal(Vector2Int index, float safeCellSize)
    {
        return new Vector3(
            (index.x + 0.5f) * safeCellSize,
            floorY,
            (index.y + 0.5f) * safeCellSize
        );
    }

    private void ResolveCoordinateFrameRoot()
    {
        if (useLiveMRUKRoom && MRUK.Instance != null && MRUK.Instance.GetCurrentRoom() != null)
        {
            coordinateFrameRoot = MRUK.Instance.GetCurrentRoom().transform;
            return;
        }

        if (coordinateFrameRoot != null)
        {
            return;
        }

        GameObject fallback = GameObject.Find(fallbackRoomAnchorName);
        if (fallback != null)
        {
            coordinateFrameRoot = fallback.transform;
        }
    }

    private void ResolveRuntimeTx()
    {
        if (!autoFindRuntimeTx)
        {
            return;
        }

        if (txLineOfSightVisualizer != null && txLineOfSightVisualizer.HasTxMarker())
        {
            txTransform = txLineOfSightVisualizer.CurrentMarkerTransform;
            return;
        }

        if (txTransform != null && IsRuntimeTxMarker(txTransform.name))
        {
            return;
        }

        if (txLineOfSightVisualizer == null)
        {
            txLineOfSightVisualizer = FindFirstObjectByType<TxLineOfSightVisualizer>();
        }

        if (txLineOfSightVisualizer != null && txLineOfSightVisualizer.HasTxMarker())
        {
            txTransform = txLineOfSightVisualizer.CurrentMarkerTransform;
            return;
        }

        Transform marker = FindRuntimeTxMarkerByName();
        if (marker != null)
        {
            txTransform = marker;
        }
    }

    private Transform FindRuntimeTxMarkerByName()
    {
        Transform[] transforms = FindObjectsByType<Transform>(FindObjectsSortMode.None);

        for (int i = 0; i < transforms.Length; i++)
        {
            if (IsRuntimeTxMarker(transforms[i].name))
            {
                return transforms[i];
            }
        }

        return null;
    }

    private bool IsRuntimeTxMarker(string objectName)
    {
        if (string.IsNullOrEmpty(objectName) || txMarkerNamePrefixes == null)
        {
            return false;
        }

        for (int i = 0; i < txMarkerNamePrefixes.Length; i++)
        {
            string prefix = txMarkerNamePrefixes[i];

            if (!string.IsNullOrEmpty(prefix) && objectName.StartsWith(prefix))
            {
                return true;
            }
        }

        return false;
    }

    private GameObject CreateTile(Vector2Int index, Vector3 floorLocal, float rssi)
    {
        Transform parent = tileParent != null ? tileParent : transform;
        Vector3 worldPosition = coordinateFrameRoot.TransformPoint(floorLocal);
        Quaternion worldRotation = coordinateFrameRoot.rotation * Quaternion.Euler(90f, 0f, 0f);

        GameObject tile = GameObject.CreatePrimitive(PrimitiveType.Quad);
        tile.name = "RT_TILE_" + index.x + "_" + index.y + "_" + rssi.ToString("F1");
        tile.transform.SetParent(parent, true);
        tile.transform.SetPositionAndRotation(worldPosition, worldRotation);
        tile.transform.localScale = new Vector3(cellSize, cellSize, 1f);

        Collider collider = tile.GetComponent<Collider>();
        if (collider != null)
        {
            Destroy(collider);
        }

        Renderer renderer = tile.GetComponent<Renderer>();
        if (renderer != null)
        {
            Material material = tileMaterial != null
                ? new Material(tileMaterial)
                : new Material(Shader.Find("Standard"));

            material.color = RssiToColor(rssi);
            renderer.material = material;
        }

        return tile;
    }

    private void ClearOldTiles()
    {
        foreach (KeyValuePair<Vector2Int, RtCell> kvp in cells)
        {
            if (kvp.Value.tileObject != null)
            {
                DestroyObject(kvp.Value.tileObject);
            }
        }

        cells.Clear();

        Transform parent = tileParent != null ? tileParent : transform;
        List<GameObject> oldTiles = new List<GameObject>();

        foreach (Transform child in parent)
        {
            if (child.name.StartsWith("RT_TILE_"))
            {
                oldTiles.Add(child.gameObject);
            }
        }

        for (int i = 0; i < oldTiles.Count; i++)
        {
            DestroyObject(oldTiles[i]);
        }
    }

    private void SetTilesVisible(bool visible)
    {
        foreach (KeyValuePair<Vector2Int, RtCell> kvp in cells)
        {
            if (kvp.Value.tileObject != null)
            {
                kvp.Value.tileObject.SetActive(visible);
            }
        }
    }

    private void DestroyObject(GameObject obj)
    {
        if (Application.isPlaying)
        {
            Destroy(obj);
        }
        else
        {
            DestroyImmediate(obj);
        }
    }

    private Color RssiToColor(float rssi)
    {
        float weak = tracer != null ? tracer.blockedRssiDb : -120f;
        float strong = tracer != null ? tracer.referenceRssiDb : -40f;
        float t = Mathf.InverseLerp(weak, strong, rssi);
        return Color.Lerp(Color.blue, Color.red, t);
    }
}
