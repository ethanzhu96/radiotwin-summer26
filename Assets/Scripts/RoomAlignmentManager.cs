using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Meta.XR.MRUtilityKit;
using UnityEngine;

[Serializable]
public class RoomAlignmentMetadata
{
    public int formatVersion = 3;
    public string roomUuid;
    public string referenceAnchorUuid;
    public string referenceAnchorLabel;
    public float referenceFrameLocalRotX;
    public float referenceFrameLocalRotY;
    public float referenceFrameLocalRotZ;
    public float referenceFrameLocalRotW = 1f;
    public string coordinateSpace = RoomAlignmentManager.CoordinateSpace;
    public string trajectoryFile;
    public string meshFile;
    public string captureTimestampUtc;
}

[DefaultExecutionOrder(-500)]
public class RoomAlignmentManager : MonoBehaviour
{
    public const string LogPrefix = "[RoomAlignment]";
    public const string CoordinateSpace = "MRUK_SCENE_ANCHOR_LOCAL";
    public const string DefaultMetadataFileName = "rf_dataset_metadata.json";

    public enum PlaybackState
    {
        WaitingForMRUK,
        Ready,
        Rejected
    }

    public static RoomAlignmentManager Instance { get; private set; }
    public PlaybackState State { get; private set; } = PlaybackState.WaitingForMRUK;
    public Transform DatasetRoot { get; private set; }
    public MRUKRoom MatchedRoom { get; private set; }
    public MRUKAnchor MatchedReferenceAnchor { get; private set; }
    public RoomAlignmentMetadata Metadata { get; private set; }
    public Vector3? FirstTrajectorySampleLocalPosition { get; private set; }

