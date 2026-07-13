using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public struct RtWedgeEdge
{
    public Vector3 pointAWorld;
    public Vector3 pointBWorld;
    public Vector3 face0NormalWorld;
    public Vector3 face1NormalWorld;
}

public class WedgeEdgeExtractor : MonoBehaviour
{
    [SerializeField, Range(1f, 90f)] private float minimumDihedralDegrees = 25f;
    [SerializeField] private bool includeBoundaryEdges = true;
    [SerializeField] private float minimumEdgeLength = 0.08f;
    [SerializeField] private int maximumWedges = 256;
    [SerializeField] private bool drawDebugWedges;

    private readonly List<RtWedgeEdge> wedges = new List<RtWedgeEdge>();
    public IReadOnlyList<RtWedgeEdge> Wedges => wedges;

    private readonly struct EdgeKey : IEquatable<EdgeKey>
    {
        public readonly int a;
        public readonly int b;

        public EdgeKey(int index0, int index1)
        {
            a = Mathf.Min(index0, index1);
            b = Mathf.Max(index0, index1);
        }

        public bool Equals(EdgeKey other) => a == other.a && b == other.b;
        public override bool Equals(object obj) => obj is EdgeKey other && Equals(other);
        public override int GetHashCode() => (a * 397) ^ b;
    }

    private sealed class EdgeFaces
    {
        public int indexA;
        public int indexB;
        public readonly List<Vector3> normalsWorld = new List<Vector3>(2);
    }

    public void Extract(IReadOnlyList<MeshFilter> meshFilters)
    {
        wedges.Clear();
        if (meshFilters == null)
        {
            return;
        }

        List<RtWedgeEdge> candidates = new List<RtWedgeEdge>();
        for (int meshIndex = 0; meshIndex < meshFilters.Count; meshIndex++)
        {
            ExtractMesh(meshFilters[meshIndex], candidates);
        }
        candidates.Sort((left, right) =>
            Vector3.SqrMagnitude(right.pointBWorld - right.pointAWorld).CompareTo(
                Vector3.SqrMagnitude(left.pointBWorld - left.pointAWorld)));
        for (int i = 0; i < candidates.Count && wedges.Count < maximumWedges; i++)
        {
            RtWedgeEdge candidate = candidates[i];
            if (!IsDuplicate(candidate.pointAWorld, candidate.pointBWorld))
            {
                wedges.Add(candidate);
            }
        }
        Debug.Log("[WedgeEdgeExtractor] Extracted " + wedges.Count +
            " bounded sharp/boundary wedge candidates.");
    }

    private void ExtractMesh(MeshFilter meshFilter, List<RtWedgeEdge> candidates)
    {
        if (meshFilter == null || meshFilter.sharedMesh == null)
        {
            return;
        }

        try
        {
            Mesh mesh = meshFilter.sharedMesh;
            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
            Dictionary<EdgeKey, EdgeFaces> edges = new Dictionary<EdgeKey, EdgeFaces>();

            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int i0 = triangles[i];
                int i1 = triangles[i + 1];
                int i2 = triangles[i + 2];
                Vector3 a = meshFilter.transform.TransformPoint(vertices[i0]);
                Vector3 b = meshFilter.transform.TransformPoint(vertices[i1]);
                Vector3 c = meshFilter.transform.TransformPoint(vertices[i2]);
                Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
                if (normal.sqrMagnitude < 0.9f)
                {
                    continue;
                }

                AddEdge(edges, i0, i1, normal);
                AddEdge(edges, i1, i2, normal);
                AddEdge(edges, i2, i0, normal);
            }

            foreach (EdgeFaces edge in edges.Values)
            {
                bool boundary = edge.normalsWorld.Count == 1;
                bool sharp = edge.normalsWorld.Count >= 2 &&
                    Vector3.Angle(edge.normalsWorld[0], edge.normalsWorld[1]) >= minimumDihedralDegrees;
                if ((!includeBoundaryEdges || !boundary) && !sharp)
                {
                    continue;
                }

                Vector3 pointA = meshFilter.transform.TransformPoint(vertices[edge.indexA]);
                Vector3 pointB = meshFilter.transform.TransformPoint(vertices[edge.indexB]);
                if (Vector3.Distance(pointA, pointB) < minimumEdgeLength)
                {
                    continue;
                }

                candidates.Add(new RtWedgeEdge
                {
                    pointAWorld = pointA,
                    pointBWorld = pointB,
                    face0NormalWorld = edge.normalsWorld[0],
                    face1NormalWorld = edge.normalsWorld.Count > 1
                        ? edge.normalsWorld[1]
                        : -edge.normalsWorld[0]
                });
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[WedgeEdgeExtractor] Skipped unreadable mesh '" +
                meshFilter.name + "': " + exception.Message);
        }
    }

    private static void AddEdge(
        Dictionary<EdgeKey, EdgeFaces> edges,
        int indexA,
        int indexB,
        Vector3 normalWorld)
    {
        EdgeKey key = new EdgeKey(indexA, indexB);
        if (!edges.TryGetValue(key, out EdgeFaces edge))
        {
            edge = new EdgeFaces { indexA = key.a, indexB = key.b };
            edges.Add(key, edge);
        }
        if (edge.normalsWorld.Count < 2)
        {
            edge.normalsWorld.Add(normalWorld);
        }
    }

    private bool IsDuplicate(Vector3 a, Vector3 b)
    {
        const float toleranceSquared = 0.02f * 0.02f;
        for (int i = 0; i < wedges.Count; i++)
        {
            RtWedgeEdge existing = wedges[i];
            bool sameOrder = (existing.pointAWorld - a).sqrMagnitude <= toleranceSquared &&
                (existing.pointBWorld - b).sqrMagnitude <= toleranceSquared;
            bool reverseOrder = (existing.pointAWorld - b).sqrMagnitude <= toleranceSquared &&
                (existing.pointBWorld - a).sqrMagnitude <= toleranceSquared;
            if (sameOrder || reverseOrder)
            {
                return true;
            }
        }
        return false;
    }

    private void Update()
    {
        if (!drawDebugWedges)
        {
            return;
        }
        for (int i = 0; i < wedges.Count; i++)
        {
            Debug.DrawLine(wedges[i].pointAWorld, wedges[i].pointBWorld, Color.magenta);
        }
    }
}
