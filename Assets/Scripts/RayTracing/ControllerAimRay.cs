using System.Collections.Generic;
using System.Text;
using UnityEngine;

[DefaultExecutionOrder(100)]
public class ControllerAimRay : MonoBehaviour
{
    public enum AimAxis
    {
        ForwardPositiveZ,
        BackwardNegativeZ,
        RightPositiveX,
        LeftNegativeX,
        UpPositiveY,
        DownNegativeY
    }

    [Header("Controller Pose")]
    [SerializeField] private Transform controllerAimTransform;
    [SerializeField] private OVRInput.Controller controller = OVRInput.Controller.RTouch;
    [SerializeField] private AimAxis aimAxis = AimAxis.ForwardPositiveZ;
    [SerializeField] private Vector3 localOriginOffset = Vector3.zero;
    [SerializeField] private bool assumeTrackingValidInEditor = true;

    [Header("Raycast")]
    [SerializeField] private LayerMask selectableTargetMask;
    [SerializeField] private LayerMask nonSelectableTargetMask;
    [SerializeField] private float maxDistance = 10f;

    [Header("Visibility")]
    [SerializeField] private bool onlyVisibleInAnalyzeMode;
    [SerializeField] private RtFloorField analyzeModeSource;

    [Header("Pointer Visual")]
    [SerializeField] private LineRenderer lineRenderer;
    [SerializeField] private Material lineMaterial;
    [SerializeField] private float rayWidth = 0.008f;
    [SerializeField] private Color validHitColor = Color.green;
    [SerializeField] private Color missColor = Color.red;
    [SerializeField] private Color invalidTargetColor = Color.yellow;

    [Header("Hit Marker")]
    [SerializeField] private bool showHitMarker = true;
    [SerializeField] private Transform hitMarker;
    [SerializeField] private float hitMarkerScale = 0.035f;

    [Header("Debug")]
    [SerializeField] private bool showDebugAxes;
    [SerializeField] private float debugAxisLength = 0.2f;
    [SerializeField] private SceneColliderBaker colliderBaker;
    [SerializeField] private SimpleRayTracer tracer;

    public bool IsTrackingValid { get; private set; }
    public bool HasHit { get; private set; }
    public bool HasSelectableHit { get; private set; }
    public RaycastHit CurrentHit { get; private set; }
    public Vector3 RayOrigin { get; private set; }
    public Vector3 RayDirection { get; private set; } = Vector3.forward;
    public LayerMask SelectableTargetMask => selectableTargetMask;
    public float MaxDistance => maxDistance;
    public Transform ControllerAimTransform => controllerAimTransform;
    public AimAxis SelectedAimAxis => aimAxis;

    private bool trackingStateKnown;
    private bool lastTrackingState;
    private Material runtimeMaterial;

    private void Awake()
    {
        ResolveDependencies();
        ResolveAimTransform();
        ResolveDefaultMask();
        EnsureLineRenderer();
        EnsureHitMarker();
        SetVisualsEnabled(false);
    }

    private void OnDisable()
    {
        ClearHit();
        SetVisualsEnabled(false);
    }

    private void LateUpdate()
    {
        ResolveDependencies();
        if (controllerAimTransform == null)
        {
            ResolveAimTransform();
        }

        bool analyzeActive = !onlyVisibleInAnalyzeMode ||
            (analyzeModeSource != null && analyzeModeSource.IsAnalyzeModeActive);
        IsTrackingValid = IsControllerTrackingValid();
        ReportTrackingTransition(IsTrackingValid);

        if (!analyzeActive || !IsTrackingValid || controllerAimTransform == null)
        {
            ClearHit();
            SetVisualsEnabled(false);
            return;
        }

        Vector3 localAxis = GetLocalAxis(aimAxis);
        RayOrigin = controllerAimTransform.TransformPoint(localOriginOffset);
        RayDirection = controllerAimTransform.TransformDirection(localAxis).normalized;

        if (showDebugAxes)
        {
            Debug.DrawRay(controllerAimTransform.position, controllerAimTransform.forward * debugAxisLength, Color.blue);
            Debug.DrawRay(controllerAimTransform.position, controllerAimTransform.right * debugAxisLength, Color.red);
            Debug.DrawRay(controllerAimTransform.position, controllerAimTransform.up * debugAxisLength, Color.green);
        }

        EvaluateHit();
        UpdateVisuals();
    }

    private void ResolveDependencies()
    {
        if (analyzeModeSource == null)
        {
            analyzeModeSource = FindFirstObjectByType<RtFloorField>();
        }
        if (colliderBaker == null)
        {
            colliderBaker = FindFirstObjectByType<SceneColliderBaker>();
        }
        if (tracer == null)
        {
            tracer = FindFirstObjectByType<SimpleRayTracer>();
        }
    }

