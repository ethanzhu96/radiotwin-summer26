using System.Collections.Generic;
using UnityEngine;

public class PathPickerInteractor : MonoBehaviour
{
    [Header("Dependencies")]
    [SerializeField] private ControllerAimRay controllerAimRay;
    [SerializeField] private SceneColliderBaker colliderBaker;
    [SerializeField] private RtFloorField floorField;
    [SerializeField] private SimpleRayTracer tracer;
    [SerializeField] private RtPathVisualizer pathVisualizer;

    [Header("Selection Input")]
    [SerializeField] private bool useQuestTrigger = true;
    [SerializeField] private OVRInput.Button pickButton = OVRInput.Button.SecondaryIndexTrigger;
    [SerializeField] private bool useKeyboardFallback = true;
    [SerializeField] private KeyCode keyboardPickKey = KeyCode.Space;

    [Header("Selection Behavior")]
    [SerializeField, Range(0f, 1f)] private float floorNormalThreshold = 0.65f;

    private void Awake()
    {
        ResolveDependencies();
    }

    private void Update()
    {
        bool selectPressed = (useKeyboardFallback && Input.GetKeyDown(keyboardPickKey)) ||
            (useQuestTrigger && OVRInput.GetDown(
                OVRInput.Button.PrimaryIndexTrigger,
                OVRInput.Controller.RTouch));
        if (selectPressed)
        {
            Pick();
        }
    }

    private void ResolveDependencies()
    {
        if (floorField == null) floorField = FindFirstObjectByType<RtFloorField>();
        if (colliderBaker == null) colliderBaker = FindFirstObjectByType<SceneColliderBaker>();
        if (tracer == null) tracer = FindFirstObjectByType<SimpleRayTracer>();
        if (controllerAimRay == null) controllerAimRay = FindFirstObjectByType<ControllerAimRay>();
        if (controllerAimRay == null)
        {
            controllerAimRay = gameObject.AddComponent<ControllerAimRay>();
            Debug.Log("[PathPickerInteractor] Added the single authoritative ControllerAimRay to " + gameObject.name + ".");
        }
        if (pathVisualizer == null) pathVisualizer = FindFirstObjectByType<RtPathVisualizer>();
        if (pathVisualizer == null) pathVisualizer = gameObject.AddComponent<RtPathVisualizer>();
    }

    [ContextMenu("Pick Current RT Target")]
    public void Pick()
    {
        ResolveDependencies();
        if (pathVisualizer != null)
        {
            pathVisualizer.Clear();
        }

        RoomAlignmentManager alignment = RoomAlignmentManager.Instance;
        if (alignment == null || alignment.State != RoomAlignmentManager.PlaybackState.Ready ||
            alignment.DatasetRoot == null)
        {
            Reject("UUID room alignment is not ready");
            return;
        }
        if (colliderBaker == null || !colliderBaker.IsReady)
        {
            Reject("Matched-room colliders are not ready");
            return;
        }
        if (tracer == null)
        {
            Reject("SimpleRayTracer is missing");
            return;
        }
        if (floorField == null || !floorField.TryGetTxTransform(out Transform tx))
        {
            Reject("Place or load the Tx first");
            return;
        }
        if (controllerAimRay == null || !controllerAimRay.IsTrackingValid)
        {
            Reject("Right-controller tracking is invalid");
            return;
        }
        if (!controllerAimRay.HasSelectableHit)
        {
            Reject("Controller ray has no selectable RTScene hit");
            return;
        }

        RaycastHit hit = controllerAimRay.CurrentHit;
        bool isFloorFacing = Vector3.Dot(hit.normal.normalized, alignment.DatasetRoot.up) >= floorNormalThreshold;
        if (isFloorFacing && floorField.IsComputed)
        {
            Vector2Int index = floorField.WorldToGridIndex(hit.point);
            if (floorField.TryGetCell(index, out RtFloorField.RtCell cell))
            {
                pathVisualizer.ShowCell(cell, hit.point);
                Debug.Log("[PathPickerInteractor] Selected cached floor grid=" + index +
                    " floorHitWorld=" + hit.point.ToString("F3") +
                    " receiverWorld=" + cell.rxWorld.ToString("F3") +
                    " paths=" + cell.paths.Count +
                    " relativeRssi=" + cell.predictedRssiDb.ToString("F2") + " dB.");
                return;
            }
        }

        List<RtPath> surfacePaths = tracer.Trace(tx.position, hit.point, out float surfaceRssi);
        pathVisualizer.ShowPaths(
            surfacePaths,
            hit.point,
            surfaceRssi,
            "Surface " + hit.collider.name);
        Debug.Log("[PathPickerInteractor] Selected direct surface hit object=" + hit.collider.name +
            " pointWorld=" + hit.point.ToString("F3") +
            " paths=" + surfacePaths.Count +
            " relativeRssi=" + surfaceRssi.ToString("F2") + " dB.");
    }

    private void Reject(string reason)
    {
        if (pathVisualizer != null)
        {
            pathVisualizer.ShowMessage(reason);
        }
        Debug.LogWarning("[PathPickerInteractor] Selection rejected: " + reason + ".");
    }
}
