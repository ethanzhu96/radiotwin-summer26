using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

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
    [FormerlySerializedAs("rtSceneMask")]
    [SerializeField] private LayerMask propagationGeometryMask;
    [SerializeField] private SceneColliderBaker colliderBaker;

    [Header("Relative Path Gain")]
    [SerializeField] private float blockedRssiDb = -120f;
    [SerializeField] private float referenceRssiDb = -40f;
    [SerializeField] private float referenceDistance = 1f;
    [SerializeField] private float pathLossExponent = 2f;
    [SerializeField] private float minimumDistance = 0.1f;
    [SerializeField] private float endpointEpsilon = 0.02f;
    [SerializeField] private float oneBounceLossDb = 4f;
    [SerializeField] private float twoBounceLossDb = 9f;
    [SerializeField] private float diffractionLossDb = 12f;

    [Header("Bounded Multipath")]
    [SerializeField] private bool enableOneBounceReflections = true;
    [SerializeField] private bool enableTwoBounceReflections = true;
    [SerializeField] private bool enableFirstOrderDiffraction = true;
    [SerializeField] private int maximumSurfaceCandidates = 20;
    [SerializeField] private int maximumSpec1Paths = 4;
    [SerializeField] private int maximumSpec2Paths = 2;
    [SerializeField] private int maximumWedgeCandidates = 64;
    [SerializeField] private int maximumDiffractionPaths = 2;

    [Header("Debug")]
    [SerializeField] private bool debugDraw;
    [SerializeField] private bool logTraceDiagnostics;

    public LayerMask PropagationGeometryMask => propagationGeometryMask;
    public float BlockedRssiDb => blockedRssiDb;
    public float ReferenceRssiDb => referenceRssiDb;

    private void Awake()
    {
        if (propagationGeometryMask.value == 0)
        {
            int rtSceneLayer = LayerMask.NameToLayer("RTScene");
            if (rtSceneLayer >= 0)
            {
                propagationGeometryMask = 1 << rtSceneLayer;
            }
            else
            {
                Debug.LogError("[SimpleRayTracer] Layer 'RTScene' does not exist. Propagation queries are disabled.");
            }
        }
        if (colliderBaker == null)
        {
            colliderBaker = FindFirstObjectByType<SceneColliderBaker>();
        }
    }

    public List<RtPath> Trace(Vector3 txWorld, Vector3 rxWorld, out float predictedRssiDb)
    {
        List<RtPath> paths = new List<RtPath>();
        float directDistance = Vector3.Distance(txWorld, rxWorld);
        if (directDistance < 0.0001f)
        {
            predictedRssiDb = referenceRssiDb;
            return paths;
        }

        bool losClear = IsSegmentClear(txWorld, rxWorld, out RaycastHit directObstruction);
        if (losClear)
        {
            paths.Add(CreatePath(RtPath.Kind.LOS, new[] { txWorld, rxWorld }, 0f));
        }

        if (colliderBaker == null)
        {
            colliderBaker = FindFirstObjectByType<SceneColliderBaker>();
        }
        if (colliderBaker != null && colliderBaker.IsReady)
        {
            List<RtSurfaceTriangle> candidates = GetSurfaceCandidates(txWorld, rxWorld);
            if (enableOneBounceReflections)
            {
                AddOneBouncePaths(txWorld, rxWorld, candidates, paths);
            }
            if (enableTwoBounceReflections)
            {
                AddTwoBouncePaths(txWorld, rxWorld, candidates, paths);
            }
            if (enableFirstOrderDiffraction && !losClear)
            {
                AddDiffractionPaths(txWorld, rxWorld, colliderBaker.Wedges, paths);
            }
        }

        predictedRssiDb = CombinePathPowers(paths);
        if (logTraceDiagnostics)
        {
            Debug.Log("[SimpleRayTracer] Trace txWorld=" + txWorld.ToString("F3") +
                " rxWorld=" + rxWorld.ToString("F3") +
                " directDistance=" + directDistance.ToString("F3") +
                " los=" + losClear +
                " obstruction=" + (!losClear && directObstruction.collider != null
                    ? directObstruction.collider.name + "@" + directObstruction.point.ToString("F3")
                    : "none") +
                " paths=" + paths.Count +
                " combinedRelativeRssi=" + predictedRssiDb.ToString("F2") + " dB.");
        }
        if (debugDraw)
        {
            DrawDebugPaths(paths, txWorld, rxWorld, directObstruction, losClear);
        }
        return paths;
    }

    private List<RtSurfaceTriangle> GetSurfaceCandidates(Vector3 txWorld, Vector3 rxWorld)
    {
        List<RtSurfaceTriangle> candidates = new List<RtSurfaceTriangle>();
        IReadOnlyList<RtSurfaceTriangle> all = colliderBaker.SurfaceTriangles;
        Vector3 midpoint = (txWorld + rxWorld) * 0.5f;
        List<(float score, RtSurfaceTriangle triangle)> scored =
            new List<(float score, RtSurfaceTriangle triangle)>(all.Count);

        for (int i = 0; i < all.Count; i++)
        {
            RtSurfaceTriangle triangle = all[i];
            Vector3 center = (triangle.pointAWorld + triangle.pointBWorld + triangle.pointCWorld) / 3f;
            float score = (center - midpoint).sqrMagnitude - Mathf.Min(triangle.area, 10f) * 0.05f;
            scored.Add((score, triangle));
        }
        scored.Sort((left, right) => left.score.CompareTo(right.score));
        int count = Mathf.Min(maximumSurfaceCandidates, scored.Count);
        for (int i = 0; i < count; i++)
        {
            candidates.Add(scored[i].triangle);
        }
        return candidates;
    }

    private void AddOneBouncePaths(
        Vector3 txWorld,
        Vector3 rxWorld,
        IReadOnlyList<RtSurfaceTriangle> surfaces,
        List<RtPath> paths)
    {
        int added = 0;
        List<Vector3> usedBouncePoints = new List<Vector3>();
        for (int i = 0; i < surfaces.Count && added < maximumSpec1Paths; i++)
        {
            if (!TryGetOneBouncePoint(txWorld, rxWorld, surfaces[i], out Vector3 bounceWorld) ||
                IsNearAny(bounceWorld, usedBouncePoints, 0.05f) ||
                !IsSegmentClear(txWorld, bounceWorld, out _) ||
                !IsSegmentClear(bounceWorld, rxWorld, out _))
            {
                continue;
            }

            usedBouncePoints.Add(bounceWorld);
            paths.Add(CreatePath(
                RtPath.Kind.Spec1,
                new[] { txWorld, bounceWorld, rxWorld },
                oneBounceLossDb));
            added++;
        }
    }

    private void AddTwoBouncePaths(
        Vector3 txWorld,
        Vector3 rxWorld,
        IReadOnlyList<RtSurfaceTriangle> surfaces,
        List<RtPath> paths)
    {
        int added = 0;
        int boundedCount = Mathf.Min(surfaces.Count, maximumSurfaceCandidates);
        for (int first = 0; first < boundedCount && added < maximumSpec2Paths; first++)
        {
            for (int second = 0; second < boundedCount && added < maximumSpec2Paths; second++)
            {
                if (first == second || !TryGetTwoBouncePoints(
                    txWorld, rxWorld, surfaces[first], surfaces[second],
                    out Vector3 bounce1World, out Vector3 bounce2World) ||
                    !IsSegmentClear(txWorld, bounce1World, out _) ||
                    !IsSegmentClear(bounce1World, bounce2World, out _) ||
                    !IsSegmentClear(bounce2World, rxWorld, out _))
                {
                    continue;
                }

                paths.Add(CreatePath(
                    RtPath.Kind.Spec2,
                    new[] { txWorld, bounce1World, bounce2World, rxWorld },
                    twoBounceLossDb));
                added++;
            }
        }
    }

    private void AddDiffractionPaths(
        Vector3 txWorld,
        Vector3 rxWorld,
        IReadOnlyList<RtWedgeEdge> wedges,
        List<RtPath> paths)
    {
        int added = 0;
        int count = Mathf.Min(maximumWedgeCandidates, wedges.Count);
        List<(float length, Vector3 point)> valid = new List<(float length, Vector3 point)>();
        for (int i = 0; i < count; i++)
        {
            Vector3 diffractionWorld = FindMinimumLengthPointOnEdge(txWorld, rxWorld, wedges[i]);
            if (!IsSegmentClear(txWorld, diffractionWorld, out _) ||
                !IsSegmentClear(diffractionWorld, rxWorld, out _))
            {
                continue;
            }
            float length = Vector3.Distance(txWorld, diffractionWorld) +
                Vector3.Distance(diffractionWorld, rxWorld);
            valid.Add((length, diffractionWorld));
        }

        valid.Sort((left, right) => left.length.CompareTo(right.length));
        for (int i = 0; i < valid.Count && added < maximumDiffractionPaths; i++)
        {
            if (i > 0 && Vector3.Distance(valid[i].point, valid[i - 1].point) < 0.05f)
            {
                continue;
            }
            paths.Add(CreatePath(
                RtPath.Kind.Diff1,
                new[] { txWorld, valid[i].point, rxWorld },
                diffractionLossDb));
            added++;
        }
    }

    private bool TryGetOneBouncePoint(
        Vector3 txWorld,
        Vector3 rxWorld,
        RtSurfaceTriangle surface,
        out Vector3 bounceWorld)
    {
        Vector3 normal = surface.normalWorld.normalized;
        float txSide = Vector3.Dot(txWorld - surface.pointAWorld, normal);
        float rxSide = Vector3.Dot(rxWorld - surface.pointAWorld, normal);
        if (txSide * rxSide <= 0.0001f)
        {
            bounceWorld = default;
            return false;
        }

        Vector3 imageTxWorld = MirrorPointAcrossPlane(txWorld, surface.pointAWorld, normal);
        return TryIntersectPlaneSegment(imageTxWorld, rxWorld, surface, out bounceWorld);
    }

    private bool TryGetTwoBouncePoints(
        Vector3 txWorld,
        Vector3 rxWorld,
        RtSurfaceTriangle first,
        RtSurfaceTriangle second,
        out Vector3 bounce1World,
        out Vector3 bounce2World)
    {
        Vector3 firstNormal = first.normalWorld.normalized;
        Vector3 secondNormal = second.normalWorld.normalized;
        Vector3 image1World = MirrorPointAcrossPlane(txWorld, first.pointAWorld, firstNormal);
        Vector3 image2World = MirrorPointAcrossPlane(image1World, second.pointAWorld, secondNormal);

        if (!TryIntersectPlaneSegment(image2World, rxWorld, second, out bounce2World) ||
            !TryIntersectPlaneSegment(image1World, bounce2World, first, out bounce1World) ||
            Vector3.Distance(bounce1World, bounce2World) < 0.03f)
        {
            bounce1World = default;
            bounce2World = default;
            return false;
        }
        return true;
    }

    private static Vector3 MirrorPointAcrossPlane(Vector3 point, Vector3 planePoint, Vector3 normal)
    {
        return point - 2f * Vector3.Dot(point - planePoint, normal) * normal;
    }

    private static bool TryIntersectPlaneSegment(
        Vector3 lineStart,
        Vector3 lineEnd,
        RtSurfaceTriangle surface,
        out Vector3 point)
    {
        Vector3 delta = lineEnd - lineStart;
        float denominator = Vector3.Dot(delta, surface.normalWorld);
        if (Mathf.Abs(denominator) < 0.00001f)
        {
            point = default;
            return false;
        }
        float t = Vector3.Dot(surface.pointAWorld - lineStart, surface.normalWorld) / denominator;
        if (t <= 0.0001f || t >= 0.9999f)
        {
            point = default;
            return false;
        }
        point = lineStart + delta * t;
        return PointInsideTriangle(point, surface);
    }

    private static bool PointInsideTriangle(Vector3 point, RtSurfaceTriangle triangle)
    {
        Vector3 v0 = triangle.pointCWorld - triangle.pointAWorld;
        Vector3 v1 = triangle.pointBWorld - triangle.pointAWorld;
        Vector3 v2 = point - triangle.pointAWorld;
        float dot00 = Vector3.Dot(v0, v0);
        float dot01 = Vector3.Dot(v0, v1);
        float dot02 = Vector3.Dot(v0, v2);
        float dot11 = Vector3.Dot(v1, v1);
        float dot12 = Vector3.Dot(v1, v2);
        float denominator = dot00 * dot11 - dot01 * dot01;
        if (Mathf.Abs(denominator) < 0.000001f)
        {
            return false;
        }
        float inverse = 1f / denominator;
        float u = (dot11 * dot02 - dot01 * dot12) * inverse;
        float v = (dot00 * dot12 - dot01 * dot02) * inverse;
        const float tolerance = 0.002f;
        return u >= -tolerance && v >= -tolerance && u + v <= 1f + tolerance;
    }

    private static Vector3 FindMinimumLengthPointOnEdge(
        Vector3 txWorld,
        Vector3 rxWorld,
        RtWedgeEdge wedge)
    {
        float low = 0f;
        float high = 1f;
        for (int iteration = 0; iteration < 18; iteration++)
        {
            float left = Mathf.Lerp(low, high, 1f / 3f);
            float right = Mathf.Lerp(low, high, 2f / 3f);
            Vector3 leftPoint = Vector3.Lerp(wedge.pointAWorld, wedge.pointBWorld, left);
            Vector3 rightPoint = Vector3.Lerp(wedge.pointAWorld, wedge.pointBWorld, right);
            float leftLength = Vector3.Distance(txWorld, leftPoint) + Vector3.Distance(leftPoint, rxWorld);
            float rightLength = Vector3.Distance(txWorld, rightPoint) + Vector3.Distance(rightPoint, rxWorld);
            if (leftLength <= rightLength)
            {
                high = right;
            }
            else
            {
                low = left;
            }
        }
        return Vector3.Lerp(wedge.pointAWorld, wedge.pointBWorld, (low + high) * 0.5f);
    }

    private bool IsSegmentClear(Vector3 startWorld, Vector3 endWorld, out RaycastHit obstruction)
    {
        Vector3 delta = endWorld - startWorld;
        float distance = delta.magnitude;
        if (distance < 0.0001f)
        {
            obstruction = default;
            return true;
        }
        Vector3 direction = delta / distance;
        float epsilon = Mathf.Min(Mathf.Max(endpointEpsilon, 0f), distance * 0.2f);
        Vector3 queryStart = startWorld + direction * epsilon;
        float queryDistance = Mathf.Max(0f, distance - 2f * epsilon);
        if (queryDistance <= 0f || propagationGeometryMask.value == 0)
        {
            obstruction = default;
            return true;
        }
        return !Physics.Raycast(
            queryStart,
            direction,
            out obstruction,
            queryDistance,
            propagationGeometryMask,
            QueryTriggerInteraction.Ignore);
    }

    private RtPath CreatePath(RtPath.Kind kind, Vector3[] points, float interactionLossDb)
    {
        float totalDistance = 0f;
        for (int i = 1; i < points.Length; i++)
        {
            totalDistance += Vector3.Distance(points[i - 1], points[i]);
        }
        float safeDistance = Mathf.Max(totalDistance, minimumDistance);
        float safeReferenceDistance = Mathf.Max(referenceDistance, 0.001f);
        float gainDb = referenceRssiDb -
            10f * pathLossExponent * Mathf.Log10(safeDistance / safeReferenceDistance) -
            Mathf.Max(0f, interactionLossDb);
        return new RtPath { kind = kind, points = points, gainDb = gainDb };
    }

    private float CombinePathPowers(IReadOnlyList<RtPath> paths)
    {
        if (paths.Count == 0)
        {
            return blockedRssiDb;
        }
        double linearPower = 0.0;
        for (int i = 0; i < paths.Count; i++)
        {
            linearPower += System.Math.Pow(10.0, paths[i].gainDb / 10.0);
        }
        return linearPower > 0.0
            ? 10f * Mathf.Log10((float)linearPower)
            : blockedRssiDb;
    }

    private static bool IsNearAny(Vector3 point, IReadOnlyList<Vector3> others, float distance)
    {
        float threshold = distance * distance;
        for (int i = 0; i < others.Count; i++)
        {
            if ((point - others[i]).sqrMagnitude <= threshold)
            {
                return true;
            }
        }
        return false;
    }

    private static void DrawDebugPaths(
        IReadOnlyList<RtPath> paths,
        Vector3 txWorld,
        Vector3 rxWorld,
        RaycastHit directObstruction,
        bool losClear)
    {
        if (!losClear && directObstruction.collider != null)
        {
            Debug.DrawLine(txWorld, directObstruction.point, Color.red, 1f);
            Debug.DrawLine(directObstruction.point, rxWorld, new Color(1f, 0.3f, 0.3f), 1f);
        }
        for (int pathIndex = 0; pathIndex < paths.Count; pathIndex++)
        {
            RtPath path = paths[pathIndex];
            Color color = path.kind == RtPath.Kind.LOS ? Color.green :
                path.kind == RtPath.Kind.Spec1 ? Color.cyan :
                path.kind == RtPath.Kind.Spec2 ? Color.blue : Color.magenta;
            for (int i = 1; i < path.points.Length; i++)
            {
                Debug.DrawLine(path.points[i - 1], path.points[i], color, 1f);
            }
        }
    }
}