    private MRUK subscribedMruk;
    private bool sceneCallbackHandled;
    private string rejectionMessage;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        EnsureInstance();
    }

    public static RoomAlignmentManager EnsureInstance()
    {
        if (Instance != null)
        {
            return Instance;
        }

        GameObject managerObject = new GameObject("RoomAlignmentManager");
        DontDestroyOnLoad(managerObject);
        return managerObject.AddComponent<RoomAlignmentManager>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private IEnumerator Start()
    {
        while (MRUK.Instance == null)
        {
            yield return null;
        }

        Subscribe(MRUK.Instance);
    }

    private void Subscribe(MRUK mruk)
    {
        if (subscribedMruk == mruk)
        {
            return;
        }

        Unsubscribe();
        subscribedMruk = mruk;
        subscribedMruk.RegisterSceneLoadedCallback(OnSceneLoaded);
    }

    private void Unsubscribe()
    {
        if (subscribedMruk != null)
        {
            subscribedMruk.SceneLoadedEvent.RemoveListener(OnSceneLoaded);
            subscribedMruk = null;
        }
    }

    private void OnDestroy()
    {
        Unsubscribe();
        if (Instance == this)
        {
            Instance = null;
        }
    }

    private void OnSceneLoaded()
    {
        if (sceneCallbackHandled)
        {
            return;
        }

        sceneCallbackHandled = true;
        StartCoroutine(PreparePlaybackAfterInitialization());
    }

    private IEnumerator PreparePlaybackAfterInitialization()
    {
        yield return null;
        PreparePlayback();
    }

    private void PreparePlayback()
    {
        MRUK mruk = MRUK.Instance;
        if (mruk == null || !mruk.IsInitialized)
        {
            Reject("MRUK scene-loaded callback fired before MRUK was initialized.");
            return;
        }

        if (mruk.SceneSettings == null)
        {
            Debug.LogWarning(LogPrefix + " MRUK SceneSettings is null; device-loading configuration could not be verified.");
        }
        else if (!mruk.SceneSettings.LoadSceneOnStartup)
        {
            Debug.LogWarning(LogPrefix + " MRUK LoadSceneOnStartup is disabled. Playback requires another component to load device scene data.");
        }

        if (mruk.SceneSettings != null &&
            mruk.SceneSettings.DataSource != MRUK.SceneDataSource.Device &&
            mruk.SceneSettings.DataSource != MRUK.SceneDataSource.DeviceWithJsonFallback &&
            mruk.SceneSettings.DataSource != MRUK.SceneDataSource.DeviceWithPrefabFallback)
        {
            Debug.LogWarning(LogPrefix + " MRUK is not configured to load scene data from the Quest device. DataSource=" + mruk.SceneSettings.DataSource);
        }

        string metadataPath = Path.Combine(Application.persistentDataPath, DefaultMetadataFileName);
        if (!File.Exists(metadataPath))
        {
            RejectLegacy("Metadata file is missing: " + metadataPath);
            return;
        }

        try
        {
            Metadata = JsonUtility.FromJson<RoomAlignmentMetadata>(File.ReadAllText(metadataPath));
        }
        catch (Exception exception)
        {
            Reject("Could not read dataset metadata: " + exception.Message);
            return;
        }

        if (Metadata == null || Metadata.formatVersion < 3 ||
            Metadata.coordinateSpace != CoordinateSpace || string.IsNullOrWhiteSpace(Metadata.roomUuid) ||
            string.IsNullOrWhiteSpace(Metadata.referenceAnchorUuid))
        {
            RejectLegacy("Metadata has no supported room UUID / scene-anchor coordinate declaration.");
            return;
        }

        string trajectoryPath = Path.Combine(Application.persistentDataPath, Metadata.trajectoryFile ?? string.Empty);
        if (!File.Exists(trajectoryPath))
        {
            Reject("Trajectory file declared by metadata is missing: " + trajectoryPath);
            return;
        }

        string csvHeader;
        using (StreamReader reader = new StreamReader(trajectoryPath))
        {
            csvHeader = reader.ReadLine() ?? string.Empty;
        }
        string normalizedHeader = csvHeader.ToLowerInvariant();
        if (!normalizedHeader.Contains("reference_local_pos_x") || !normalizedHeader.Contains("reference_local_pos_y") ||
            !normalizedHeader.Contains("reference_local_pos_z") || !normalizedHeader.Contains("reference_local_rot_w"))
        {
            RejectLegacy("Trajectory CSV has no authoritative scene-anchor-local position/rotation columns.");
            return;
        }

        if (!Guid.TryParse(Metadata.roomUuid, out Guid savedUuid) || savedUuid == Guid.Empty)
        {
            Reject("Metadata contains an invalid room UUID: " + Metadata.roomUuid);
            return;
        }

        if (!Guid.TryParse(Metadata.referenceAnchorUuid, out Guid savedReferenceUuid) || savedReferenceUuid == Guid.Empty)
        {
            Reject("Metadata contains an invalid reference anchor UUID: " + Metadata.referenceAnchorUuid);
            return;
        }

        Debug.Log(LogPrefix + " Saved room UUID: " + savedUuid);
        List<string> loadedUuids = new List<string>();
        foreach (MRUKRoom room in mruk.Rooms)
        {
            Guid loadedUuid = room.Anchor.Uuid;
            loadedUuids.Add(loadedUuid.ToString());
            Debug.Log(LogPrefix + " Loaded room UUID: " + loadedUuid + " (" + room.name + ")");
            if (loadedUuid == savedUuid)
            {
                MatchedRoom = room;
            }
        }

        if (MatchedRoom == null)
        {
            Reject("ROOM MISMATCH. Saved UUID=" + savedUuid + "; loaded UUIDs=" + string.Join(", ", loadedUuids));
            return;
        }

        Debug.Log(LogPrefix + " Saved reference anchor UUID: " + savedReferenceUuid);
        foreach (MRUKAnchor anchor in MatchedRoom.Anchors)
        {
            Guid loadedAnchorUuid = anchor.Anchor.Uuid;
            Debug.Log(LogPrefix + " Loaded scene anchor UUID: " + loadedAnchorUuid + " label=" + anchor.Label);
            if (loadedAnchorUuid == savedReferenceUuid)
            {
                MatchedReferenceAnchor = anchor;
            }
        }

        if (MatchedReferenceAnchor == null)
        {
            Reject("REFERENCE ANCHOR MISMATCH. Saved anchor UUID=" + savedReferenceUuid +
                " was not found inside matched room " + savedUuid + ".");
            return;
        }

        Transform existing = MatchedReferenceAnchor.transform.Find("DatasetRoot");
        if (existing == null)
        {
            existing = new GameObject("DatasetRoot").transform;
        }

        existing.SetParent(MatchedReferenceAnchor.transform, false);
        existing.localPosition = Vector3.zero;
        Quaternion referenceFrameLocalRotation = new Quaternion(
            Metadata.referenceFrameLocalRotX,
            Metadata.referenceFrameLocalRotY,
            Metadata.referenceFrameLocalRotZ,
            Metadata.referenceFrameLocalRotW);
        if (Quaternion.Dot(referenceFrameLocalRotation, referenceFrameLocalRotation) < 0.9f)
        {
            Reject("Metadata contains an invalid reference-frame rotation.");
            return;
        }
        referenceFrameLocalRotation.Normalize();
        existing.localRotation = referenceFrameLocalRotation;
        existing.localScale = Vector3.one;
        DatasetRoot = existing;
        AttachSavedMeshIfPresent();
        State = PlaybackState.Ready;

        Debug.Log(LogPrefix + " UUID match found: " + savedUuid);
        Debug.Log(LogPrefix + " Reference anchor UUID match found: " + savedReferenceUuid +
            " label=" + MatchedReferenceAnchor.Label);
        Debug.Log(LogPrefix + " DatasetRoot parented to matched scene anchor: " + MatchedReferenceAnchor.name);
        Debug.Log(LogPrefix + " DatasetRoot local transform: position=" + DatasetRoot.localPosition +
            ", rotation=" + DatasetRoot.localRotation + ", scale=" + DatasetRoot.localScale);
    }

    private void AttachSavedMeshIfPresent()
    {
        if (Metadata == null || string.IsNullOrWhiteSpace(Metadata.meshFile))
        {
            return;
        }

        string meshStem = Path.GetFileNameWithoutExtension(Metadata.meshFile);
        Transform[] transforms = FindObjectsByType<Transform>(FindObjectsInactive.Include);
        foreach (Transform candidate in transforms)
        {
            if (!candidate.gameObject.scene.IsValid() || candidate.GetComponentInParent<MRUKRoom>() != null)
            {
                continue;
            }

            if (candidate.name == meshStem || candidate.name == meshStem + "_generated")
            {
                candidate.SetParent(DatasetRoot, false);
                candidate.name = "SavedRoomMesh";
                candidate.localPosition = Vector3.zero;
                candidate.localRotation = Quaternion.identity;
                candidate.localScale = Vector3.one;
                Debug.Log(LogPrefix + " Saved mesh local transform: position=" + candidate.localPosition +
                    ", rotation=" + candidate.localRotation + ", scale=" + candidate.localScale);
                return;
            }
        }

        Debug.LogWarning(LogPrefix + " No scene object matching saved mesh '" + Metadata.meshFile + "' was found. The UUID-matched trajectory can still load.");
    }

    private void RejectLegacy(string reason)
    {
        Debug.LogError(LogPrefix + " Legacy dataset rejected: " + reason);
        Reject("LEGACY DATASET\nThis dataset must be recollected or migrated.");
        Debug.Log(LogPrefix + " Legacy data rejection confirmed.");
    }

    private void Reject(string reason)
    {
        rejectionMessage = reason;
        State = PlaybackState.Rejected;
        DisableSerializedDatasetObjects();
        Debug.LogError(LogPrefix + " Playback aborted: " + reason);
        StartCoroutine(ShowStatusWhenCameraIsReady());
    }

    private void DisableSerializedDatasetObjects()
    {
        SetVisualizerObjectsActive<M0_RawTrajectory>(false);
        SetVisualizerObjectsActive<RssiFloorHeatmap>(false);
        SetVisualizerObjectsActive<M2HeightBarVisualizer>(false);
        SetVisualizerObjectsActive<M3Collapsed2DVisualizer>(false);
        SetVisualizerObjectsActive<M4VoxelCloudVisualizer>(false);

        Transform[] transforms = FindObjectsByType<Transform>(FindObjectsInactive.Include);
        foreach (Transform candidate in transforms)
        {
            if (candidate.gameObject.scene.IsValid() &&
                (candidate.name == "SavedRoomMesh" || candidate.name == "quest_room_mesh" ||
                 candidate.name == "quest_room_mesh_generated"))
            {
                candidate.gameObject.SetActive(false);
            }
        }
    }

    private static void SetVisualizerObjectsActive<T>(bool active) where T : Component
    {
        T[] visualizers = FindObjectsByType<T>(FindObjectsInactive.Include);
        foreach (T visualizer in visualizers)
        {
            visualizer.gameObject.SetActive(active);
        }
    }

    private IEnumerator ShowStatusWhenCameraIsReady()
    {
        float deadline = Time.unscaledTime + 10f;
        while (Camera.main == null && Time.unscaledTime < deadline)
        {
            yield return null;
        }

        if (Camera.main == null)
        {
            yield break;
        }

        GameObject statusObject = new GameObject("RoomAlignment_StatusDisplay");
        statusObject.transform.SetParent(Camera.main.transform, false);
        statusObject.transform.localPosition = new Vector3(0f, 0f, 1.2f);
        TextMesh text = statusObject.AddComponent<TextMesh>();
        text.anchor = TextAnchor.MiddleCenter;
        text.alignment = TextAlignment.Center;
        text.fontSize = 64;
        text.characterSize = 0.014f;
        text.color = Color.red;
        text.text = rejectionMessage.StartsWith("ROOM MISMATCH", StringComparison.Ordinal)
            ? "ROOM MISMATCH\nThis dataset belongs to a different saved MRUK room."
            : rejectionMessage;
    }

    public static IEnumerator WaitForPlaybackDecision()
    {
        RoomAlignmentManager manager = EnsureInstance();
        while (manager.State == PlaybackState.WaitingForMRUK)
        {
            yield return null;
        }
    }

    public bool AttachVisualization(Transform visualization, string childName)
    {
        if (State != PlaybackState.Ready || DatasetRoot == null || visualization == null)
        {
            return false;
        }

        visualization.SetParent(DatasetRoot, false);
        visualization.name = childName;
        visualization.localPosition = Vector3.zero;
        visualization.localRotation = Quaternion.identity;
        visualization.localScale = Vector3.one;
        return true;
    }

    public void ReportFirstTrajectorySample(Vector3 localPosition)
    {
        if (!FirstTrajectorySampleLocalPosition.HasValue)
        {
            FirstTrajectorySampleLocalPosition = localPosition;
        }
    }

    public static bool TryGetCaptureReferenceAnchor(MRUKRoom room, out MRUKAnchor referenceAnchor)
    {
        referenceAnchor = null;
        if (room == null)
        {
            return false;
        }

        foreach (MRUKAnchor floorAnchor in room.FloorAnchors)
        {
            if (floorAnchor != null && floorAnchor.Anchor.Uuid != Guid.Empty)
            {
                referenceAnchor = floorAnchor;
                return true;
            }
        }

        if (room.GlobalMeshAnchor != null && room.GlobalMeshAnchor.Anchor.Uuid != Guid.Empty)
        {
            referenceAnchor = room.GlobalMeshAnchor;
            return true;
        }

        return false;
    }

    public static Quaternion GetUprightReferenceWorldRotation(MRUKAnchor anchor)
    {
        if (anchor == null)
        {
            return Quaternion.identity;
        }

        bool isFloor = (anchor.Label & MRUKAnchor.SceneLabels.FLOOR) != 0;
        Vector3 forward = Vector3.ProjectOnPlane(
            isFloor ? anchor.transform.up : anchor.transform.forward,
            Vector3.up);
        if (forward.sqrMagnitude < 0.0001f)
        {
            forward = Vector3.ProjectOnPlane(anchor.transform.right, Vector3.up);
        }
        if (forward.sqrMagnitude < 0.0001f)
        {
            forward = Vector3.forward;
        }
        return Quaternion.LookRotation(forward.normalized, Vector3.up);
    }

    public static Quaternion GetUprightReferenceLocalRotation(MRUKAnchor anchor)
    {
        return Quaternion.Inverse(anchor.transform.rotation) * GetUprightReferenceWorldRotation(anchor);
    }

    [ContextMenu("Print Room Alignment Debug")]
    public void PrintDebugState()
    {
        Debug.Log(LogPrefix + " DEBUG saved UUID=" + (Metadata != null ? Metadata.roomUuid : "none"));
        if (MRUK.Instance != null)
        {
            foreach (MRUKRoom room in MRUK.Instance.Rooms)
            {
                Debug.Log(LogPrefix + " DEBUG loaded UUID=" + room.Anchor.Uuid + " room=" + room.name);
            }
        }

        Debug.Log(LogPrefix + " DEBUG matched room=" + (MatchedRoom != null ? MatchedRoom.name : "none"));
        Debug.Log(LogPrefix + " DEBUG matched reference anchor=" +
            (MatchedReferenceAnchor != null ? MatchedReferenceAnchor.name + " UUID=" + MatchedReferenceAnchor.Anchor.Uuid : "none"));
        Debug.Log(LogPrefix + " DEBUG DatasetRoot parent=" + (DatasetRoot != null && DatasetRoot.parent != null ? DatasetRoot.parent.name : "none"));
        if (DatasetRoot != null)
        {
            Debug.Log(LogPrefix + " DEBUG DatasetRoot local transform position=" + DatasetRoot.localPosition +
                " rotation=" + DatasetRoot.localRotation + " scale=" + DatasetRoot.localScale);
            Transform mesh = DatasetRoot.Find("SavedRoomMesh");
            Debug.Log(LogPrefix + " DEBUG mesh local transform=" + (mesh != null
                ? mesh.localPosition + " / " + mesh.localRotation + " / " + mesh.localScale
                : "none"));
            Transform firstPoint = null;
            Transform m0 = DatasetRoot.Find("M0");
            if (m0 != null)
            {
                foreach (Transform child in m0)
                {
                    if (child.name.StartsWith("M0_RSSI_POINT_0_", StringComparison.Ordinal))
                    {
                        firstPoint = child;
                        break;
                    }
                }
            }
            Debug.Log(LogPrefix + " DEBUG first rendered M0 local position=" + (firstPoint != null ? firstPoint.localPosition.ToString() : "none"));
        }
        Debug.Log(LogPrefix + " DEBUG first sample local position=" +
            (FirstTrajectorySampleLocalPosition.HasValue ? FirstTrajectorySampleLocalPosition.Value.ToString() : "none"));
    }
}
