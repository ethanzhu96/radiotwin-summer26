using System.Collections.Generic;
using TMPro;
using UnityEngine;

/*
Inspector setup:
- Add this to RT_Debug or another active GameObject.
- Assign Floor Field to the RtFloorField component.
- Assign Ray Origin to the right controller transform.
- Set Pick Mask to the floor/room mesh layer you want to point at.
- Optional: assign Label and Path Visual Parent.
- Press Space in editor, or call Pick() from controller input later.
*/
public class PathPickerInteractor : MonoBehaviour
{
    public RtFloorField floorField;
    public Transform rayOrigin;
    public bool useOVRControllerPose = true;
    public OVRInput.Controller controller = OVRInput.Controller.RTouch;
    public Transform trackingSpace;
    public LayerMask pickMask;
    public float maxPickDistance = 10f;
    public Material losLineMaterial;
    public Transform pathVisualParent;
    public TextMeshPro label;
    public bool useKeyboardFallback = true;
    public bool useQuestTrigger = true;
    public OVRInput.Button pickButton = OVRInput.Button.SecondaryIndexTrigger;
    public Vector3 primaryLocalRayDirection = Vector3.forward;
    public bool tryAlternateControllerAxes = true;
    public Transform pickedHitMarker;
    public bool drawTxToPickedHitPoint = true;
    public bool alsoDrawCachedCellPaths = false;
    public bool allowAnyLayerPickFallback = true;
    public bool logPickDiagnostics = true;

    [Header("Pointer Line")]
    public bool drawPointerLine = true;
    public bool pointerOnlyWhilePicking = true;
    public LineRenderer pointerLine;
    public Color pointerHitColor = Color.green;
    public Color pointerMissColor = Color.red;
    public float pointerLineWidth = 0.01f;

    [Header("Path Visuals")]
    public float pathLineWidth = 0.025f;
    public float markerRadius = 0.06f;
    public float pointerVisibleSecondsAfterPick = 0.35f;

    private readonly List<GameObject> pathVisuals = new List<GameObject>();
    private float pointerVisibleUntil;

    void Update()
    {
        if (useKeyboardFallback && Input.GetKeyDown(KeyCode.Space))
        {
            pointerVisibleUntil = Time.unscaledTime + pointerVisibleSecondsAfterPick;
            Pick();
        }

        if (useQuestTrigger && OVRInput.GetDown(pickButton))
        {
            pointerVisibleUntil = Time.unscaledTime + pointerVisibleSecondsAfterPick;
            Pick();
        }

        UpdatePointerLine();
    }

    void OnDisable()
    {
        ClearPathVisuals();

        if (pointerLine != null)
        {
            pointerLine.enabled = false;
        }

        if (pickedHitMarker != null)
        {
            pickedHitMarker.gameObject.SetActive(false);
        }
    }

    public void Pick()
    {
        if (floorField == null)
        {
            SetLabel("No RT floor field");
            return;
        }

        if (!CanGetPickRay())
        {
            SetLabel("No pick ray origin");
            return;
        }

        if (!TryRaycastFromRayOrigin(out RaycastHit hit))
        {
            LogPickDiagnostics(false, default);
            ClearPathVisuals();
            SetLabel("No pick hit");
            return;
        }

        LogPickDiagnostics(true, hit);

        if (!floorField.HasCells)
        {
            floorField.GenerateField();
        }

        MovePickedHitMarker(hit.point);
        Vector2Int index = floorField.WorldToGridIndex(hit.point);

        if (!floorField.TryGetCell(index, out RtFloorField.RtCell cell))
        {
            ClearPathVisuals();
            SetLabel("No RT cell\n" + index);
            return;
        }

        ClearPathVisuals();

        if (drawTxToPickedHitPoint)
        {
            DrawTxToPickedHitPoint(hit.point, cell.paths.Count > 0);
        }

        if (alsoDrawCachedCellPaths)
        {
            for (int i = 0; i < cell.paths.Count; i++)
            {
                DrawPath(cell.paths[i], i);
            }

            DrawMarker(cell.rxWorld, "RT_RX_" + index.x + "_" + index.y, Color.cyan);
        }

        SetLabel("RSSI " + cell.predictedRssiDb.ToString("F1") + " dB\nPaths " + cell.paths.Count);
    }

    private void DrawTxToPickedHitPoint(Vector3 hitPoint, bool losClear)
    {
        if (floorField == null || floorField.CurrentTxTransform == null)
        {
            SetLabel("No Tx for picked path");
            return;
        }

        GameObject lineObject = new GameObject("RT_PICKED_TX_LINE");
        lineObject.transform.SetParent(GetVisualParent(), false);
        pathVisuals.Add(lineObject);

        LineRenderer lineRenderer = lineObject.AddComponent<LineRenderer>();
        lineRenderer.useWorldSpace = true;
        lineRenderer.positionCount = 2;
        lineRenderer.widthMultiplier = pathLineWidth;
        lineRenderer.material = losLineMaterial != null
            ? losLineMaterial
            : new Material(Shader.Find("Sprites/Default"));

        Color color = losClear ? Color.green : Color.red;
        lineRenderer.startColor = color;
        lineRenderer.endColor = color;
        lineRenderer.SetPosition(0, floorField.CurrentTxTransform.position);
        lineRenderer.SetPosition(1, hitPoint);
    }

