using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class TxLineOfSightVisualizer : MonoBehaviour
{
    [Serializable]
    private class TxSpatialAnchorSaveData
    {
        public string txId;
        public string uuid;
        public float posX;
        public float posY;
        public float posZ;
        public float rotX;
        public float rotY; 
        public float rotZ;
        public float rotW;
    }

    [Header("Placement")]
    public Transform placementSource;
    public Transform trackingSpace;
    public bool useOVRControllerPose = true;
    public OVRInput.Controller placementController = OVRInput.Controller.RTouch;
    public bool autoUseControllerPlacementSource = true;
    public bool allowHeadsetPlacementFallback = false;
    public string[] controllerPlacementSourceNames =
    {
        "RightControllerInHandAnchor",
        "RightHandOnControllerAnchor",
        "RightHandAnchor",
        "RightHandAnchorDetached",
        "LeftControllerInHandAnchor",
        "LeftHandOnControllerAnchor"
    };
    public GameObject txMarkerPrefab;
    public float forwardOffsetMeters = 0.25f;

    [Header("Save Data")]
    public string txId = "router_1";
    public string saveFileName = "tx_spatial_anchor.json";
    public bool loadOnStart = true;

    [Header("Telemetry")]
    public WifiPoseLogger telemetryLogger;
    public bool autoAssignTelemetryAnchor = true;
    public string roomAnchorObjectName = "RoomAnchor";

    [Header("Visual Mapping")]
    public Color antennaColor = new Color(1f, 0.1f, 0.85f, 1f);
    public Color pulseColor = new Color(0.2f, 0.9f, 1f, 0.85f);
    public float markerScale = 0.5f;

    [Header("Keyboard Debug")]
    public KeyCode placeKey = KeyCode.T;
    public KeyCode loadKey = KeyCode.L;
    public KeyCode eraseKey = KeyCode.Backspace;

    [Header("Quest Controls")]
    public OVRInput.Button placeButton = OVRInput.Button.One;
    public bool placeButtonTogglesAnchor = true;
    public OVRInput.Button loadButton = OVRInput.Button.Four;

    private GameObject currentMarker;
    private OVRSpatialAnchor currentAnchor;
    private string savePath;
    private bool isBusy;

    public Transform CurrentMarkerTransform
    {
        get
        {
            return currentMarker != null ? currentMarker.transform : null;
        }
    }

    void Start()
    {
        savePath = Path.Combine(Application.persistentDataPath, saveFileName);
        Debug.Log("TxLineOfSightVisualizer save path: " + savePath);

        ResolvePlacementSource();

        if (telemetryLogger == null)
        {
            telemetryLogger = FindFirstObjectByType<WifiPoseLogger>();
        }

        if (loadOnStart)
        {
            StartCoroutine(LoadSavedTxAnchorRoutine());
        }
    }

    void Update()
    {
        bool questPlacePressed = OVRInput.GetDown(placeButton);
        bool questLoadPressed = OVRInput.GetDown(loadButton);

        if ((Input.GetKeyDown(placeKey) || questPlacePressed) && !isBusy)
        {
            if (placeButtonTogglesAnchor && currentMarker != null)
            {
                StartCoroutine(EraseCurrentAnchorRoutine());
            }
            else
            {
                StartCoroutine(CreateAndSaveTxAnchorRoutine());
            }
        }

        if ((Input.GetKeyDown(loadKey) || questLoadPressed) && !isBusy)
        {
            StartCoroutine(LoadSavedTxAnchorRoutine());
        }

        if (Input.GetKeyDown(eraseKey) && !isBusy)
        {
            StartCoroutine(EraseCurrentAnchorRoutine());
        }
    }

    public void PlaceTxAnchor()
    {
        if (!isBusy)
        {
            if (placeButtonTogglesAnchor && currentMarker != null)
            {
                StartCoroutine(EraseCurrentAnchorRoutine());
            }
            else
            {
                StartCoroutine(CreateAndSaveTxAnchorRoutine());
            }
        }
    }

    public void LoadTxAnchor()
    {
        if (!isBusy)
        {
            StartCoroutine(LoadSavedTxAnchorRoutine());
        }
    }

    public void EraseTxAnchor()
    {
        if (!isBusy)
        {
            StartCoroutine(EraseCurrentAnchorRoutine());
        }
    }

    private IEnumerator CreateAndSaveTxAnchorRoutine()
    {
        isBusy = true;

        if (!TryGetPlacementPose(out Pose placementPose))
        {
            Debug.LogError("Cannot create Tx spatial anchor: no headset/controller placement pose is available.");
            isBusy = false;
            yield break;
        }

        ClearCurrentMarker();

        Vector3 forward = placementPose.rotation * Vector3.forward;
        Vector3 pos = placementPose.position + forward * forwardOffsetMeters;
        Quaternion rot = Quaternion.LookRotation(forward, Vector3.up);

        currentMarker = CreateMarkerObject(pos, rot);
        currentAnchor = EnsureSpatialAnchor(currentMarker);
        AssignTelemetryAnchor();

        Debug.Log("Created Tx marker. Waiting for OVRSpatialAnchor creation...");

        float timeout = 10f;
        float timer = 0f;

        while (!currentAnchor.Created && timer < timeout)
        {
            timer += Time.deltaTime;
            yield return null;
        }

        if (!currentAnchor.Created)
        {
            Debug.LogError("OVRSpatialAnchor was not created before timeout.");
            isBusy = false;
            yield break;
        }

        Debug.Log("OVRSpatialAnchor created. UUID: " + currentAnchor.Uuid);

        var saveTask = currentAnchor.SaveAnchorAsync();

        while (!saveTask.IsCompleted)
        {
            yield return null;
        }

        if (!saveTask.GetResult())
        {
            Debug.LogError("Failed to save Tx OVRSpatialAnchor.");
            isBusy = false;
            yield break;
        }

        SaveAnchorJson(currentAnchor.Uuid, currentMarker.transform.position, currentMarker.transform.rotation);
        Debug.Log("Saved Tx spatial anchor at: " + currentMarker.transform.position);

        isBusy = false;
    }

    private bool TryGetPlacementPose(out Pose pose)
    {
        if (useOVRControllerPose && TryGetOVRControllerPose(out pose))
        {
            return true;
        }

        ResolvePlacementSource();

        if (placementSource != null)
        {
            pose = new Pose(placementSource.position, placementSource.rotation);
            return true;
        }

        pose = default;
        return false;
    }

    private bool TryGetOVRControllerPose(out Pose pose)
    {
        Transform trackingRoot = ResolveTrackingSpace();

        if (trackingRoot == null)
        {
            pose = default;
            return false;
        }

        Vector3 localPosition = OVRInput.GetLocalControllerPosition(placementController);
        Quaternion localRotation = OVRInput.GetLocalControllerRotation(placementController);

        if (localPosition == Vector3.zero && localRotation == Quaternion.identity)
        {
            pose = default;
            return false;
        }

        pose = new Pose(
            trackingRoot.TransformPoint(localPosition),
            trackingRoot.rotation * localRotation
        );

        return true;
    }

    private Transform ResolveTrackingSpace()
    {
        if (trackingSpace != null)
        {
            return trackingSpace;
        }

        OVRCameraRig cameraRig = FindFirstObjectByType<OVRCameraRig>();

        if (cameraRig != null && cameraRig.trackingSpace != null)
        {
            trackingSpace = cameraRig.trackingSpace;
        }

        return trackingSpace;
    }

    private void ResolvePlacementSource()
    {
        Camera cam = Camera.main;
        bool placementSourceIsCamera = cam != null && placementSource == cam.transform;

        if (placementSourceIsCamera && !allowHeadsetPlacementFallback)
        {
            placementSource = null;
        }

        if (autoUseControllerPlacementSource)
        {
            for (int i = 0; i < controllerPlacementSourceNames.Length; i++)
            {
                GameObject sourceObject = GameObject.Find(controllerPlacementSourceNames[i]);

                if (sourceObject != null)
                {
                    if (placementSource == null || placementSourceIsCamera)
                    {
                        placementSource = sourceObject.transform;
                        Debug.Log("TxLineOfSightVisualizer using controller placement source: " + sourceObject.name);
                    }

                    return;
                }
            }
        }

        if (placementSource != null)
        {
            return;
        }

        if (allowHeadsetPlacementFallback && cam != null)
        {
            placementSource = cam.transform;
            Debug.LogWarning("TxLineOfSightVisualizer using Main Camera as fallback placement source.");
        }
        else
        {
            Debug.LogWarning("No placementSource assigned and no controller anchor found. Headset fallback is disabled.");
        }
    }

    private IEnumerator LoadSavedTxAnchorRoutine()
    {
        isBusy = true;

        if (string.IsNullOrEmpty(savePath))
        {
            savePath = Path.Combine(Application.persistentDataPath, saveFileName);
        }

        if (!File.Exists(savePath))
        {
            Debug.LogWarning("No saved Tx spatial anchor JSON found at: " + savePath);
            isBusy = false;
            yield break;
        }

        string json = File.ReadAllText(savePath);
        TxSpatialAnchorSaveData data = JsonUtility.FromJson<TxSpatialAnchorSaveData>(json);

        if (data == null || string.IsNullOrWhiteSpace(data.uuid))
        {
            Debug.LogError("Saved Tx anchor JSON has no UUID.");
            isBusy = false;
            yield break;
        }

        Guid uuid = new Guid(data.uuid);

        Debug.Log("Loading Tx spatial anchor UUID: " + uuid);

        List<Guid> uuids = new List<Guid> { uuid };
        List<OVRSpatialAnchor.UnboundAnchor> unboundAnchors = new List<OVRSpatialAnchor.UnboundAnchor>();

        var loadTask = OVRSpatialAnchor.LoadUnboundAnchorsAsync(uuids, unboundAnchors);

        while (!loadTask.IsCompleted)
        {
            yield return null;
        }

        if (!loadTask.GetResult())
        {
            Debug.LogError("Failed to load unbound Tx spatial anchor.");
            LoadFallbackPose(data);
            isBusy = false;
            yield break;
        }

        if (unboundAnchors.Count == 0)
        {
            Debug.LogError("Loaded zero unbound anchors for UUID: " + uuid);
            LoadFallbackPose(data);
            isBusy = false;
            yield break;
        }

        OVRSpatialAnchor.UnboundAnchor unboundAnchor = unboundAnchors[0];

        if (!unboundAnchor.Localized)
        {
            Debug.Log("Localizing Tx anchor...");

            var localizeTask = unboundAnchor.LocalizeAsync();

            while (!localizeTask.IsCompleted)
            {
                yield return null;
            }

            if (!localizeTask.GetResult())
            {
                Debug.LogError("Failed to localize Tx spatial anchor.");
                LoadFallbackPose(data);
                isBusy = false;
                yield break;
            }
        }

        if (!unboundAnchor.TryGetPose(out Pose pose))
        {
            Debug.LogError("Failed to get pose for Tx spatial anchor.");
            LoadFallbackPose(data);
            isBusy = false;
            yield break;
        }

        ClearCurrentMarker();

        currentMarker = CreateMarkerObject(pose.position, pose.rotation);
        currentAnchor = EnsureSpatialAnchor(currentMarker);
        unboundAnchor.BindTo(currentAnchor);
        AssignTelemetryAnchor();

        Debug.Log("Loaded and bound Tx spatial anchor at: " + pose.position);

        isBusy = false;
    }

    private IEnumerator EraseCurrentAnchorRoutine()
    {
        isBusy = true;

        if (currentAnchor != null && currentAnchor.Created)
        {
            var eraseTask = currentAnchor.EraseAnchorAsync();

            while (!eraseTask.IsCompleted)
            {
                yield return null;
            }

            if (eraseTask.GetResult())
            {
                Debug.Log("Erased Tx spatial anchor from device.");
            }
            else
            {
                Debug.LogWarning("Failed to erase Tx spatial anchor from device.");
            }
        }

        ClearCurrentMarker();

        if (File.Exists(savePath))
        {
            File.Delete(savePath);
            Debug.Log("Deleted Tx spatial anchor JSON: " + savePath);
        }

        if (autoAssignTelemetryAnchor && telemetryLogger != null)
        {
            telemetryLogger.UseRoomAnchor();
        }

        isBusy = false;
    }

    private void LoadFallbackPose(TxSpatialAnchorSaveData data)
    {
        Vector3 pos = new Vector3(data.posX, data.posY, data.posZ);
        Quaternion rot = new Quaternion(data.rotX, data.rotY, data.rotZ, data.rotW);

        if (rot == Quaternion.identity && pos == Vector3.zero)
        {
            Debug.LogWarning("No fallback pose was stored for Tx anchor.");
            return;
        }

        ClearCurrentMarker();
        currentMarker = CreateMarkerObject(pos, rot);
        AssignTelemetryAnchor();

        Debug.LogWarning("Using saved Tx fallback pose. Spatial anchor could not be restored.");
    }

    private GameObject CreateMarkerObject(Vector3 pos, Quaternion rot)
    {
        GameObject marker;

        if (txMarkerPrefab != null)
        {
            marker = Instantiate(txMarkerPrefab, pos, rot);
            marker.SetActive(true);
        }
        else
        {
            marker = CreateDefaultTxVisual(pos, rot);
        }

        marker.name = "TX_LOS_ANCHOR_" + txId;
        return marker;
    }

    private GameObject CreateDefaultTxVisual(Vector3 pos, Quaternion rot)
    {
        GameObject root = new GameObject("TX_LOS_VISUAL");
        root.transform.SetPositionAndRotation(pos, rot);
        root.transform.localScale = Vector3.one * markerScale;

        Material antennaMaterial = CreateMaterial(antennaColor);
        Material pulseMaterial = CreateMaterial(pulseColor);

        GameObject mast = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        mast.name = "Antenna_Mast";
        mast.transform.SetParent(root.transform, false);
        mast.transform.localPosition = Vector3.up * 0.18f;
        mast.transform.localScale = new Vector3(0.025f, 0.18f, 0.025f);
        SetRendererMaterial(mast, antennaMaterial);

        GameObject tip = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        tip.name = "Antenna_Tip";
        tip.transform.SetParent(root.transform, false);
        tip.transform.localPosition = Vector3.up * 0.4f;
        tip.transform.localScale = Vector3.one * 0.09f;
        SetRendererMaterial(tip, antennaMaterial);

        GameObject baseSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        baseSphere.name = "Tx_Origin_Core";
        baseSphere.transform.SetParent(root.transform, false);
        baseSphere.transform.localPosition = Vector3.zero;
        baseSphere.transform.localScale = Vector3.one * 0.14f;
        SetRendererMaterial(baseSphere, antennaMaterial);

        TxPulseRings pulse = root.AddComponent<TxPulseRings>();
        pulse.Configure(pulseMaterial, pulseColor);

        return root;
    }

    private OVRSpatialAnchor EnsureSpatialAnchor(GameObject marker)
    {
        OVRSpatialAnchor anchor = marker.GetComponent<OVRSpatialAnchor>();

        if (anchor == null)
        {
            anchor = marker.AddComponent<OVRSpatialAnchor>();
        }

        return anchor;
    }

    private void SaveAnchorJson(Guid uuid, Vector3 pos, Quaternion rot)
    {
        TxSpatialAnchorSaveData data = new TxSpatialAnchorSaveData
        {
            txId = txId,
            uuid = uuid.ToString(),
            posX = pos.x,
            posY = pos.y,
            posZ = pos.z,
            rotX = rot.x,
            rotY = rot.y,
            rotZ = rot.z,
            rotW = rot.w
        };

        string json = JsonUtility.ToJson(data, true);
        File.WriteAllText(savePath, json);
    }

    private void AssignTelemetryAnchor()
    {
        if (!autoAssignTelemetryAnchor || telemetryLogger == null)
        {
            return;
        }

        GameObject roomAnchor = GameObject.Find(roomAnchorObjectName);

        if (roomAnchor != null)
        {
            telemetryLogger.anchorTransform = roomAnchor.transform;
            Debug.Log("TxLineOfSightVisualizer assigned WifiPoseLogger anchorTransform to RoomAnchor.");
        }
        else
        {
            telemetryLogger.UseRoomAnchor();
        }
    }

    private void ClearCurrentMarker()
    {
        if (currentMarker != null)
        {
            Destroy(currentMarker);
        }

        currentMarker = null;
        currentAnchor = null;
    }

    private Material CreateMaterial(Color color)
    {
        Shader shader = Shader.Find("Standard");

        if (shader == null)
        {
            shader = Shader.Find("Sprites/Default");
        }

        if (shader == null)
        {
            Debug.LogWarning("Could not find a shader for Tx marker materials.");
            return null;
        }

        Material material = new Material(shader);
        material.color = color;

        if (shader.name == "Standard")
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", color * 1.2f);
        }

        return material;
    }

    private void SetRendererMaterial(GameObject obj, Material material)
    {
        if (material == null)
        {
            return;
        }

        Renderer renderer = obj.GetComponent<Renderer>();

        if (renderer != null)
        {
            renderer.material = material;
        }
    }

    public Vector3 GetTxPosition()
    {
        return currentMarker != null ? currentMarker.transform.position : Vector3.zero;
    }

    public Quaternion GetTxRotation()
    {
        return currentMarker != null ? currentMarker.transform.rotation : Quaternion.identity;
    }

    public Vector3 WorldToTxRelative(Vector3 worldPosition)
    {
        if (currentMarker == null)
        {
            return worldPosition;
        }

        return currentMarker.transform.InverseTransformPoint(worldPosition);
    }

    public bool HasTxMarker()
    {
        return currentMarker != null;
    }
}

