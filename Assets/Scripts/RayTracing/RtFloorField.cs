using System.Collections.Generic;
using UnityEngine;

public class RtFloorField : MonoBehaviour
{
    public class RtCell
    {
        public Vector2Int index;
        public Vector3 rxLocal;
        public Vector3 rxWorld;
        public float predictedRssiDb;
        public List<RtPath> paths;
        public GameObject tileObject;
    }

    [Header("Dependencies")]
    public SimpleRayTracer tracer;
    [SerializeField] private SceneColliderBaker colliderBaker;

    [Header("Tx / Grid")]
    public Transform txTransform;
    public float cellSize = 0.5f;
    public float ueHeight = 1.7f;
    public float floorY = 0.01f;
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

    [Header("Analyze Mode")]
    public bool enableRightThumbstickToggle = true;
    public OVRInput.Button toggleButton = OVRInput.Button.SecondaryThumbstick;
    public bool startVisible;

    private readonly Dictionary<Vector2Int, RtCell> cells = new Dictionary<Vector2Int, RtCell>();
    private bool isVisible;
    private Transform coordinateFrameRoot;

    public Transform CurrentTxTransform => txTransform;
    public Transform CoordinateFrameRoot => coordinateFrameRoot;
    public bool HasCells => cells.Count > 0;
    public bool IsComputed { get; private set; }
    public bool IsAnalyzeModeActive => isVisible;

    private void Start()
    {
        if (colliderBaker == null)
        {
            colliderBaker = FindFirstObjectByType<SceneColliderBaker>();
        }
        isVisible = startVisible;
        SetTilesVisible(isVisible);
    }

    private void Update()
    {
        if (enableRightThumbstickToggle && OVRInput.GetDown(toggleButton))
        {
            ToggleField();
        }
    }

    private void OnDisable()
    {
        SetTilesVisible(false);
    }

    [ContextMenu("Generate RT Field")]
    public void GenerateField()
    {
        IsComputed = false;
        if (!ResolveCoordinateFrameRoot())
        {
            Debug.LogWarning("[RtFloorField] UUID alignment is not ready; field generation rejected.");
            return;
        }

        if (colliderBaker == null)
        {
            colliderBaker = FindFirstObjectByType<SceneColliderBaker>();
        }
        if (colliderBaker == null || !colliderBaker.IsReady)
        {
            Debug.LogWarning("[RtFloorField] Matched-room colliders are not ready.");
            return;
        }

        ResolveRuntimeTx();
        if (tracer == null)
        {
            Debug.LogWarning("[RtFloorField] SimpleRayTracer is missing.");
            return;
        }
        if (txTransform == null)
        {
            Debug.LogWarning("[RtFloorField] Place or load the Tx before computing the field.");
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
                cells[index] = new RtCell
                {
                    index = index,
                    rxLocal = rxLocal,
                    rxWorld = rxWorld,
                    predictedRssiDb = rssi,
                    paths = paths,
                    tileObject = CreateTile(index, floorLocal, rssi)
                };
            }
        }