    private void ResolveAimTransform()
    {
        if (controllerAimTransform != null)
        {
            Debug.Log("[ControllerAimRay] Using assigned aim transform: " + GetHierarchyPath(controllerAimTransform));
            return;
        }

        OVRCameraRig[] rigs = FindObjectsByType<OVRCameraRig>(FindObjectsSortMode.None);
        if (rigs.Length == 0)
        {
            Debug.LogError("[ControllerAimRay] No OVRCameraRig was found. Assign Controller Aim Transform explicitly.");
            return;
        }
        if (rigs.Length > 1)
        {
            Debug.LogWarning("[ControllerAimRay] Multiple OVRCameraRig instances found; using the first active rig deterministically.");
        }

        controllerAimTransform = rigs[0].rightControllerAnchor;
        if (controllerAimTransform == null)
        {
            Debug.LogError("[ControllerAimRay] OVRCameraRig has no RightControllerAnchor.");
            return;
        }

        Debug.Log("[ControllerAimRay] Using aim transform: " + GetHierarchyPath(controllerAimTransform));
    }

    private void ResolveDefaultMask()
    {
        if (selectableTargetMask.value != 0)
        {
            return;
        }

        int rtSceneLayer = LayerMask.NameToLayer("RTScene");
        if (rtSceneLayer < 0)
        {
            Debug.LogError("[ControllerAimRay] Layer 'RTScene' does not exist. Add it in Project Settings > Tags and Layers.");
            return;
        }

        selectableTargetMask = 1 << rtSceneLayer;
        Debug.Log("[ControllerAimRay] Selectable mask defaulted to RTScene (layer " + rtSceneLayer + ").");
    }

    private bool IsControllerTrackingValid()
    {
#if UNITY_EDITOR
        if (assumeTrackingValidInEditor && controllerAimTransform != null)
        {
            return true;
        }
#endif
        OVRInput.Controller connected = OVRInput.GetConnectedControllers();
        bool isConnected = (connected & controller) != 0;
        return isConnected &&
            OVRInput.GetControllerPositionTracked(controller) &&
            OVRInput.GetControllerOrientationTracked(controller);
    }

    private void ReportTrackingTransition(bool valid)
    {
        if (trackingStateKnown && valid == lastTrackingState)
        {
            return;
        }

        trackingStateKnown = true;
        lastTrackingState = valid;
        if (valid)
        {
            Debug.Log("[ControllerAimRay] Right-controller tracking is valid.");
        }
        else
        {
            Debug.LogWarning("[ControllerAimRay] Right-controller tracking is invalid; pointer hidden.");
        }
    }

    private void EvaluateHit()
    {
        ClearHit();
        if (selectableTargetMask.value != 0 && Physics.Raycast(
            RayOrigin, RayDirection, out RaycastHit selectableHit, maxDistance,
            selectableTargetMask, QueryTriggerInteraction.Ignore))
        {
            CurrentHit = selectableHit;
            HasHit = true;
            HasSelectableHit = true;
            return;
        }

        if (nonSelectableTargetMask.value != 0 && Physics.Raycast(
            RayOrigin, RayDirection, out RaycastHit invalidHit, maxDistance,
            nonSelectableTargetMask, QueryTriggerInteraction.Ignore))
        {
            CurrentHit = invalidHit;
            HasHit = true;
        }
    }

    private void UpdateVisuals()
    {
        EnsureLineRenderer();
        Vector3 end = HasHit ? CurrentHit.point : RayOrigin + RayDirection * maxDistance;
        Color color = HasSelectableHit ? validHitColor : HasHit ? invalidTargetColor : missColor;

        lineRenderer.enabled = true;
        lineRenderer.positionCount = 2;
        lineRenderer.SetPosition(0, RayOrigin);
        lineRenderer.SetPosition(1, end);
        lineRenderer.startColor = color;
        lineRenderer.endColor = color;

        if (hitMarker != null)
        {
            hitMarker.gameObject.SetActive(showHitMarker && HasHit);
            if (HasHit)
            {
                hitMarker.SetPositionAndRotation(CurrentHit.point, Quaternion.LookRotation(CurrentHit.normal));
                Renderer renderer = hitMarker.GetComponent<Renderer>();
                if (renderer != null && renderer.sharedMaterial != null)
                {
                    renderer.sharedMaterial.color = color;
                }
            }
        }
    }

