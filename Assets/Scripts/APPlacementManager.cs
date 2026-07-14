using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[DefaultExecutionOrder(50)]
public class APPlacementManager : MonoBehaviour
{
    [Header("Placement")]
    [SerializeField] private Transform leftController;
    [SerializeField] private GameObject candidateAntennaPrefab;
    [SerializeField] private float forwardOffset = 0.25f;
    [SerializeField] private Vector3 localPositionOffset;
    [SerializeField] private Vector3 antennaRotationOffset;
    [SerializeField] private int minimumCandidates = 2;
    [SerializeField] private int maximumCandidates = 8;

    [Header("Deletion")]
    [SerializeField] private float proximityRadius = 0.16f;
    [SerializeField] private float deletionHoldDuration = 0.5f;
    [SerializeField] private bool enableHaptics = true;
    [SerializeField, Range(0f, 1f)] private float hapticAmplitude = 0.25f;
    [SerializeField] private float hapticDuration = 0.06f;

    private readonly List<APCandidateData> candidates = new List<APCandidateData>();
    private readonly List<CandidateAntennaMarker> markers = new List<CandidateAntennaMarker>();
    private Transform candidateContainer;
    private int nextCandidateNumber;
    private WifiPoseLogger logger;
    private APOptimizationManager optimizer;
    private GameObject proximityProxy;

