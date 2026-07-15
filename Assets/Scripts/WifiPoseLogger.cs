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
    public string metadataFileName = RoomAlignmentManager.DefaultMetadataFileName;
    public string meshFileName = "quest_room_mesh.obj";
    public float sampleIntervalSeconds = 1.0f;

    [Header("Logging Controls")]
    public bool isLogging = false;
    public KeyCode toggleLoggingKey = KeyCode.S;
    public bool overwriteFileWhenLoggingStarts = true;

    [Header("Status Display")]
    public bool showStatusDisplay = true;
    public Vector3 statusDisplayLocalPosition = new Vector3(0.62f, 0.36f, 1.25f);
    public float statusTextSize = 0.012f;
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
    private Guid captureRoomUuid = Guid.Empty;
    private Guid captureReferenceAnchorUuid = Guid.Empty;
    private Quaternion captureReferenceFrameLocalRotation = Quaternion.identity;

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

        if (Input.GetKeyDown(toggleLoggingKey) ||
            OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch))
        {
            Debug.Log("[ControllerInput] Left trigger down: toggle logging");
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

        if (preferMRUKRoomAnchor && MRUK.Instance != null && MRUK.Instance.IsInitialized && MRUK.Instance.GetCurrentRoom() != null)
        {
            MRUKRoom room = MRUK.Instance.GetCurrentRoom();
            Guid roomUuid = room.Anchor.Uuid;
            if (roomUuid == Guid.Empty)
            {
                anchorTransform = null;
                Debug.LogError(RoomAlignmentManager.LogPrefix + " Current MRUK room has an empty anchor UUID.");
                return;
            }

            if (captureRoomUuid != Guid.Empty && roomUuid != captureRoomUuid)
            {
                anchorTransform = null;
                Debug.LogError(RoomAlignmentManager.LogPrefix + " Recording room changed. Expected " + captureRoomUuid + " but current room is " + roomUuid + ". Row rejected.");
                return;
            }

            MRUKAnchor referenceAnchor = null;
            if (captureReferenceAnchorUuid != Guid.Empty)
            {
                foreach (MRUKAnchor candidate in room.Anchors)
                {
                    if (candidate.Anchor.Uuid == captureReferenceAnchorUuid)
                    {
                        referenceAnchor = candidate;
                        break;
                    }
                }
            }
            else
            {
                RoomAlignmentManager.TryGetCaptureReferenceAnchor(room, out referenceAnchor);
            }

            if (referenceAnchor == null)
            {
                anchorTransform = null;
                Debug.LogError(RoomAlignmentManager.LogPrefix + " No valid global-mesh or floor scene anchor is available for capture.");
                return;
            }

            anchorTransform = referenceAnchor.transform;
            usingMRUKRoomAnchor = true;
            anchorSource = "MRUK_SCENE_ANCHOR";
            anchorName = anchorTransform.name;
            Debug.Log(RoomAlignmentManager.LogPrefix + " Capture reference anchor ready: " + anchorTransform.name +
                " roomUUID=" + roomUuid + " anchorUUID=" + referenceAnchor.Anchor.Uuid + " label=" + referenceAnchor.Label);
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
            if (!InitializeLogFileForSession())
            {
                isLogging = false;
                ShowStatusMessage("ROOM REQUIRED");
                return;
            }
            Debug.LogError("WIFI_POSE_LOGGER_RESUMED");
            ShowStatusMessage(usingMRUKRoomAnchor ? "ROOM OK" : "WAIT ROOM");
        }
        else
        {
            Debug.LogError("WIFI_POSE_LOGGER_PAUSED");
            ShowStatusMessage("LOGGING STOPPED");
        }
    }

    bool InitializeLogFileForSession()
    {
        if (logFileInitialized)
        {
            return true;
        }

        if (MRUK.Instance == null || !MRUK.Instance.IsInitialized || MRUK.Instance.GetCurrentRoom() == null)
        {
            Debug.LogError(RoomAlignmentManager.LogPrefix + " Cannot start capture: MRUK is not initialized or has no current room.");
            return false;
        }

        MRUKRoom captureRoom = MRUK.Instance.GetCurrentRoom();
        captureRoomUuid = captureRoom.Anchor.Uuid;
        if (captureRoomUuid == Guid.Empty)
        {
            Debug.LogError(RoomAlignmentManager.LogPrefix + " Cannot start capture: current room UUID is empty.");
            captureRoomUuid = Guid.Empty;
            return false;
        }

        if (!RoomAlignmentManager.TryGetCaptureReferenceAnchor(captureRoom, out MRUKAnchor referenceAnchor))
        {
            Debug.LogError(RoomAlignmentManager.LogPrefix + " Cannot start capture: no stable global-mesh or floor scene anchor exists.");
            return false;
        }

        captureReferenceAnchorUuid = referenceAnchor.Anchor.Uuid;
        captureReferenceFrameLocalRotation = RoomAlignmentManager.GetUprightReferenceLocalRotation(referenceAnchor);

        anchorTransform = referenceAnchor.transform;
        usingMRUKRoomAnchor = true;

        string header =
            "timestamp_unix_ms," +
            "world_pos_x,world_pos_y,world_pos_z," +
            "world_rot_x,world_rot_y,world_rot_z,world_rot_w," +
            "anchor_pos_x,anchor_pos_y,anchor_pos_z," +
            "anchor_rot_x,anchor_rot_y,anchor_rot_z,anchor_rot_w," +
            "reference_local_pos_x,reference_local_pos_y,reference_local_pos_z," +
            "reference_local_rot_x,reference_local_rot_y,reference_local_rot_z,reference_local_rot_w," +
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
        WriteDatasetMetadata(captureRoom, referenceAnchor);
        Debug.Log(RoomAlignmentManager.LogPrefix + " Saved room UUID: " + captureRoomUuid);
        Debug.Log(RoomAlignmentManager.LogPrefix + " Saved reference anchor UUID: " + captureReferenceAnchorUuid);
        Debug.Log(RoomAlignmentManager.LogPrefix + " CSV path: " + filePath);
        Debug.Log(RoomAlignmentManager.LogPrefix + " Positions and rotations are saved in matched MRUK scene-anchor-local coordinates.");
        return true;
    }

    void WriteDatasetMetadata(MRUKRoom captureRoom, MRUKAnchor referenceAnchor)
    {
        RoomAlignmentMetadata metadata = new RoomAlignmentMetadata
        {
            roomUuid = captureRoomUuid.ToString(),
            referenceAnchorUuid = captureReferenceAnchorUuid.ToString(),
            referenceAnchorLabel = referenceAnchor.Label.ToString(),
            referenceFrameLocalRotX = captureReferenceFrameLocalRotation.x,
            referenceFrameLocalRotY = captureReferenceFrameLocalRotation.y,
            referenceFrameLocalRotZ = captureReferenceFrameLocalRotation.z,
            referenceFrameLocalRotW = captureReferenceFrameLocalRotation.w,
            trajectoryFile = fileName,
            meshFile = meshFileName,
            captureTimestampUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
        };
        string metadataPath = Path.Combine(Application.persistentDataPath, metadataFileName);
        File.WriteAllText(metadataPath, JsonUtility.ToJson(metadata, true));
        Debug.Log(RoomAlignmentManager.LogPrefix + " Capture room object: " + captureRoom.name);
        Debug.Log(RoomAlignmentManager.LogPrefix + " Metadata path: " + metadataPath);
        Debug.Log(RoomAlignmentManager.LogPrefix + " Mesh path: " + Path.Combine(Application.persistentDataPath, meshFileName));
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
            anchorRot = anchorTransform.rotation * captureReferenceFrameLocalRotation;
            anchorLocalPos = Quaternion.Inverse(anchorRot) * (worldPos - anchorPos);
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
