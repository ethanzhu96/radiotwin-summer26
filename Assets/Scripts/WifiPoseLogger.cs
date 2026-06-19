using System;
using System.IO;
using System.Globalization;
using UnityEngine;

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

public class WifiPoseLogger : MonoBehaviour
{
    public Transform centerEyeAnchor;
    public Transform anchorTransform;

    public string fileName = "rf_trajectory_log.csv";
    public float sampleIntervalSeconds = 1.0f;

    private string filePath;
    private float timer = 0f;

#if UNITY_ANDROID && !UNITY_EDITOR
    private AndroidJavaObject wifiManager;
#endif

    void Start()
    {
        Debug.LogError("WIFI_POSE_LOGGER_START_REACHED");

        filePath = Path.Combine(Application.persistentDataPath, fileName);

        string header =
            "timestamp_unix_ms," +
            "world_pos_x,world_pos_y,world_pos_z," +
            "world_rot_x,world_rot_y,world_rot_z,world_rot_w," +
            "anchor_pos_x,anchor_pos_y,anchor_pos_z," +
            "anchor_rot_x,anchor_rot_y,anchor_rot_z,anchor_rot_w," +
            "ssid,bssid,rssi_dbm,frequency_mhz,link_speed_mbps\n";

        File.WriteAllText(filePath, header);
        Debug.LogError("WIFI_POSE_LOGGER_FILE_CREATED: " + filePath);

#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                wifiManager = activity.Call<AndroidJavaObject>("getSystemService", "wifi");
            }

            if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
            {
                Permission.RequestUserPermission(Permission.FineLocation);
            }

            if (!Permission.HasUserAuthorizedPermission(Permission.CoarseLocation))
            {
                Permission.RequestUserPermission(Permission.CoarseLocation);
            }
        }
        catch (Exception e)
        {
            Debug.LogError("WIFI_POSE_LOGGER_ANDROID_INIT_FAILED: " + e);
        }
#endif
    }

    void Update()
    {
        timer += Time.unscaledDeltaTime;

        if (timer >= sampleIntervalSeconds)
        {
            Debug.LogError("WIFI_POSE_LOGGER_TIMER_PASSED");
            timer = 0f;
            LogWifiAndPose();
        }
    }

    void LogWifiAndPose()
    {
        Debug.LogError("WIFI_POSE_LOGGER_ROW_ATTEMPT");

        if (centerEyeAnchor == null)
        {
            Debug.LogError("WIFI_POSE_LOGGER_NO_CENTER_EYE_ANCHOR");
            return;
        }

        long timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Vector3 worldPos = centerEyeAnchor.position;
        Quaternion worldRot = centerEyeAnchor.rotation;

        Vector3 anchorPos = Vector3.zero;
        Quaternion anchorRot = Quaternion.identity;

        if (anchorTransform != null)
        {
            anchorPos = anchorTransform.position;
            anchorRot = anchorTransform.rotation;
        }
        else
        {
            Debug.LogError("WIFI_POSE_LOGGER_NO_ANCHOR_TRANSFORM");
        }

        string ssid = "UNKNOWN";
        string bssid = "UNKNOWN";
        int rssi = -999;
        int frequency = -1;
        int linkSpeed = -1;

#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            if (wifiManager != null)
            {
                using (AndroidJavaObject wifiInfo = wifiManager.Call<AndroidJavaObject>("getConnectionInfo"))
                {
                    if (wifiInfo != null)
                    {
                        ssid = wifiInfo.Call<string>("getSSID");
                        bssid = wifiInfo.Call<string>("getBSSID");
                        rssi = wifiInfo.Call<int>("getRssi");
                        frequency = wifiInfo.Call<int>("getFrequency");
                        linkSpeed = wifiInfo.Call<int>("getLinkSpeed");
                    }
                }
            }
            else
            {
                Debug.LogError("WIFI_POSE_LOGGER_WIFI_MANAGER_NULL");
            }
        }
        catch (Exception e)
        {
            Debug.LogError("WIFI_POSE_LOGGER_WIFI_READ_FAILED: " + e);
        }
#endif

        string line =
            timestampMs + "," +
            F(worldPos.x) + "," + F(worldPos.y) + "," + F(worldPos.z) + "," +
            F(worldRot.x) + "," + F(worldRot.y) + "," + F(worldRot.z) + "," + F(worldRot.w) + "," +
            F(anchorPos.x) + "," + F(anchorPos.y) + "," + F(anchorPos.z) + "," +
            F(anchorRot.x) + "," + F(anchorRot.y) + "," + F(anchorRot.z) + "," + F(anchorRot.w) + "," +
            Csv(ssid) + "," +
            Csv(bssid) + "," +
            rssi + "," +
            frequency + "," +
            linkSpeed + "\n";

        try
        {
            File.AppendAllText(filePath, line);
            Debug.LogError("WIFI_POSE_LOGGER_APPEND_SUCCESS: " + line);
        }
        catch (Exception e)
        {
            Debug.LogError("WIFI_POSE_LOGGER_APPEND_FAILED: " + e);
        }
    }

    string F(float value)
    {
        return value.ToString("F6", CultureInfo.InvariantCulture);
    }

    string Csv(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "UNKNOWN";
        }

        value = value.Replace("\"", "\"\"");
        return "\"" + value + "\"";
    }
}