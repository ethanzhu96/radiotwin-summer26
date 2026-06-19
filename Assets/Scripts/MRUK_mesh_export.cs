using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;

public class MRUKMeshExporter : MonoBehaviour
{
    public string fileName = "quest_room_mesh.obj";
    public float exportDelaySeconds = 5f;

    private bool exported = false;

    IEnumerator Start()
    {
        Debug.Log("MRUKMeshExporter started. Waiting before export...");
        yield return new WaitForSeconds(exportDelaySeconds);
        ExportAllMeshes();
    }

    public void ExportAllMeshes()
    {
        if (exported) return;
        exported = true;

        MeshFilter[] meshFilters = FindObjectsOfType<MeshFilter>();

        StringBuilder obj = new StringBuilder();
        int vertexOffset = 0;
        int meshCount = 0;

        obj.AppendLine("# Quest Room Mesh Export");
        obj.AppendLine("# Exported from Unity scene");

        foreach (MeshFilter mf in meshFilters)
        {
            Mesh mesh = mf.sharedMesh;

            if (mesh == null) continue;
            if (mesh.vertexCount == 0) continue;

            Transform t = mf.transform;

            obj.AppendLine("o " + mf.gameObject.name);

            foreach (Vector3 v in mesh.vertices)
            {
                Vector3 world = t.TransformPoint(v);
                obj.AppendLine($"v {world.x} {world.y} {world.z}");
            }

            int[] triangles = mesh.triangles;

            for (int i = 0; i < triangles.Length; i += 3)
            {
                int a = triangles[i] + 1 + vertexOffset;
                int b = triangles[i + 1] + 1 + vertexOffset;
                int c = triangles[i + 2] + 1 + vertexOffset;

                obj.AppendLine($"f {a} {b} {c}");
            }

            vertexOffset += mesh.vertexCount;
            meshCount++;
        }

        string path = Path.Combine(Application.persistentDataPath, fileName);
        File.WriteAllText(path, obj.ToString());

        Debug.Log("EXPORTED ROOM MESH TO: " + path);
        Debug.Log("Exported mesh count: " + meshCount);
        Debug.Log("Total vertices: " + vertexOffset);
    }
}