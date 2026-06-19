using System;
using System.IO;
using UnityEngine;

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

public class WifiPoseLogger : MonoBehaviour
{
    public Transform centerEyeAnchor;
    public string fileName = "rf_trajectory_log.csv";
    public float sampleIntervalSeconds = 1.0f;

    private string filePath;
    private float timer = 0f;

#if UNITY_ANDROID && !UNITY_EDITOR
    private AndroidJavaObject wifiManager;
#endif

    void Start()
    {
        filePath = Path.Combine(Application.persistentDataPath, fileName);

        File.WriteAllText(
            filePath,
            "timestamp_unix_ms,pos_x,pos_y,pos_z,rot_x,rot_y,rot_z,rot_w,ssid,bssid,rssi_dbm,frequency_mhz,link_speed_mbps\n"
        );

        Debug.Log("WifiPoseLogger writing to: " + filePath);

        if (centerEyeAnchor == null)
        {
            Debug.LogError("WifiPoseLogger: centerEyeAnchor is not assigned.");
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        // Wi-Fi SSID/BSSID access may require location permission on Android.
        if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
        {
            Permission.RequestUserPermission(Permission.FineLocation);
            Debug.Log("WifiPoseLogger requested Fine Location permission.");
        }

        AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
        AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
        AndroidJavaObject appContext = activity.Call<AndroidJavaObject>("getApplicationContext");

        AndroidJavaClass contextClass = new AndroidJavaClass("android.content.Context");
        string wifiService = contextClass.GetStatic<string>("WIFI_SERVICE");

        wifiManager = appContext.Call<AndroidJavaObject>("getSystemService", wifiService);

        if (wifiManager == null)
        {
            Debug.LogError("WifiPoseLogger: wifiManager is null.");
        }
        else
        {
            Debug.Log("WifiPoseLogger: wifiManager initialized.");
        }
#endif
    }

    void Update()
    {
        if (centerEyeAnchor == null) return;

        timer += Time.deltaTime;

        if (timer >= sampleIntervalSeconds)
        {
            timer = 0f;
            LogWifiAndPose();
        }
    }

    void LogWifiAndPose()
    {
        long timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Vector3 p = centerEyeAnchor.position;
        Quaternion q = centerEyeAnchor.rotation;

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
                AndroidJavaObject wifiInfo = wifiManager.Call<AndroidJavaObject>("getConnectionInfo");

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
        catch (Exception e)
        {
            Debug.LogError("WifiPoseLogger Wi-Fi read failed: " + e.Message);
        }
#else
        ssid = "EDITOR_FAKE_SSID";
        bssid = "EDITOR_FAKE_BSSID";
        rssi = -50;
        frequency = 5200;
        linkSpeed = 100;
#endif

        string cleanSsid = CleanCsv(ssid);
        string cleanBssid = CleanCsv(bssid);

        string row =
            $"{timestampMs},{p.x},{p.y},{p.z},{q.x},{q.y},{q.z},{q.w},{cleanSsid},{cleanBssid},{rssi},{frequency},{linkSpeed}\n";

        File.AppendAllText(filePath, row);

        Debug.Log($"WifiPoseLogger row: RSSI={rssi}, SSID={ssid}, BSSID={bssid}");
    }

    string CleanCsv(string input)
    {
        if (string.IsNullOrEmpty(input)) return "UNKNOWN";
        return input.Replace(",", "_").Replace("\n", "").Replace("\r", "");
    }
}