using System;
using System.Collections;
using System.IO;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class TxSpatialAnchorSaveData
{
    public string txId;
    public string uuid;
}

public class TxSpatialAnchorManager : MonoBehaviour
{
    [Header("Placement")]
    public Transform placementSource;
    public GameObject txMarkerPrefab;
    public float forwardOffsetMeters = 0.25f;

    [Header("Save Data")]
    public string txId = "router_1";
    public string saveFileName = "tx_spatial_anchor.json";
    public bool loadOnStart = true;

    [Header("Keyboard Debug")]
    public KeyCode placeKey = KeyCode.T;
    public KeyCode loadKey = KeyCode.L;
    public KeyCode eraseKey = KeyCode.Backspace;

    private GameObject currentMarker;
    private OVRSpatialAnchor currentAnchor;
    private string savePath;

    void Start()
    {
        TxLineOfSightVisualizer lineOfSightVisualizer = GetComponent<TxLineOfSightVisualizer>();

        if (lineOfSightVisualizer != null && lineOfSightVisualizer.enabled)
        {
            Debug.LogWarning("TxSpatialAnchorManager disabled because TxLineOfSightVisualizer is handling Tx anchor placement.");
            enabled = false;
            return;
        }

        savePath = Path.Combine(Application.persistentDataPath, saveFileName);

        Debug.Log("Tx spatial anchor save path: " + savePath);

        if (placementSource == null)
        {
            Camera cam = Camera.main;
            if (cam != null)
            {
                placementSource = cam.transform;
                Debug.Log("Using Main Camera as placement source.");
            }
            else
            {
                Debug.LogWarning("No placementSource assigned and no Main Camera found.");
            }
        }

        if (loadOnStart)
        {
            StartCoroutine(LoadSavedTxAnchorRoutine());
        }
    }

    void Update()
    {
        bool questPlacePressed = OVRInput.GetDown(OVRInput.Button.One); // A button
        bool questLoadPressed = OVRInput.GetDown(OVRInput.Button.Two);  // B button

        if (Input.GetKeyDown(placeKey) || questPlacePressed)
        {   
            Debug.LogError("PLACE INPUT DETECTED");
            StartCoroutine(CreateAndSaveTxAnchorRoutine());
        }

        if (Input.GetKeyDown(loadKey) || questLoadPressed)
        {
            Debug.LogError("LOAD INPUT DETECTED");
            StartCoroutine(LoadSavedTxAnchorRoutine());
        }

        if (Input.GetKeyDown(eraseKey))
        {   
            Debug.LogError("ERASE INPUT DETECTED");
            StartCoroutine(EraseCurrentAnchorRoutine());
        }
    }

    IEnumerator CreateAndSaveTxAnchorRoutine()
    {
        if (placementSource == null)
        {
            Debug.LogError("Cannot create Tx spatial anchor: placementSource is null.");
            yield break;
        }

        if (currentMarker != null)
        {
            Destroy(currentMarker);
            currentMarker = null;
            currentAnchor = null;
        }

        Vector3 pos = placementSource.position + placementSource.forward * forwardOffsetMeters;
        Quaternion rot = placementSource.rotation;

        Debug.LogError("CREATING TX MARKER AT: " + pos);

        currentMarker = CreateMarkerObject(pos, rot);
        currentMarker.SetActive(true);
        Debug.LogError("TX MARKER CREATED: " + currentMarker.name);
        currentAnchor = currentMarker.GetComponent<OVRSpatialAnchor>();

        if (currentAnchor == null)
        {
            currentAnchor = currentMarker.AddComponent<OVRSpatialAnchor>();
        }

        Debug.Log("Created Tx marker. Waiting for OVRSpatialAnchor creation/localization...");

        float timeout = 10f;
        float timer = 0f;

        while (!currentAnchor.Created && timer < timeout)
        {
            timer += Time.deltaTime;
            yield return null;
        }

        if (!currentAnchor.Created)
        {
            Debug.LogError("OVRSpatialAnchor was not created before timeout.");
            yield break;
        }

        Debug.Log("OVRSpatialAnchor created. UUID: " + currentAnchor.Uuid);

        var saveTask = currentAnchor.SaveAnchorAsync();

        while (!saveTask.IsCompleted)
        {
            yield return null;
        }

        if (!saveTask.GetResult())
        {
            Debug.LogError("Failed to save Tx OVRSpatialAnchor.");
            yield break;
        }

        TxSpatialAnchorSaveData data = new TxSpatialAnchorSaveData
        {
            txId = txId,
            uuid = currentAnchor.Uuid.ToString()
        };

        string json = JsonUtility.ToJson(data, true);
        File.WriteAllText(savePath, json);

        Debug.Log("Saved Tx spatial anchor:\n" + json);
    }

