using System.Collections.Generic;
using UnityEngine;

public struct RtPath
{
    public enum Kind
    {
        LOS,
        Spec1,
        Spec2,
        Diff1
    }

    public Kind kind;
    public Vector3[] points;
    public float gainDb;
}

public class SimpleRayTracer : MonoBehaviour
{
    public LayerMask rtSceneMask;
    public float blockedRssiDb = -120f;
    public float referenceRssiDb = -40f;
    public float referenceDistance = 1f;
    public float pathLossExponent = 2f;
    public float endpointHitTolerance = 0.08f;
    public bool debugDraw = true;

    public List<RtPath> Trace(Vector3 tx, Vector3 rx, out float predictedRssiDb)
    {
        List<RtPath> paths = new List<RtPath>();
        Vector3 delta = rx - tx;
        float distance = Mathf.Max(delta.magnitude, 0.001f);
        Vector3 direction = delta / distance;

        if (Physics.Raycast(tx, direction, out RaycastHit hit, distance, rtSceneMask) &&
            hit.distance > endpointHitTolerance &&
            hit.distance < distance - endpointHitTolerance)
        {
            predictedRssiDb = blockedRssiDb;

            if (debugDraw)
            {
                Debug.DrawLine(tx, hit.point, Color.red);
                Debug.DrawLine(hit.point, rx, new Color(1f, 0.35f, 0.35f));
            }

            return paths;
        }

        float safeReferenceDistance = Mathf.Max(referenceDistance, 0.001f);
        predictedRssiDb = referenceRssiDb -
            10f * pathLossExponent * Mathf.Log10(distance / safeReferenceDistance);

        RtPath losPath = new RtPath
        {
            kind = RtPath.Kind.LOS,
            points = new Vector3[] { tx, rx },
            gainDb = predictedRssiDb
        };

        paths.Add(losPath);

        if (debugDraw)
        {
            Debug.DrawLine(tx, rx, Color.green);
        }

        return paths;
    }
}
