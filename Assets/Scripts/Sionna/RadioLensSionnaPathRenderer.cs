using System.Collections.Generic;
using UnityEngine;

public class RadioLensSionnaPathRenderer : MonoBehaviour
{
    [SerializeField] private Material lineMaterial;
    [SerializeField, Min(.001f)] private float lineWidth = .018f;
    [SerializeField] private Color strongestPathColor = new Color(.15f, 1f, .45f, 1f);
    [SerializeField] private Color otherPathColor = new Color(.1f, .65f, 1f, 1f);

    private readonly List<LineRenderer> pool = new List<LineRenderer>();
    private Material runtimeMaterial;
    public int VisiblePathCount { get; private set; }

    public int RenderPaths(IReadOnlyList<SionnaPathDto> paths, Transform roomAnchor, int maximumPaths,
        Vector3 expectedTxLocal, Vector3 expectedRxLocal)
    {
        ClearPaths();
        if (paths == null || roomAnchor == null) return 0;
        int limit = Mathf.Min(Mathf.Max(0, maximumPaths), paths.Count);
        for (int i = 0; i < limit; i++)
        {
            SionnaPathDto path = paths[i];
            if (path == null || path.vertices == null || path.vertices.Length < 2)
            {
                Debug.LogWarning("[SionnaPaths] Ignored path " + i + " with fewer than two vertices.");
                continue;
            }
            if (Vector3.Distance(path.vertices[0], expectedTxLocal) > .1f ||
                Vector3.Distance(path.vertices[path.vertices.Length - 1], expectedRxLocal) > .1f)
            {
                Debug.LogWarning("[SionnaPaths] Path " + i + " endpoints differ from requested Tx/Rx; rendering server vertices unchanged.");
            }
            LineRenderer line = GetLine(VisiblePathCount, roomAnchor);
            line.positionCount = path.vertices.Length;
            line.SetPositions(path.vertices);
            Color color = i == 0 ? strongestPathColor : otherPathColor;
            line.startColor = color;
            line.endColor = color;
            line.gameObject.SetActive(true);
            Debug.Log("[SionnaPaths] path=" + i + " gain=" + path.pathGain.ToString("E4") +
                " delayNs=" + (path.delaySeconds * 1e9).ToString("F3") + " vertices=" + path.vertices.Length + ".");
            VisiblePathCount++;
        }
        return VisiblePathCount;
    }

    public void ClearPaths()
    {
        for (int i = 0; i < pool.Count; i++)
        {
            if (pool[i] == null) continue;
            pool[i].positionCount = 0;
            pool[i].gameObject.SetActive(false);
        }
        VisiblePathCount = 0;
    }

    private LineRenderer GetLine(int index, Transform roomAnchor)
    {
        while (pool.Count <= index)
        {
            GameObject lineObject = new GameObject("SionnaPath_" + pool.Count);
            int ignoreRaycast = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreRaycast >= 0) lineObject.layer = ignoreRaycast;
            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.widthMultiplier = lineWidth;
            line.sharedMaterial = GetMaterial();
            line.numCapVertices = 2;
            pool.Add(line);
        }
        LineRenderer result = pool[index];
        result.transform.SetParent(roomAnchor, false);
        result.transform.localPosition = Vector3.zero;
        result.transform.localRotation = Quaternion.identity;
        result.transform.localScale = Vector3.one;
        result.useWorldSpace = false;
        result.widthMultiplier = lineWidth;
        return result;
    }

    private Material GetMaterial()
    {
        if (lineMaterial != null) return lineMaterial;
        if (runtimeMaterial == null)
            runtimeMaterial = new Material(Shader.Find("Sprites/Default")) { name = "SionnaPath_RuntimeMaterial" };
        return runtimeMaterial;
    }

    private void OnDestroy()
    {
        if (runtimeMaterial != null) Destroy(runtimeMaterial);
    }
}