    IEnumerator LoadSavedTxAnchorRoutine()
    {
        if (!File.Exists(savePath))
        {
            Debug.LogWarning("No saved Tx spatial anchor JSON found at: " + savePath);
            yield break;
        }

        string json = File.ReadAllText(savePath);
        TxSpatialAnchorSaveData data = JsonUtility.FromJson<TxSpatialAnchorSaveData>(json);

        if (string.IsNullOrWhiteSpace(data.uuid))
        {
            Debug.LogError("Saved Tx anchor JSON has no UUID.");
            yield break;
        }

        Guid uuid = new Guid(data.uuid);

        Debug.Log("Loading Tx spatial anchor UUID: " + uuid);

        List<Guid> uuids = new List<Guid> { uuid };
        List<OVRSpatialAnchor.UnboundAnchor> unboundAnchors =
        new List<OVRSpatialAnchor.UnboundAnchor>();

        var loadTask = OVRSpatialAnchor.LoadUnboundAnchorsAsync(uuids, unboundAnchors);

        while (!loadTask.IsCompleted)
        {
            yield return null;
        }

        if (!loadTask.GetResult())
        {
            Debug.LogError("Failed to load unbound Tx spatial anchor.");
            yield break;
        }

        if (unboundAnchors.Count == 0)
        {
            Debug.LogError("Loaded zero unbound anchors for UUID: " + uuid);
            yield break;
        }

        OVRSpatialAnchor.UnboundAnchor unboundAnchor = unboundAnchors[0];

        if (!unboundAnchor.Localized)
        {
            Debug.Log("Localizing Tx anchor...");

            var localizeTask = unboundAnchor.LocalizeAsync();

            while (!localizeTask.IsCompleted)
            {
                yield return null;
            }

            if (!localizeTask.GetResult())
            {
                Debug.LogError("Failed to localize Tx spatial anchor.");
                yield break;
            }
        }

        if (!unboundAnchor.TryGetPose(out Pose pose))
        {
            Debug.LogError("Failed to get pose for Tx spatial anchor.");
            yield break;
        }


        currentMarker = CreateMarkerObject(pose.position, pose.rotation);
        currentAnchor = currentMarker.GetComponent<OVRSpatialAnchor>();

        if (currentAnchor == null)
        {
            currentAnchor = currentMarker.AddComponent<OVRSpatialAnchor>();
        }

        unboundAnchor.BindTo(currentAnchor);

        Debug.Log("Loaded and bound Tx spatial anchor at: " + pose.position);
    }

    IEnumerator EraseCurrentAnchorRoutine()
    {
        if (currentAnchor != null && currentAnchor.Created)
        {
            var eraseTask = currentAnchor.EraseAnchorAsync();

            while (!eraseTask.IsCompleted)
            {
                yield return null;
            }

            if (eraseTask.GetResult())
            {
                Debug.Log("Erased Tx spatial anchor from device.");
            }
            else
            {
                Debug.LogWarning("Failed to erase Tx spatial anchor from device.");
            }
        }

        if (currentMarker != null)
        {
            Destroy(currentMarker);
            currentMarker = null;
            currentAnchor = null;
        }

        if (File.Exists(savePath))
        {
            File.Delete(savePath);
            Debug.Log("Deleted Tx spatial anchor JSON: " + savePath);
        }

        yield return null;
    }

    GameObject CreateMarkerObject(Vector3 pos, Quaternion rot)
    {
        GameObject marker;

        if (txMarkerPrefab != null)
        {
            marker = Instantiate(txMarkerPrefab, pos, rot);
            marker.SetActive(true);
        }
        else
        {
            marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.transform.position = pos;
            marker.transform.rotation = rot;
            marker.transform.localScale = Vector3.one * 0.3f;

            Renderer renderer = marker.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = Color.magenta;
            }
        }

        marker.name = "TX_SPATIAL_ANCHOR_" + txId;
        return marker;
    }
} 
