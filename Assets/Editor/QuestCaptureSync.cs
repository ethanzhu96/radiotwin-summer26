using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

public static class QuestCaptureSync
{
    private const string CsvFileName = "rf_trajectory_log.csv";
    private const string MeshFileName = "quest_room_mesh.obj";
    private const string MeshAssetPath = "Assets/ExportedMeshes/quest_room_mesh.obj";
    private const string GeneratedMeshAssetPath = "Assets/ExportedMeshes/quest_room_mesh_generated.asset";
    private const string GeneratedMeshPrefabPath = "Assets/ExportedMeshes/quest_room_mesh_generated.prefab";
    private const int AdbTimeoutMilliseconds = 60000;

    [MenuItem("Tools/RadioTwin/Sync Quest Capture")]
    public static void SyncQuestCapture()
    {
        try
        {
            string adbPath = FindAdbPath();
            string packageName = PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android);

            if (string.IsNullOrWhiteSpace(packageName))
            {
                packageName = "com.DefaultCompany.RadioTwin_Quest_DataCollector";
            }

            EnsureDeviceReady(adbPath);

            string csvDevicePath = "/sdcard/Android/data/" + packageName + "/files/" + CsvFileName;
            string meshDevicePath = "/sdcard/Android/data/" + packageName + "/files/" + MeshFileName;
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string csvProjectPath = Path.Combine(projectRoot, CsvFileName);
            string csvEditorPath = Path.Combine(Application.persistentDataPath, CsvFileName);
            string meshProjectPath = Path.Combine(projectRoot, MeshAssetPath);

            Directory.CreateDirectory(Application.persistentDataPath);
            Directory.CreateDirectory(Path.GetDirectoryName(meshProjectPath));

            PullRequiredFile(adbPath, csvDevicePath, csvProjectPath, "trajectory CSV");
            File.Copy(csvProjectPath, csvEditorPath, true);
            string csvProjectHash = HashFile(csvProjectPath);
            string csvEditorHash = HashFile(csvEditorPath);

            if (csvProjectHash != csvEditorHash)
            {
                throw new IOException("CSV copy hash mismatch. Project CSV and visualizer CSV are not identical.");
            }

            bool csvHasRoomAnchor = CsvHasNonIdentityAnchor(csvEditorPath);

            PullRequiredFile(adbPath, meshDevicePath, meshProjectPath, "room mesh OBJ");
            string meshHash = HashFile(meshProjectPath);

            AssetDatabase.ImportAsset(MeshAssetPath, ImportAssetOptions.ForceUpdate);
            BuildUnityMeshAssetFromObj(meshProjectPath);
            AssetDatabase.Refresh();

            FileInfo csvInfo = new FileInfo(csvEditorPath);
            FileInfo meshInfo = new FileInfo(meshProjectPath);

            string anchorWarning = csvHasRoomAnchor
                ? ""
                : "\nWARNING: CSV anchor is identity/zero. This trajectory was not logged against the MRUK room anchor and will be misaligned.";

            UnityEngine.Debug.Log(
                "Quest capture synced.\n" +
                "CSV for visualizers: " + csvEditorPath + " (" + csvInfo.Length + " bytes, sha256 " + csvEditorHash + ")\n" +
                "Project CSV: " + csvProjectPath + " (sha256 " + csvProjectHash + ")\n" +
                "Mesh OBJ: " + meshProjectPath + " (" + meshInfo.Length + " bytes, sha256 " + meshHash + ")\n" +
                "Unity mesh prefab: " + GeneratedMeshPrefabPath +
                anchorWarning
            );

            EditorUtility.DisplayDialog(
                csvHasRoomAnchor ? "Quest Capture Synced" : "Quest Capture Synced With Warning",
                "Updated trajectory CSV and room mesh.\n\n" +
                (csvHasRoomAnchor ? "" : "WARNING: The CSV anchor is still identity/zero, so this trajectory will be misaligned. Rebuild/install the latest APK and redo the trajectory.\n\n") +
                "Press R in Play Mode to regenerate the visualization.",
                "OK"
            );
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("Quest capture sync failed: " + e.Message);
            EditorUtility.DisplayDialog("Quest Capture Sync Failed", e.Message, "OK");
        }
    }

    private static string FindAdbPath()
    {
        string unityAdbPath = Path.Combine(
            EditorApplication.applicationContentsPath,
            "PlaybackEngines",
            "AndroidPlayer",
            "SDK",
            "platform-tools",
            "adb.exe"
        );

        if (File.Exists(unityAdbPath))
        {
            return unityAdbPath;
        }

        string localAndroidAdbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Android",
            "Sdk",
            "platform-tools",
            "adb.exe"
        );

        if (File.Exists(localAndroidAdbPath))
        {
            return localAndroidAdbPath;
        }

        throw new FileNotFoundException("Could not find adb.exe in Unity's Android SDK or the local Android SDK.");
    }

    private static void EnsureDeviceReady(string adbPath)
    {
        string output = RunAdb(adbPath, "devices");

        if (output.Contains("unauthorized"))
        {
            throw new InvalidOperationException("Quest is unauthorized. Put on the headset and accept 'Allow USB debugging'.");
        }

        string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (string line in lines)
        {
            if (line.EndsWith("\tdevice", StringComparison.Ordinal))
            {
                return;
            }
        }

        throw new InvalidOperationException("No ready Quest found. Connect the headset and enable USB debugging.");
    }

    private static void PullRequiredFile(string adbPath, string devicePath, string destinationPath, string label)
    {
        string output = RunAdb(adbPath, "pull \"" + devicePath + "\" \"" + destinationPath + "\"");

        if (!File.Exists(destinationPath))
        {
            throw new FileNotFoundException("Could not pull " + label + " from Quest: " + devicePath + "\n" + output);
        }
    }

    private static string RunAdb(string adbPath, string arguments)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = adbPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using (Process process = Process.Start(startInfo))
        {
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(AdbTimeoutMilliseconds))
            {
                process.Kill();
                throw new TimeoutException("adb timed out while running: adb " + arguments);
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "adb " + arguments + " failed.\n" +
                    output + "\n" +
                    error
                );
            }

            return output + error;
        }
    }

    private static string HashFile(string path)
    {
        using (SHA256 sha256 = SHA256.Create())
        using (FileStream stream = File.OpenRead(path))
        {
            byte[] hash = sha256.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }

    private static void BuildUnityMeshAssetFromObj(string objPath)
    {
        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();

        foreach (string line in File.ReadLines(objPath))
        {
            if (line.StartsWith("v ", StringComparison.Ordinal))
            {
                string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length < 4)
                {
                    continue;
                }

                vertices.Add(new Vector3(
                    ParseInvariantFloat(parts[1]),
                    ParseInvariantFloat(parts[2]),
                    ParseInvariantFloat(parts[3])
                ));
            }
            else if (line.StartsWith("f ", StringComparison.Ordinal))
            {
                string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length < 4)
                {
                    continue;
                }

                int first = ParseObjFaceIndex(parts[1], vertices.Count);
                int previous = ParseObjFaceIndex(parts[2], vertices.Count);

                for (int i = 3; i < parts.Length; i++)
                {
                    int current = ParseObjFaceIndex(parts[i], vertices.Count);
                    triangles.Add(first);
                    triangles.Add(previous);
                    triangles.Add(current);
                    previous = current;
                }
            }
        }

        if (vertices.Count == 0 || triangles.Count == 0)
        {
            throw new InvalidOperationException("Could not generate Unity mesh asset because the OBJ has no vertices or faces.");
        }

        Mesh mesh = new Mesh
        {
            name = "quest_room_mesh_generated"
        };

        if (vertices.Count > 65535)
        {
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        }

        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        if (File.Exists(Path.Combine(Directory.GetParent(Application.dataPath).FullName, GeneratedMeshAssetPath)))
        {
            AssetDatabase.DeleteAsset(GeneratedMeshAssetPath);
        }

        AssetDatabase.CreateAsset(mesh, GeneratedMeshAssetPath);

        GameObject prefabRoot = new GameObject("quest_room_mesh_generated");
        MeshFilter meshFilter = prefabRoot.AddComponent<MeshFilter>();
        MeshRenderer meshRenderer = prefabRoot.AddComponent<MeshRenderer>();

        meshFilter.sharedMesh = mesh;
        meshRenderer.sharedMaterial = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");

        PrefabUtility.SaveAsPrefabAsset(prefabRoot, GeneratedMeshPrefabPath);
        UnityEngine.Object.DestroyImmediate(prefabRoot);
    }

    private static int ParseObjFaceIndex(string token, int vertexCount)
    {
        string indexText = token.Split('/')[0];
        int index = int.Parse(indexText, CultureInfo.InvariantCulture);

        if (index < 0)
        {
            return vertexCount + index;
        }

        return index - 1;
    }

    private static float ParseInvariantFloat(string value)
    {
        return float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static bool CsvHasNonIdentityAnchor(string csvPath)
    {
        string[] lines = File.ReadAllLines(csvPath);

        if (lines.Length < 2)
        {
            return false;
        }

        string[] headers = lines[0].Split(',');
        string[] values = lines[1].Split(',');

        int source = Array.IndexOf(headers, "anchor_source");

        if (source >= 0 && source < values.Length)
        {
            return values[source].Trim().Replace("\"", "") == "MRUK";
        }

        int px = Array.IndexOf(headers, "anchor_pos_x");
        int py = Array.IndexOf(headers, "anchor_pos_y");
        int pz = Array.IndexOf(headers, "anchor_pos_z");
        int rx = Array.IndexOf(headers, "anchor_rot_x");
        int ry = Array.IndexOf(headers, "anchor_rot_y");
        int rz = Array.IndexOf(headers, "anchor_rot_z");
        int rw = Array.IndexOf(headers, "anchor_rot_w");

        if (px < 0 || py < 0 || pz < 0 || rx < 0 || ry < 0 || rz < 0 || rw < 0)
        {
            return false;
        }

        float anchorPosMagnitude =
            Mathf.Abs(ParseCsvFloat(values, px)) +
            Mathf.Abs(ParseCsvFloat(values, py)) +
            Mathf.Abs(ParseCsvFloat(values, pz));

        float identityRotationDelta =
            Mathf.Abs(ParseCsvFloat(values, rx)) +
            Mathf.Abs(ParseCsvFloat(values, ry)) +
            Mathf.Abs(ParseCsvFloat(values, rz)) +
            Mathf.Abs(ParseCsvFloat(values, rw) - 1f);

        return anchorPosMagnitude > 0.001f || identityRotationDelta > 0.001f;
    }

    private static float ParseCsvFloat(string[] values, int index)
    {
        if (index < 0 || index >= values.Length)
        {
            return 0f;
        }

        float.TryParse(
            values[index].Trim().Replace("\"", ""),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out float value
        );

        return value;
    }
}
