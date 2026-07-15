using System;
using System.Collections;
using UnityEngine;

[DefaultExecutionOrder(70)]
public class RadioLensPropagationController : MonoBehaviour
{
    public static RadioLensPropagationController Instance { get; private set; }
    public PropagationBackend CurrentBackend { get; private set; } = PropagationBackend.Simple;
    public string CurrentStatus { get; private set; } = "Simple mode selected.";
    public bool HasSelectedReceiver { get; private set; }
    public bool IsRequestRunning { get; private set; }

    [Header("Dependencies")]
    [SerializeField] private RadioLensRoomContext roomContext;
    [SerializeField] private RadioLensSionnaClient sionnaClient;
    [SerializeField] private RadioLensSionnaPathRenderer sionnaRenderer;
    [SerializeField] private RtFloorField floorField;
    [SerializeField] private APPlacementManager routerPlacement;
    [SerializeField] private ReflectorOptimizationController reflectorOptimization;
    [SerializeField] private RtPathVisualizer simplePathVisualizer;

    [Header("Receiver Marker")]
    [SerializeField, Min(.01f)] private float receiverMarkerDiameter = .08f;
    [SerializeField] private Color receiverMarkerColor = new Color(1f, .45f, .05f, 1f);

    private Vector3 selectedRxWorld;
    private GameObject receiverMarker;
    private string activeRequestId;
    private int requestGeneration;
    private int requestRoomRevision;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindFirstObjectByType<RadioLensPropagationController>() == null)
            new GameObject("RadioLensPropagationSystem").AddComponent<RadioLensPropagationController>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        ResolveDependencies();
    }

    private void Start()
    {
        ResolveDependencies();
        if (roomContext != null) roomContext.ContextChanged += OnRoomContextChanged;
        if (sionnaClient != null) sionnaClient.StatusChanged += SetStatus;
    }

    private void Update()
    {
        if (CurrentBackend != PropagationBackend.Sionna ||
            (RFOptimizationWorkflowManager.Instance != null && RFOptimizationWorkflowManager.Instance.IsMenuOpen)) return;
        if (OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch))
        {
            if (IsRequestRunning || (sionnaRenderer != null && sionnaRenderer.VisiblePathCount > 0)) ClearPropagationVisuals(true);
            else RequestSionnaTrace();
        }
        if (Input.GetKeyDown(KeyCode.Return)) RequestSionnaTrace();
        if (Input.GetKeyDown(KeyCode.C)) ClearPropagationVisuals(true);
    }

    private void ResolveDependencies()
    {
        if (roomContext == null) roomContext = GetComponent<RadioLensRoomContext>();
        if (roomContext == null) roomContext = gameObject.AddComponent<RadioLensRoomContext>();
        if (sionnaClient == null) sionnaClient = GetComponent<RadioLensSionnaClient>();
        if (sionnaClient == null) sionnaClient = gameObject.AddComponent<RadioLensSionnaClient>();
        if (sionnaRenderer == null) sionnaRenderer = GetComponent<RadioLensSionnaPathRenderer>();
        if (sionnaRenderer == null) sionnaRenderer = gameObject.AddComponent<RadioLensSionnaPathRenderer>();
        if (floorField == null) floorField = FindFirstObjectByType<RtFloorField>();
        if (routerPlacement == null) routerPlacement = FindFirstObjectByType<APPlacementManager>();
        if (reflectorOptimization == null) reflectorOptimization = FindFirstObjectByType<ReflectorOptimizationController>();
        if (simplePathVisualizer == null) simplePathVisualizer = FindFirstObjectByType<RtPathVisualizer>();
    }

    public void SelectBackend(PropagationBackend backend)
    {
        if (CurrentBackend != backend)
        {
            InvalidateRequests();
            if (simplePathVisualizer != null) simplePathVisualizer.Clear();
            if (sionnaRenderer != null) sionnaRenderer.ClearPaths();
            HasSelectedReceiver = false;
            if (receiverMarker != null) receiverMarker.SetActive(false);
        }
        CurrentBackend = backend;
        if (backend == PropagationBackend.Simple)
        {
            SetStatus("Simple mode selected.");
            return;
        }
        SetStatus("Checking Sionna server...");
        int generation = requestGeneration;
        StartCoroutine(sionnaClient.CheckHealth((success, error) =>
        {
            if (generation != requestGeneration || CurrentBackend != PropagationBackend.Sionna) return;
            SetStatus(success ? "Sionna connected." : FriendlyNetworkError(error));
        }));
    }

    public bool SelectReceiverFromCurrentRay(ControllerAimRay aimRay)
    {
        if (CurrentBackend != PropagationBackend.Sionna) return false;
        if (aimRay == null || !aimRay.IsTrackingValid || !aimRay.HasSelectableHit)
        { SetStatus("Aim at a valid room surface."); return true; }
        SelectReceiver(aimRay.CurrentHit.point);
        return true;
    }

    public void SelectReceiver(Vector3 worldPosition)
    {
        if (IsRequestRunning) InvalidateRequests();
        selectedRxWorld = worldPosition;
        HasSelectedReceiver = true;
        if (sionnaRenderer != null) sionnaRenderer.ClearPaths();
        ShowReceiverMarker(worldPosition);
        SetStatus("Receiver selected. Right grip to trace.");
    }

    public void RequestSionnaTrace()
    {
        ResolveDependencies();
        if (CurrentBackend != PropagationBackend.Sionna) return;
        if (IsRequestRunning) { SetStatus("Sionna request already running."); return; }
        if (roomContext == null || !roomContext.IsReady)
        { SetStatus("Preparing room mesh..."); roomContext?.RefreshContext(); return; }
        if (!TryGetTx(out Transform tx)) { SetStatus("Place a router before tracing."); return; }
        if (!HasSelectedReceiver) { SetStatus("Select a receiver point before tracing."); return; }

        activeRequestId = Guid.NewGuid().ToString();
        requestRoomRevision = roomContext.Revision;
        int generation = ++requestGeneration;
        IsRequestRunning = true;
        Vector3 txLocal = roomContext.RoomAnchor.InverseTransformPoint(tx.position);
        Vector3 rxLocal = roomContext.RoomAnchor.InverseTransformPoint(selectedRxWorld);
        StartCoroutine(TraceRoutine(generation, activeRequestId, requestRoomRevision, txLocal, rxLocal));
    }

    private IEnumerator TraceRoutine(int generation, string requestId, int roomRevision, Vector3 txLocal, Vector3 rxLocal)
    {
        SetStatus("Checking Sionna server...");
        bool healthOk = false; string failure = null;
        yield return sionnaClient.CheckHealth((ok, error) => { healthOk = ok; failure = error; });
        if (!RequestIsCurrent(generation, requestId, roomRevision)) { FinishStale(generation); yield break; }
        if (!healthOk) { IsRequestRunning = false; SetStatus(FriendlyNetworkError(failure)); yield break; }

        SetStatus("Checking room cache...");
        bool roomOk = false; string roomResult = null;
        yield return sionnaClient.EnsureRoom(roomContext, (ok, result) => { roomOk = ok; roomResult = result; });
        if (!RequestIsCurrent(generation, requestId, roomRevision)) { FinishStale(generation); yield break; }
        if (!roomOk) { IsRequestRunning = false; SetStatus(roomResult); yield break; }
        SetStatus(roomResult == "cached" ? "Room already cached." : "Room uploaded.");

        SetStatus("Tracing with Sionna RT...");
        bool traceOk = false; string traceError = null; SionnaTraceResponseDto response = null;
        yield return sionnaClient.Trace(roomContext, txLocal, rxLocal, requestId,
            (ok, error, value) => { traceOk = ok; traceError = error; response = value; });
        if (!RequestIsCurrent(generation, requestId, roomRevision)) { FinishStale(generation); yield break; }
        IsRequestRunning = false;
        if (!traceOk) { SetStatus("Sionna trace failed. " + traceError); yield break; }
        if (!ResponseMatches(response, requestId))
        { Debug.LogWarning("[Sionna] Stale or mismatched trace response ignored."); SetStatus("Stale Sionna result ignored."); yield break; }
        int rendered = sionnaRenderer.RenderPaths(response.paths, roomContext.RoomAnchor, sionnaClient.TopK, txLocal, rxLocal);
        SetStatus(rendered == 0 ? "No valid propagation path found." : "Sionna paths displayed: " + rendered);
    }

    private bool TryGetTx(out Transform tx)
    {
        tx = null;
        // Match PathPickerInteractor's Simple backend exactly: both propagation
        // models trace from the Tx resolved by RtFloorField (the A-button anchor).
        return floorField != null && floorField.TryGetTxTransform(out tx) && tx != null;
    }

    public void ClearPropagationVisuals(bool clearReceiver)
    {
        InvalidateRequests();
        if (simplePathVisualizer != null) simplePathVisualizer.Clear();
        if (sionnaRenderer != null) sionnaRenderer.ClearPaths();
        if (clearReceiver)
        {
            HasSelectedReceiver = false;
            if (receiverMarker != null) receiverMarker.SetActive(false);
        }
        SetStatus(CurrentBackend == PropagationBackend.Sionna ? "Sionna paths cleared." : "Simple paths cleared.");
    }

    private void ShowReceiverMarker(Vector3 worldPosition)
    {
        if (receiverMarker == null)
        {
            receiverMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            receiverMarker.name = "Sionna_SelectedReceiver";
            receiverMarker.transform.localScale = Vector3.one * receiverMarkerDiameter;
            Collider collider = receiverMarker.GetComponent<Collider>(); if (collider != null) Destroy(collider);
            Renderer renderer = receiverMarker.GetComponent<Renderer>(); if (renderer != null) renderer.material.color = receiverMarkerColor;
            int layer = LayerMask.NameToLayer("Ignore Raycast"); if (layer >= 0) receiverMarker.layer = layer;
        }
        receiverMarker.transform.SetParent(roomContext != null ? roomContext.RoomAnchor : null, true);
        receiverMarker.transform.position = worldPosition;
        receiverMarker.SetActive(true);
    }

    private bool RequestIsCurrent(int generation, string requestId, int roomRevision) =>
        generation == requestGeneration && requestId == activeRequestId &&
        CurrentBackend == PropagationBackend.Sionna && roomContext != null && roomContext.Revision == roomRevision;

    private bool ResponseMatches(SionnaTraceResponseDto response, string requestId) => response != null &&
        response.requestId == requestId && response.roomId == roomContext.RoomId &&
        response.meshVersion == roomContext.MeshVersion && response.localizationId == roomContext.LocalizationId &&
        response.coordinateFrame == SionnaProtocol.CoordinateFrame && roomContext.RoomAnchor != null;

    private void FinishStale(int generation)
    {
        if (generation == requestGeneration) IsRequestRunning = false;
        Debug.LogWarning("[Sionna] Stale request result ignored.");
    }

    private void InvalidateRequests()
    {
        requestGeneration++;
        activeRequestId = null;
        IsRequestRunning = false;
    }

    private void OnRoomContextChanged()
    {
        InvalidateRequests();
        if (sionnaRenderer != null) sionnaRenderer.ClearPaths();
        HasSelectedReceiver = false;
        if (receiverMarker != null) receiverMarker.SetActive(false);
        if (CurrentBackend == PropagationBackend.Sionna && roomContext != null && roomContext.IsReady)
            SetStatus("Room mesh ready.");
    }

    public void SetStatus(string message)
    {
        CurrentStatus = message;
        Debug.Log("[RadioLensPropagation] " + message);
        RFOptimizationWorkflowManager.Instance?.SetPropagationStatus(message);
    }

    public void ReportSimpleRecommendationFallback() => SetStatus("Recommendation generated using Simple mode.");

    private static string FriendlyNetworkError(string error)
    {
        if (!string.IsNullOrEmpty(error) && error.ToLowerInvariant().Contains("timeout")) return "Sionna request timed out.";
        return "Sionna unavailable.";
    }

    private void OnApplicationPause(bool paused) { if (paused) InvalidateRequests(); }
    private void OnApplicationQuit() { InvalidateRequests(); }

    private void OnDestroy()
    {
        if (roomContext != null) roomContext.ContextChanged -= OnRoomContextChanged;
        if (sionnaClient != null) sionnaClient.StatusChanged -= SetStatus;
        if (Instance == this) Instance = null;
    }
}
