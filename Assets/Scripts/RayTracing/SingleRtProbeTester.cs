using System.Collections.Generic;
using TMPro;
using UnityEngine;

/*
Inspector setup:
- Add this to RT_Debug or any active GameObject.
- Assign Tracer to a SimpleRayTracer component.
- Assign Tx to your transmitter marker/anchor.
- Assign Rx to a receiver probe object, such as the raycast hit marker.
- Optional: assign Label to a TextMeshPro object for in-headset status.
*/
public class SingleRtProbeTester : MonoBehaviour
{
    public SimpleRayTracer tracer;
    public Transform tx;
    public Transform rx;
    public LineRenderer line;
    public TextMeshPro label;

    [Header("Runtime Tx Auto-Find")]
    public bool autoFindRuntimeTx = true;
    public TxLineOfSightVisualizer txLineOfSightVisualizer;
    public string[] txMarkerNamePrefixes =
    {
        "TX_LOS_ANCHOR_",
        "TX_SPATIAL_ANCHOR_",
        "TX_MARKER_"
    };

    private float nextTxSearchTime;

    void Update()
    {
        ResolveRuntimeTx();

        if (tracer == null || tx == null || rx == null)
        {
            SetLabel("Missing tracer/Tx/Rx");
            return;
        }

        EnsureLineRenderer();

        List<RtPath> paths = tracer.Trace(tx.position, rx.position, out float rssi);
        bool losClear = paths.Count > 0;
        Color color = losClear ? Color.green : Color.red;

        line.enabled = true;
        line.positionCount = 2;
        line.SetPosition(0, tx.position);
        line.SetPosition(1, rx.position);
        line.startColor = color;
        line.endColor = color;

        SetLabel((losClear ? "LOS clear" : "Blocked") + "\nRSSI " + rssi.ToString("F1") + " dB");
    }

    void OnDisable()
    {
        if (line != null)
        {
            line.enabled = false;
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
            tx = txLineOfSightVisualizer.CurrentMarkerTransform;
            return;
        }

        if (tx != null && IsRuntimeTxMarker(tx.name))
        {
            return;
        }

        if (Time.unscaledTime < nextTxSearchTime)
        {
            return;
        }

        nextTxSearchTime = Time.unscaledTime + 0.5f;

        if (txLineOfSightVisualizer == null)
        {
            txLineOfSightVisualizer = FindFirstObjectByType<TxLineOfSightVisualizer>();
        }

        if (txLineOfSightVisualizer != null && txLineOfSightVisualizer.HasTxMarker())
        {
            tx = txLineOfSightVisualizer.CurrentMarkerTransform;
            return;
        }

        Transform marker = FindRuntimeTxMarkerByName();
        if (marker != null)
        {
            tx = marker;
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

    private void EnsureLineRenderer()
    {
        if (line != null)
        {
            return;
        }

        line = gameObject.GetComponent<LineRenderer>();

        if (line == null)
        {
            line = gameObject.AddComponent<LineRenderer>();
        }

        line.useWorldSpace = true;
        line.widthMultiplier = 0.02f;
        line.material = new Material(Shader.Find("Sprites/Default"));
    }

    private void SetLabel(string text)
    {
        if (label != null)
        {
            label.text = text;
        }
    }
}