    private void DrawPath(RtPath path, int pathIndex)
    {
        if (path.points == null || path.points.Length < 2)
        {
            return;
        }

        GameObject lineObject = new GameObject("RT_PATH_" + path.kind + "_" + pathIndex);
        lineObject.transform.SetParent(GetVisualParent(), false);
        pathVisuals.Add(lineObject);

        LineRenderer lineRenderer = lineObject.AddComponent<LineRenderer>();
        lineRenderer.useWorldSpace = true;
        lineRenderer.positionCount = path.points.Length;
        lineRenderer.widthMultiplier = pathLineWidth;
        lineRenderer.material = losLineMaterial != null
            ? losLineMaterial
            : new Material(Shader.Find("Sprites/Default"));

        Color color = path.kind == RtPath.Kind.LOS ? Color.green : Color.white;
        lineRenderer.startColor = color;
        lineRenderer.endColor = color;

        for (int i = 0; i < path.points.Length; i++)
        {
            lineRenderer.SetPosition(i, path.points[i]);
        }

        for (int i = 1; i < path.points.Length; i++)
        {
            DrawMarker(path.points[i], "RT_PATH_POINT_" + pathIndex + "_" + i, Color.green);
        }
    }

    private void DrawMarker(Vector3 worldPosition, string markerName, Color color)
    {
        GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.name = markerName;
        marker.transform.SetParent(GetVisualParent(), true);
        marker.transform.position = worldPosition;
        marker.transform.localScale = Vector3.one * markerRadius * 2f;
        pathVisuals.Add(marker);

        Collider collider = marker.GetComponent<Collider>();
        if (collider != null)
        {
            Destroy(collider);
        }

        Renderer renderer = marker.GetComponent<Renderer>();
        if (renderer != null)
        {
            Material material = new Material(Shader.Find("Standard"));
            material.color = color;
            renderer.material = material;
        }
    }

