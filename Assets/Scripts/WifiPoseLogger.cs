using System;
using System.IO;
using System.Globalization;
using UnityEngine;
using Meta.XR.MRUtilityKit;

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

public class WifiPoseLogger : MonoBehaviour
{
    public Transform centerEyeAnchor;
    public Transform anchorTransform;
    public string roomAnchorObjectName = "RoomAnchor";
    public bool preferMRUKRoomAnchor = true;
    public bool requireMRUKRoomAnchorForLogging = true;

    public string fileName = "rf_trajectory_log.csv";
    public float sampleIntervalSeconds = 1.0f;

    [Header("Logging Controls")]
    public bool isLogging = false;
    public KeyCode toggleLoggingKey = KeyCode.S;
    public OVRInput.Button toggleLoggingButton = OVRInput.Button.Three;
    public bool overwriteFileWhenLoggingStarts = true;

    [Header("Status Display")]
    public bool showStatusDisplay = true;
    public Vector3 statusDisplayLocalPosition = new Vector3(0.45f, 0.26f, 1.25f);
    public float statusTextSize = 0.018f;
    public float statusMessageSeconds = 1.5f;

    private string filePath;
    private float timer = 0f;
    private TextMesh statusText;
    private string statusMessage = "";
    private float statusMessageUntil = 0f;
    private int rowsWritten = 0;
    private bool logFileInitialized = false;
    private bool usingMRUKRoomAnchor = false;
    private string anchorSource = "NONE";
    private string anchorName = "NONE";

#if UNITY_ANDROID && !UNITY_EDITOR
    private AndroidJavaObject wifiManager;
#endif

    void Start()
    {
        Debug.LogError("WIFI_POSE_LOGGER_START_REACHED");
        ResolveRoomAnchor();

        filePath = Path.Combine(Application.persistentDataPath, fileName);
        ShowStatusMessage("LOG READY");

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
        UpdateStatusDisplay();

        if (Input.GetKeyDown(toggleLoggingKey) || OVRInput.GetDown(toggleLoggingButton))
        {
            ToggleLogging();
        }

        if (!isLogging)
        {
            return;
        }

        timer += Time.unscaledDeltaTime;

        if (timer >= sampleIntervalSeconds)
        {
            Debug.LogError("WIFI_POSE_LOGGER_TIMER_PASSED");
            timer = 0f;
            LogWifiAndPose();
        }
    }

    public void UseRoomAnchor()
    {
        ResolveRoomAnchor();
    }

    void ResolveRoomAnchor()
    {
        usingMRUKRoomAnchor = false;
        anchorSource = "NONE";
        anchorName = "NONE";

        if (preferMRUKRoomAnchor && MRUK.Instance != null && MRUK.Instance.GetCurrentRoom() != null)
        {
            anchorTransform = MRUK.Instance.GetCurrentRoom().transform;
            usingMRUKRoomAnchor = true;
            anchorSource = "MRUK";
            anchorName = anchorTransform.name;
            Debug.LogError("WIFI_POSE_LOGGER_USING_MRUK_ROOM_ANCHOR: " + anchorTransform.name);
            return;
        }

        if (requireMRUKRoomAnchorForLogging)
        {
            anchorTransform = null;
            Debug.LogError("WIFI_POSE_LOGGER_WAITING_FOR_MRUK_ROOM_ANCHOR");
            return;
        }

        GameObject roomAnchor = GameObject.Find(roomAnchorObjectName);

        if (roomAnchor != null)
        {
            anchorTransform = roomAnchor.transform;
            anchorSource = "ROOM_ANCHOR_OBJECT";
            anchorName = roomAnchor.name;
            Debug.LogError("WIFI_POSE_LOGGER_USING_ROOM_ANCHOR: " + roomAnchorObjectName);
        }
        else if (anchorTransform == null)
        {
            Debug.LogError("WIFI_POSE_LOGGER_ROOM_ANCHOR_NOT_FOUND: " + roomAnchorObjectName);
        }
    }

    public void ToggleLogging()
    {
        isLogging = !isLogging;
        timer = 0f;

        if (isLogging)
        {
            ResolveRoomAnchor();
            InitializeLogFileForSession();
            Debug.LogError("WIFI_POSE_LOGGER_RESUMED");
            ShowStatusMessage(usingMRUKRoomAnchor ? "ROOM OK" : "WAIT ROOM");
        }
        else
        {
            Debug.LogError("WIFI_POSE_LOGGER_PAUSED");
            ShowStatusMessage("LOGGING STOPPED");
        }
    }