        IsComputed = cells.Count > 0;
        SetTilesVisible(isVisible);
        Debug.Log("[RtFloorField] Generated " + cells.Count + " cached RT cells in DatasetRoot frame '" +
            coordinateFrameRoot.name + "' at cellSize=" + safeCellSize + " ueHeight=" + ueHeight + ".");
    }

    [ContextMenu("Toggle RT Analyze Mode")]
    public void ToggleField()
    {
        isVisible = !isVisible;
        if (isVisible && !IsComputed)
        {
            GenerateField();
        }
        SetTilesVisible(isVisible && IsComputed);
        Debug.Log("[RtFloorField] Analyze mode " + (isVisible ? "enabled" : "disabled") +
            "; fieldComputed=" + IsComputed + ".");
    }

    public bool TryGetCell(Vector2Int index, out RtCell cell)
    {
        return cells.TryGetValue(index, out cell);
    }

    public bool TryGetTxTransform(out Transform tx)
    {
        ResolveRuntimeTx();
        tx = txTransform;
        return tx != null;
    }

    public Vector2Int WorldToGridIndex(Vector3 worldPoint)
    {
        Vector3 localPoint = coordinateFrameRoot != null
            ? coordinateFrameRoot.InverseTransformPoint(worldPoint)
            : worldPoint;
        float safeCellSize = Mathf.Max(cellSize, 0.001f);
        return new Vector2Int(
            Mathf.FloorToInt(localPoint.x / safeCellSize),
            Mathf.FloorToInt(localPoint.z / safeCellSize));
    }

    public Vector3 GridIndexToFloorLocal(Vector2Int index)
    {
        return GridIndexToFloorLocal(index, Mathf.Max(cellSize, 0.001f));
    }

    private Vector3 GridIndexToFloorLocal(Vector2Int index, float safeCellSize)
    {
        return new Vector3(
            (index.x + 0.5f) * safeCellSize,
            floorY,
            (index.y + 0.5f) * safeCellSize);
    }

    private bool ResolveCoordinateFrameRoot()
    {
        RoomAlignmentManager manager = RoomAlignmentManager.Instance;
        if (manager == null || manager.State != RoomAlignmentManager.PlaybackState.Ready ||
            manager.DatasetRoot == null)
        {
            coordinateFrameRoot = null;
            return false;
        }

        coordinateFrameRoot = manager.DatasetRoot;
        return true;
    }

    private void ResolveRuntimeTx()
    {
        if (!autoFindRuntimeTx)
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
        if (txTransform != null && IsRuntimeTxMarker(txTransform.name))
        {
            return;
        }

        Transform[] transforms = FindObjectsByType<Transform>(FindObjectsSortMode.None);
        for (int i = 0; i < transforms.Length; i++)
        {
            if (IsRuntimeTxMarker(transforms[i].name))
            {
                txTransform = transforms[i];
                return;
            }
        }
    }

    private bool IsRuntimeTxMarker(string objectName)
    {
        if (string.IsNullOrEmpty(objectName) || txMarkerNamePrefixes == null)
        {
            return false;
        }
        for (int i = 0; i < txMarkerNamePrefixes.Length; i++)
        {
            if (!string.IsNullOrEmpty(txMarkerNamePrefixes[i]) &&
                objectName.StartsWith(txMarkerNamePrefixes[i]))
            {
                return true;
            }
        }
        return false;
    }

    private GameObject CreateTile(Vector2Int index, Vector3 floorLocal, float rssi)
    {
        Transform parent = tileParent != null ? tileParent : transform;
        GameObject tile = GameObject.CreatePrimitive(PrimitiveType.Quad);
        tile.name = "RT_TILE_" + index.x + "_" + index.y + "_" + rssi.ToString("F1");
        tile.transform.SetParent(parent, true);
        tile.transform.SetPositionAndRotation(
            coordinateFrameRoot.TransformPoint(floorLocal),
            coordinateFrameRoot.rotation * Quaternion.Euler(90f, 0f, 0f));
        tile.transform.localScale = new Vector3(cellSize, cellSize, 1f);

        int ignoreRaycast = LayerMask.NameToLayer("Ignore Raycast");
        if (ignoreRaycast >= 0)
        {
            tile.layer = ignoreRaycast;
        }
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
        foreach (KeyValuePair<Vector2Int, RtCell> entry in cells)
        {
            if (entry.Value.tileObject != null)
            {
                Destroy(entry.Value.tileObject);
            }
        }
        cells.Clear();
        IsComputed = false;
    }

    private void SetTilesVisible(bool visible)
    {
        foreach (KeyValuePair<Vector2Int, RtCell> entry in cells)
        {
            if (entry.Value.tileObject != null)
            {
                entry.Value.tileObject.SetActive(visible);
            }
        }
    }

    private Color RssiToColor(float rssi)
    {
        float weak = tracer != null ? tracer.BlockedRssiDb : -120f;
        float strong = tracer != null ? tracer.ReferenceRssiDb : -40f;
        return Color.Lerp(Color.blue, Color.red, Mathf.InverseLerp(weak, strong, rssi));
    }
}