    private void MovePickedHitMarker(Vector3 worldPosition)
    {
        if (pickedHitMarker == null)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "RT_PICKED_HIT_MARKER";
            marker.transform.localScale = Vector3.one * markerRadius * 2f;
            pickedHitMarker = marker.transform;

            Collider collider = marker.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }
        }

        pickedHitMarker.gameObject.SetActive(true);
        pickedHitMarker.position = worldPosition;
    }

    private void UpdatePointerLine()
    {
        bool recentlyPicked = Time.unscaledTime < pointerVisibleUntil;
        bool keyboardPicking = useKeyboardFallback && Input.GetKey(KeyCode.Space);

        if (!drawPointerLine || !CanGetPickRay() || (pointerOnlyWhilePicking && !recentlyPicked && !keyboardPicking))
        {
            if (pointerLine != null)
            {
                pointerLine.enabled = false;
            }

            return;
        }

        EnsurePointerLine();

        GetPickRay(primaryLocalRayDirection, out Vector3 start, out Vector3 direction);
        Vector3 end = start + direction * maxPickDistance;
        Color color = pointerMissColor;

        if (TryRaycastFromRayOrigin(out RaycastHit hit, out Vector3 hitDirection))
        {
            end = hit.point;
            color = pointerHitColor;
        }

        pointerLine.enabled = true;
        pointerLine.positionCount = 2;
        pointerLine.SetPosition(0, start);
        pointerLine.SetPosition(1, end);
        pointerLine.startColor = color;
        pointerLine.endColor = color;
    }

    private bool TryRaycastFromRayOrigin(out RaycastHit hit)
    {
        return TryRaycastFromRayOrigin(out hit, out _);
    }

    private bool TryRaycastFromRayOrigin(out RaycastHit hit, out Vector3 hitDirection)
    {
        GetPickRay(primaryLocalRayDirection, out Vector3 origin, out Vector3 direction);

        if (Physics.Raycast(origin, direction, out hit, maxPickDistance, pickMask, QueryTriggerInteraction.Collide))
        {
            hitDirection = direction;
            return true;
        }

        if (allowAnyLayerPickFallback &&
            Physics.Raycast(origin, direction, out hit, maxPickDistance, ~0, QueryTriggerInteraction.Collide))
        {
            hitDirection = direction;
            Debug.LogWarning("PathPickerInteractor: Pick mask missed, but fallback hit '" +
                hit.collider.name + "' on layer '" +
                LayerMask.LayerToName(hit.collider.gameObject.layer) + "'. Check Pick Mask / collider layer setup.");
            return true;
        }

        if (tryAlternateControllerAxes)
        {
            Vector3[] fallbackDirections =
            {
                Vector3.forward,
                -Vector3.forward,
                Vector3.up,
                -Vector3.up,
                Vector3.right,
                -Vector3.right
            };

            for (int i = 0; i < fallbackDirections.Length; i++)
            {
                GetPickRay(fallbackDirections[i], out origin, out direction);

                if (Physics.Raycast(origin, direction, out hit, maxPickDistance, pickMask, QueryTriggerInteraction.Collide))
                {
                    hitDirection = direction;
                    primaryLocalRayDirection = fallbackDirections[i];
                    return true;
                }

                if (allowAnyLayerPickFallback &&
                    Physics.Raycast(origin, direction, out hit, maxPickDistance, ~0, QueryTriggerInteraction.Collide))
                {
                    hitDirection = direction;
                    primaryLocalRayDirection = fallbackDirections[i];
                    Debug.LogWarning("PathPickerInteractor: Pick mask missed on fallback axis, but fallback hit '" +
                        hit.collider.name + "' on layer '" +
                        LayerMask.LayerToName(hit.collider.gameObject.layer) + "'. Check Pick Mask / collider layer setup.");
                    return true;
                }
            }
        }

        hit = default;
        hitDirection = direction;
        return false;
    }

    private void LogPickDiagnostics(bool maskedHit, RaycastHit maskedRaycastHit)
    {
        if (!logPickDiagnostics)
        {
            return;
        }

        GetPickRay(primaryLocalRayDirection, out Vector3 origin, out Vector3 direction);

        bool anyHit = Physics.Raycast(origin, direction, out RaycastHit anyHitInfo, maxPickDistance, ~0, QueryTriggerInteraction.Collide);
        string maskedHitText = maskedHit
            ? maskedRaycastHit.collider.name + " layer=" + LayerMask.LayerToName(maskedRaycastHit.collider.gameObject.layer)
            : "none";
        string anyHitText = anyHit
            ? anyHitInfo.collider.name + " layer=" + LayerMask.LayerToName(anyHitInfo.collider.gameObject.layer)
            : "none";

        Debug.LogWarning(
            "PathPickerInteractor pick diagnostics\n" +
            "origin=" + origin.ToString("F3") +
            " direction=" + direction.ToString("F3") +
            " maxDistance=" + maxPickDistance.ToString("F2") + "\n" +
            "pickMaskBits=" + pickMask.value +
            " maskedHit=" + maskedHitText +
            " anyLayerHit=" + anyHitText +
            " useOVRControllerPose=" + useOVRControllerPose +
            " rayOrigin=" + (rayOrigin != null ? rayOrigin.name : "null") +
            " trackingSpace=" + (trackingSpace != null ? trackingSpace.name : "null")
        );
    }

    private Vector3 GetWorldRayDirection(Vector3 localDirection)
    {
        if (localDirection.sqrMagnitude < 0.0001f)
        {
            localDirection = Vector3.forward;
        }

        return rayOrigin.TransformDirection(localDirection.normalized);
    }

    private bool CanGetPickRay()
    {
        if (useOVRControllerPose && ResolveTrackingSpace() != null)
        {
            return true;
        }

        return rayOrigin != null;
    }

    private bool GetPickRay(Vector3 localDirection, out Vector3 origin, out Vector3 direction)
    {
        if (useOVRControllerPose && TryGetOVRControllerRay(localDirection, out origin, out direction))
        {
            return true;
        }

        origin = rayOrigin.position;
        direction = GetWorldRayDirection(localDirection);
        return true;
    }

    private bool TryGetOVRControllerRay(Vector3 localDirection, out Vector3 origin, out Vector3 direction)
    {
        Transform trackingRoot = ResolveTrackingSpace();

        if (trackingRoot == null)
        {
            origin = Vector3.zero;
            direction = Vector3.forward;
            return false;
        }

        Vector3 localPosition = OVRInput.GetLocalControllerPosition(controller);
        Quaternion localRotation = OVRInput.GetLocalControllerRotation(controller);

        if (localPosition == Vector3.zero && localRotation == Quaternion.identity)
        {
            origin = Vector3.zero;
            direction = Vector3.forward;
            return false;
        }

        origin = trackingRoot.TransformPoint(localPosition);
        if (localDirection.sqrMagnitude < 0.0001f)
        {
            localDirection = Vector3.forward;
        }

        direction = (trackingRoot.rotation * localRotation) * localDirection.normalized;
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

    private void EnsurePointerLine()
    {
        if (pointerLine != null)
        {
            return;
        }

        GameObject pointerObject = new GameObject("RT_PICK_POINTER_LINE");
        pointerObject.transform.SetParent(transform, false);
        pointerLine = pointerObject.AddComponent<LineRenderer>();
        pointerLine.useWorldSpace = true;
        pointerLine.widthMultiplier = pointerLineWidth;
        pointerLine.material = new Material(Shader.Find("Sprites/Default"));
    }

    private void ClearPathVisuals()
    {
        for (int i = 0; i < pathVisuals.Count; i++)
        {
            if (pathVisuals[i] != null)
            {
                Destroy(pathVisuals[i]);
            }
        }

        pathVisuals.Clear();
    }

    private Transform GetVisualParent()
    {
        return pathVisualParent != null ? pathVisualParent : transform;
    }

    private void SetLabel(string text)
    {
        if (label != null)
        {
            label.text = text;
        }
    }
}
