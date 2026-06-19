using System.Collections;
using UnityEngine;
using Meta.XR.MRUtilityKit;

public class MRUKDiagnostic : MonoBehaviour
{
    IEnumerator Start()
    {
        Debug.Log("MRUKDiagnostic started.");

        yield return new WaitForSeconds(10f);

        if (MRUK.Instance == null)
        {
            Debug.LogError("MRUKDiagnostic: MRUK.Instance is NULL.");
            yield break;
        }

        var room = MRUK.Instance.GetCurrentRoom();

        if (room == null)
        {
            Debug.LogError("MRUKDiagnostic: Current room is NULL. MRUK did not load headset scene data.");
            yield break;
        }

        Debug.Log("MRUKDiagnostic: Current room loaded.");

        var anchors = FindObjectsByType<MRUKAnchor>(FindObjectsSortMode.None);
        var meshFilters = FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);

        Debug.Log("MRUKDiagnostic: MRUKAnchor count = " + anchors.Length);
        Debug.Log("MRUKDiagnostic: MeshFilter count = " + meshFilters.Length);

        foreach (var anchor in anchors)
        {
            Debug.Log("MRUK Anchor: " + anchor.name + " position=" + anchor.transform.position + " scale=" + anchor.transform.localScale);
        }

        foreach (var mf in meshFilters)
        {
            if (mf.sharedMesh != null)
            {
                Debug.Log("MeshFilter: " + mf.name + " vertices=" + mf.sharedMesh.vertexCount);
            }
        }
    }
}