    public APPlacementState State { get; internal set; } = APPlacementState.EditingCandidates;
    public float ProximityRadius => proximityRadius;
    public float DeletionHoldDuration => deletionHoldDuration;
    public int MinimumCandidates => minimumCandidates;
    public IReadOnlyList<APCandidateData> Candidates => candidates;
    public IReadOnlyList<CandidateAntennaMarker> Markers => markers;
    public Transform MatchedRoomTransform => RoomAlignmentManager.Instance != null ? RoomAlignmentManager.Instance.DatasetRoot : null;
    public bool IsLogging => logger != null && logger.isLogging;
    public Collider ProximityCollider => proximityProxy != null ? proximityProxy.GetComponent<Collider>() : null;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindFirstObjectByType<APPlacementManager>() == null)
            new GameObject("APPlacementSystem").AddComponent<APPlacementManager>();
    }

    private void Awake()
    {
        logger = FindFirstObjectByType<WifiPoseLogger>();
        optimizer = GetComponent<APOptimizationManager>();
        if (optimizer == null) optimizer = gameObject.AddComponent<APOptimizationManager>();
        optimizer.Initialize(this);
    }

    private void Update()
    {
        ResolveControllerAndHierarchy();

        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch))
        {
            Debug.Log("[ControllerInput] X down: place candidate");
            HandlePlaceCandidatePressed();
        }
        if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.LTouch))
        {
            Debug.Log("[ControllerInput] Left thumbstick down: evaluate candidates");
            optimizer.RequestCandidateEvaluation();
        }
    }

    private void FixedUpdate()
    {
        if (proximityProxy != null && leftController != null)
            proximityProxy.transform.SetPositionAndRotation(leftController.position, leftController.rotation);
    }

    private void ResolveControllerAndHierarchy()
    {
        if (leftController == null)
        {
            GameObject controller = GameObject.Find("LeftControllerInHandAnchor");
            if (controller == null) controller = GameObject.Find("LeftHandOnControllerAnchor");
            if (controller != null) leftController = controller.transform;
        }
        if (leftController != null && proximityProxy == null) EnsureProximityProxy();
        Transform room = MatchedRoomTransform;
        if (room != null && candidateContainer == null)
        {
            GameObject container = new GameObject("CandidateContainer");
            candidateContainer = container.transform;
            candidateContainer.SetParent(room, false);
            Debug.Log("[APPlacement] Ready in matched-room frame " + room.name + ".");
        }
    }

    private void EnsureProximityProxy()
    {
        proximityProxy = new GameObject("AP_LeftControllerProximityProxy");
        proximityProxy.transform.SetPositionAndRotation(leftController.position, leftController.rotation);
        SphereCollider collider = proximityProxy.AddComponent<SphereCollider>();
        collider.radius = 0.025f;
        collider.isTrigger = true;
        Rigidbody body = proximityProxy.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
    }

    public void HandlePlaceCandidatePressed()
    {
        if (State == APPlacementState.Evaluating) { Report("Evaluation is running; candidate placement is unavailable."); return; }
        if (IsLogging) { Report("Stop trajectory logging before placing router candidates."); return; }
        if (State == APPlacementState.ShowingResults)
        {
            ClearCandidateRound();
            State = APPlacementState.EditingCandidates;
            Debug.Log("[APPlacement] Previous evaluation cleared. Press X again to place Candidate A.");
            return;
        }
        PlaceCandidateAtControllerPose();
    }

    public void PlaceCandidateAtControllerPose()
    {
        if (leftController == null || candidateContainer == null || MatchedRoomTransform == null)
        { Report("Matched room or left controller is not ready."); return; }
        if (candidates.Count >= Mathf.Max(1, maximumCandidates))
        { Report("Maximum router candidate count reached."); return; }

        Vector3 forward = leftController.forward;
        Vector3 placementPosition = leftController.position + forward * forwardOffset +
            leftController.TransformVector(localPositionOffset);
        Quaternion placementRotation = Quaternion.LookRotation(forward, Vector3.up) *
            Quaternion.Euler(antennaRotationOffset);

        string id = "AP_" + (char)('A' + nextCandidateNumber++);
        GameObject candidate = candidateAntennaPrefab != null
            ? Instantiate(candidateAntennaPrefab, placementPosition, placementRotation)
            : CreateAntennaVisual(id);
        candidate.transform.SetPositionAndRotation(placementPosition, placementRotation);
        candidate.transform.SetParent(candidateContainer, true);
        candidate.name = id;
        SphereCollider proximity = candidate.GetComponent<SphereCollider>();
        if (proximity == null) proximity = candidate.AddComponent<SphereCollider>();
        proximity.radius = proximityRadius;
        proximity.isTrigger = true;

        APCandidateData data = new APCandidateData
        {
            candidateId = id,
            roomLocalPosition = MatchedRoomTransform.InverseTransformPoint(candidate.transform.position),
            roomLocalRotation = Quaternion.Inverse(MatchedRoomTransform.rotation) * candidate.transform.rotation
        };
        CandidateAntennaMarker marker = candidate.GetComponent<CandidateAntennaMarker>();
        if (marker == null) marker = candidate.AddComponent<CandidateAntennaMarker>();
        marker.Initialize(data, this, leftController);
        candidates.Add(data);
        markers.Add(marker);
        Debug.Log("[APPlacement] Placed " + id + " at room-local pose " + data.roomLocalPosition.ToString("F3") +
            ". Candidate count=" + candidates.Count + ".");
    }

    public void DeleteCandidate(CandidateAntennaMarker marker)
    {
        if (State != APPlacementState.EditingCandidates || marker == null) return;
        string id = marker.Data.candidateId;
        candidates.Remove(marker.Data);
        markers.Remove(marker);
        Destroy(marker.gameObject);
        Debug.Log("[APPlacement] Deleted " + id + ". Candidate count=" + candidates.Count + ".");
    }

    public void ClearCandidateRound()
    {
        for (int i = 0; i < markers.Count; i++) if (markers[i] != null) Destroy(markers[i].gameObject);
        markers.Clear();
        candidates.Clear();
        nextCandidateNumber = 0;
        optimizer.ClearCandidateResults();
    }

    public void PulseLeftHaptics()
    {
        if (enableHaptics) StartCoroutine(HapticPulse());
    }

    private IEnumerator HapticPulse()
    {
        OVRInput.SetControllerVibration(0.25f, hapticAmplitude, OVRInput.Controller.LTouch);
        yield return new WaitForSecondsRealtime(hapticDuration);
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.LTouch);
    }

    private GameObject CreateAntennaVisual(string objectName)
    {
        GameObject root = new GameObject(objectName);
        CreatePart(root.transform, PrimitiveType.Cylinder, "Antenna", new Vector3(0f, .18f, 0f), new Vector3(.025f, .18f, .025f));
        CreatePart(root.transform, PrimitiveType.Sphere, "Tip", new Vector3(0f, .4f, 0f), Vector3.one * .09f);
        CreatePart(root.transform, PrimitiveType.Sphere, "Base", Vector3.zero, Vector3.one * .14f);
        GameObject textObject = new GameObject("CandidateLabel");
        textObject.transform.SetParent(root.transform, false);
        textObject.transform.localPosition = new Vector3(0f, .55f, 0f);
        TextMesh text = textObject.AddComponent<TextMesh>();
        text.anchor = TextAnchor.MiddleCenter; text.alignment = TextAlignment.Center;
        text.characterSize = .025f; text.fontSize = 48; text.color = Color.white;
        root.AddComponent<CandidateAntennaMarker>();
        return root;
    }

    private static void CreatePart(Transform parent, PrimitiveType type, string name, Vector3 position, Vector3 scale)
    {
        GameObject part = GameObject.CreatePrimitive(type);
        part.name = name; part.transform.SetParent(parent, false);
        part.transform.localPosition = position; part.transform.localScale = scale;
        Collider collider = part.GetComponent<Collider>(); if (collider != null) Destroy(collider);
        Renderer renderer = part.GetComponent<Renderer>();
        if (renderer != null)
        {
            Material material = renderer.material;
            material.color = new Color(1f, .05f, .05f, .5f);
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Mode", 3f);
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.EnableKeyword("_ALPHABLEND_ON");
            material.renderQueue = 3000;
        }
    }

    private static void Report(string message) { Debug.LogWarning("[APPlacement] " + message); }
}
