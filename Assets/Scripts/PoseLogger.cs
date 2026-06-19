using System;
using System.IO;
using UnityEngine;

public class PoseLogger : MonoBehaviour
{
    public Transform centerEyeAnchor;
    public string fileName = "pose_log.csv";
    public float sampleIntervalSeconds = 0.1f;

    private string filePath;
    private float timer = 0f;

    void Start()
    {
        filePath = Path.Combine(Application.persistentDataPath, fileName);

        File.WriteAllText(
            filePath,
            "timestamp_unix_ms,pos_x,pos_y,pos_z,rot_x,rot_y,rot_z,rot_w\n"
        );

        Debug.Log("PoseLogger writing to: " + filePath);

        if (centerEyeAnchor == null)
        {
            Debug.LogError("PoseLogger: centerEyeAnchor is not assigned.");
        }
    }

    void Update()
    {
        if (centerEyeAnchor == null) return;

        timer += Time.deltaTime;

        if (timer >= sampleIntervalSeconds)
        {
            timer = 0f;
            LogPose();
        }
    }

    void LogPose()
    {
        long timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Vector3 p = centerEyeAnchor.position;
        Quaternion q = centerEyeAnchor.rotation;

        string row =
            $"{timestampMs},{p.x},{p.y},{p.z},{q.x},{q.y},{q.z},{q.w}\n";

        File.AppendAllText(filePath, row);
    }
}