    void InitializeLogFileForSession()
    {
        if (logFileInitialized)
        {
            return;
        }

        string header =
            "timestamp_unix_ms," +
            "world_pos_x,world_pos_y,world_pos_z," +
            "world_rot_x,world_rot_y,world_rot_z,world_rot_w," +
            "anchor_pos_x,anchor_pos_y,anchor_pos_z," +
            "anchor_rot_x,anchor_rot_y,anchor_rot_z,anchor_rot_w," +
            "anchor_local_x,anchor_local_y,anchor_local_z," +
            "anchor_local_rot_x,anchor_local_rot_y,anchor_local_rot_z,anchor_local_rot_w," +
            "anchor_source,anchor_name," +
            "ssid,bssid,rssi_dbm,frequency_mhz,link_speed_mbps\n";

        if (overwriteFileWhenLoggingStarts || !File.Exists(filePath))
        {
            File.WriteAllText(filePath, header);
            rowsWritten = 0;
            Debug.LogError("WIFI_POSE_LOGGER_FILE_CREATED: " + filePath);
        }
        else
        {
            Debug.LogError("WIFI_POSE_LOGGER_REUSING_EXISTING_FILE: " + filePath);
        }

        logFileInitialized = true;
    }

    void EnsureStatusDisplay()
    {
        if (!showStatusDisplay || statusText != null || centerEyeAnchor == null)
        {
            return;
        }

        GameObject statusObject = new GameObject("WifiPoseLogger_StatusDisplay");
        statusObject.transform.SetParent(centerEyeAnchor, false);
        statusObject.transform.localPosition = statusDisplayLocalPosition;
        statusObject.transform.localRotation = Quaternion.identity;

        statusText = statusObject.AddComponent<TextMesh>();
        statusText.anchor = TextAnchor.UpperRight;
        statusText.alignment = TextAlignment.Right;
        statusText.fontSize = 64;
        statusText.characterSize = statusTextSize;
        statusText.color = Color.white;
    }

    void UpdateStatusDisplay()
    {
        EnsureStatusDisplay();

        if (statusText == null)
        {
            return;
        }

        statusText.transform.localPosition = statusDisplayLocalPosition;
        statusText.transform.localRotation = Quaternion.identity;
        statusText.characterSize = statusTextSize;

        string state = isLogging ? "LOG ON" : "LOG OFF";
        string rows = rowsWritten.ToString(CultureInfo.InvariantCulture) + " rows";

        if (Time.unscaledTime < statusMessageUntil && !string.IsNullOrEmpty(statusMessage))
        {
            statusText.text = state + "\n" + statusMessage + "\n" + rows;
        }
        else
        {
            statusText.text = state + "\n" + rows;
        }

        statusText.color = isLogging ? Color.green : Color.white;
    }

    void ShowStatusMessage(string message)
    {
        statusMessage = message;
        statusMessageUntil = Time.unscaledTime + statusMessageSeconds;
        UpdateStatusDisplay();
    }

    void LogWifiAndPose()
    {
        Debug.LogError("WIFI_POSE_LOGGER_ROW_ATTEMPT");

        if (centerEyeAnchor == null)
        {
            Debug.LogError("WIFI_POSE_LOGGER_NO_CENTER_EYE_ANCHOR");
            ShowStatusMessage("NO CENTER EYE ANCHOR");
            return;
        }

        long timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Vector3 worldPos = centerEyeAnchor.position;
        Quaternion worldRot = centerEyeAnchor.rotation;

        ResolveRoomAnchor();

        if (requireMRUKRoomAnchorForLogging && !usingMRUKRoomAnchor)
        {
            Debug.LogError("WIFI_POSE_LOGGER_SKIPPED_ROW_WAITING_FOR_MRUK_ROOM_ANCHOR");
            ShowStatusMessage("WAIT ROOM");
            return;
        }

        Vector3 anchorPos = Vector3.zero;
        Quaternion anchorRot = Quaternion.identity;
        Vector3 anchorLocalPos = Vector3.zero;
        Quaternion anchorLocalRot = Quaternion.identity;

        if (anchorTransform != null)
        {
            anchorPos = anchorTransform.position;
            anchorRot = anchorTransform.rotation;
            anchorLocalPos = anchorTransform.InverseTransformPoint(worldPos);
            anchorLocalRot = Quaternion.Inverse(anchorRot) * worldRot;
        }
        else
        {
            Debug.LogError("WIFI_POSE_LOGGER_NO_ANCHOR_TRANSFORM");
            ShowStatusMessage("NO ROOM ANCHOR");
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
            F(anchorLocalPos.x) + "," + F(anchorLocalPos.y) + "," + F(anchorLocalPos.z) + "," +
            F(anchorLocalRot.x) + "," + F(anchorLocalRot.y) + "," + F(anchorLocalRot.z) + "," + F(anchorLocalRot.w) + "," +
            Csv(anchorSource) + "," +
            Csv(anchorName) + "," +
            Csv(ssid) + "," +
            Csv(bssid) + "," +
            rssi + "," +
            frequency + "," +
            linkSpeed + "\n";

        try
        {
            File.AppendAllText(filePath, line);
            rowsWritten++;
            ShowStatusMessage(usingMRUKRoomAnchor ? "ROW SAVED MRUK" : "ROW SAVED");
            Debug.LogError("WIFI_POSE_LOGGER_APPEND_SUCCESS: " + line);
        }
        catch (Exception e)
        {
            Debug.LogError("WIFI_POSE_LOGGER_APPEND_FAILED: " + e);
            ShowStatusMessage("WRITE FAILED");
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