    private void EnsureLineRenderer()
    {
        if (lineRenderer == null)
        {
            GameObject lineObject = new GameObject("ControllerAimPointer");
            lineObject.transform.SetParent(transform, false);
            SetNonRaycastableLayer(lineObject);
            lineRenderer = lineObject.AddComponent<LineRenderer>();
        }

        lineRenderer.useWorldSpace = true;
        lineRenderer.positionCount = 2;
        lineRenderer.widthMultiplier = rayWidth;
        lineRenderer.sharedMaterial = GetVisualMaterial();
    }

    private void EnsureHitMarker()
    {
        if (!showHitMarker || hitMarker != null)
        {
            return;
        }

        GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.name = "ControllerAimHitMarker";
        marker.transform.SetParent(transform, false);
        marker.transform.localScale = Vector3.one * hitMarkerScale;
        SetNonRaycastableLayer(marker);
        Collider markerCollider = marker.GetComponent<Collider>();
        if (markerCollider != null)
        {
            Destroy(markerCollider);
        }
        Renderer renderer = marker.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.sharedMaterial = GetVisualMaterial();
        }
        hitMarker = marker.transform;
    }

    private Material GetVisualMaterial()
    {
        if (lineMaterial != null)
        {
            return lineMaterial;
        }
        if (runtimeMaterial == null)
        {
            Shader shader = Shader.Find("Sprites/Default");
            runtimeMaterial = new Material(shader) { name = "ControllerAimRay_RuntimeMaterial" };
        }
        return runtimeMaterial;
    }

    private static void SetNonRaycastableLayer(GameObject target)
    {
        int ignoreRaycast = LayerMask.NameToLayer("Ignore Raycast");
        if (ignoreRaycast >= 0)
        {
            target.layer = ignoreRaycast;
        }
    }

    private void ClearHit()
    {
        HasHit = false;
        HasSelectableHit = false;
        CurrentHit = default;
    }

    private void SetVisualsEnabled(bool enabled)
    {
        if (lineRenderer != null)
        {
            lineRenderer.enabled = enabled;
        }
        if (hitMarker != null)
        {
            hitMarker.gameObject.SetActive(enabled && showHitMarker && HasHit);
        }
    }

    [ContextMenu("Print Controller Aim Debug")]
    public void PrintControllerAimDebug()
    {
        Transform parent = controllerAimTransform != null ? controllerAimTransform.parent : null;
        StringBuilder message = new StringBuilder("[ControllerAimRay] DEBUG\n");
        message.AppendLine("path=" + GetHierarchyPath(controllerAimTransform));
        message.AppendLine("position=" + (controllerAimTransform != null ? controllerAimTransform.position.ToString("F4") : "none"));
        message.AppendLine("rotation=" + (controllerAimTransform != null ? controllerAimTransform.rotation.ToString("F5") : "none"));
        message.AppendLine("parent=" + (parent != null ? parent.name : "none"));
        message.AppendLine("parent position=" + (parent != null ? parent.position.ToString("F4") : "none"));
        message.AppendLine("parent rotation=" + (parent != null ? parent.rotation.ToString("F5") : "none"));
        message.AppendLine("parent scale=" + (parent != null ? parent.lossyScale.ToString("F4") : "none"));
        message.AppendLine("forward=" + (controllerAimTransform != null ? controllerAimTransform.forward.ToString("F4") : "none"));
        message.AppendLine("right=" + (controllerAimTransform != null ? controllerAimTransform.right.ToString("F4") : "none"));
        message.AppendLine("up=" + (controllerAimTransform != null ? controllerAimTransform.up.ToString("F4") : "none"));
        message.AppendLine("aimAxis=" + aimAxis + " localAxis=" + GetLocalAxis(aimAxis));
        message.AppendLine("ray direction=" + RayDirection.ToString("F4") + " magnitude=" + RayDirection.magnitude.ToString("F4"));
        message.AppendLine("tracking valid=" + IsTrackingValid + " hasHit=" + HasHit + " selectable=" + HasSelectableHit);
        message.AppendLine("hit collider=" + (HasHit ? CurrentHit.collider.name : "none"));
        message.AppendLine("hit object=" + (HasHit ? CurrentHit.collider.gameObject.name : "none"));
        message.AppendLine("hit layer=" + (HasHit ? LayerMask.LayerToName(CurrentHit.collider.gameObject.layer) : "none"));
        message.AppendLine("hit point=" + (HasHit ? CurrentHit.point.ToString("F4") : "none"));
        message.AppendLine("hit distance=" + (HasHit ? CurrentHit.distance.ToString("F4") : "none"));
        message.AppendLine("selectable mask=" + selectableTargetMask.value + " [" + GetMaskNames(selectableTargetMask) + "]");
        Debug.Log(message.ToString());
    }

    [ContextMenu("Diagnose Raycast Targets")]
    public void DiagnoseRaycastTargets()
    {
        RaycastHit anyHit = default;
        RaycastHit targetHit = default;
        bool unrestrictedHit = RayDirection.sqrMagnitude > 0.9f && Physics.Raycast(
            RayOrigin, RayDirection, out anyHit, maxDistance, ~0, QueryTriggerInteraction.Ignore);
        bool maskedHit = RayDirection.sqrMagnitude > 0.9f && selectableTargetMask.value != 0 && Physics.Raycast(
            RayOrigin, RayDirection, out targetHit, maxDistance,
            selectableTargetMask, QueryTriggerInteraction.Ignore);

        string unrestricted = unrestrictedHit
            ? anyHit.collider.name + " layer=" + LayerMask.LayerToName(anyHit.collider.gameObject.layer)
            : "none";
        string masked = maskedHit
            ? targetHit.collider.name + " layer=" + LayerMask.LayerToName(targetHit.collider.gameObject.layer)
            : "none";
        bool startsInside = unrestrictedHit && anyHit.distance <= 0.001f;
        bool hitControllerSelf = unrestrictedHit && controllerAimTransform != null &&
            (anyHit.collider.transform == controllerAimTransform ||
             anyHit.collider.transform.IsChildOf(controllerAimTransform) ||
             controllerAimTransform.IsChildOf(anyHit.collider.transform));
        LayerMask propagationMask = tracer != null ? tracer.PropagationGeometryMask : 0;

        Debug.LogWarning(
            "[ControllerAimRay] RAYCAST DIAGNOSIS\n" +
            "enabled=" + enabled + " tracking=" + IsTrackingValid +
            " bakerReady=" + (colliderBaker != null && colliderBaker.IsReady) +
            " baked=" + (colliderBaker != null ? colliderBaker.PreparedColliderCount : 0) +
            " enabledBaked=" + (colliderBaker != null ? colliderBaker.EnabledColliderCount : 0) + "\n" +
            "selectableMask=" + selectableTargetMask.value + " [" + GetMaskNames(selectableTargetMask) + "]" +
            " containsRTScene=" + MaskContainsLayer(selectableTargetMask, "RTScene") + "\n" +
            "propagationMask=" + propagationMask.value + " [" + GetMaskNames(propagationMask) + "]" +
            " containsRTScene=" + MaskContainsLayer(propagationMask, "RTScene") + "\n" +
            "maskedHit=" + masked + " unrestrictedHit=" + unrestricted +
            " excludedByMask=" + (unrestrictedHit && !maskedHit) + " hitControllerSelf=" + hitControllerSelf +
            " startsInsideCollider=" + startsInside + "\n" +
            "origin=" + RayOrigin.ToString("F4") + " direction=" + RayDirection.ToString("F4") +
            " magnitude=" + RayDirection.magnitude.ToString("F4") +
            " maxDistance=" + maxDistance + " QueryTriggerInteraction=Ignore\n" +
            "colliders=" + (colliderBaker != null ? colliderBaker.GetColliderSummary(8) : "no baker"));
    }

    private static Vector3 GetLocalAxis(AimAxis axis)
    {
        switch (axis)
        {
            case AimAxis.BackwardNegativeZ: return Vector3.back;
            case AimAxis.RightPositiveX: return Vector3.right;
            case AimAxis.LeftNegativeX: return Vector3.left;
            case AimAxis.UpPositiveY: return Vector3.up;
            case AimAxis.DownNegativeY: return Vector3.down;
            default: return Vector3.forward;
        }
    }

    private static string GetHierarchyPath(Transform target)
    {
        if (target == null)
        {
            return "none";
        }
        List<string> names = new List<string>();
        for (Transform current = target; current != null; current = current.parent)
        {
            names.Add(current.name);
        }
        names.Reverse();
        return string.Join("/", names);
    }

    private static string GetMaskNames(LayerMask mask)
    {
        List<string> names = new List<string>();
        for (int layer = 0; layer < 32; layer++)
        {
            if ((mask.value & (1 << layer)) != 0)
            {
                string name = LayerMask.LayerToName(layer);
                names.Add(string.IsNullOrEmpty(name) ? "Layer" + layer : name);
            }
        }
        return string.Join(", ", names);
    }

    private static bool MaskContainsLayer(LayerMask mask, string layerName)
    {
        int layer = LayerMask.NameToLayer(layerName);
        return layer >= 0 && (mask.value & (1 << layer)) != 0;
    }
}