class TxPulseRings : MonoBehaviour
{
    private const int RingCount = 3;
    private const int SegmentCount = 72;

    private readonly LineRenderer[] rings = new LineRenderer[RingCount];
    private Color pulseColor;

    public void Configure(Material material, Color color)
    {
        pulseColor = color;

        for (int i = 0; i < RingCount; i++)
        {
            GameObject ringObject = new GameObject("Pulse_Ring_" + i);
            ringObject.transform.SetParent(transform, false);

            LineRenderer ring = ringObject.AddComponent<LineRenderer>();
            ring.useWorldSpace = false;
            ring.loop = true;
            ring.positionCount = SegmentCount;
            ring.widthMultiplier = 0.012f;

            if (material != null)
            {
                ring.material = material;
            }

            rings[i] = ring;
        }
    }

    void Update()
    {
        for (int i = 0; i < rings.Length; i++)
        {
            if (rings[i] == null)
            {
                continue;
            }

            float phase = Mathf.Repeat(Time.time * 0.6f + i / (float)RingCount, 1f);
            float radius = Mathf.Lerp(0.22f, 0.95f, phase);
            float alpha = Mathf.Lerp(0.85f, 0f, phase);

            DrawRing(rings[i], radius, alpha);
        }
    }

    private void DrawRing(LineRenderer ring, float radius, float alpha)
    {
        Color color = pulseColor;
        color.a = alpha;
        ring.startColor = color;
        ring.endColor = color;

        for (int i = 0; i < SegmentCount; i++)
        {
            float angle = i * Mathf.PI * 2f / SegmentCount;
            Vector3 point = new Vector3(Mathf.Cos(angle) * radius, 0.03f, Mathf.Sin(angle) * radius);
            ring.SetPosition(i, point);
        }
    }
}
