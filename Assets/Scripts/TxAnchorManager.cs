using System;
using System.IO;
using UnityEngine;

[Serializable]
public class TxAnchorData
{
    public string txId;
    public float posX;
    public float posY;
    public float posZ;
    public float rotX;
    public float rotY;
    public float rotZ;
    public float rotW;
}

public class TxAnchorManager : MonoBehaviour
{
    [Header("References")]
    public Transform placementSource;
    public GameObject txMarkerPrefab;

    [Header("Settings")]
    public string fileName = "tx_anchor.json";
    public string txId = "router_1";
    public bool loadOnStart = true;

    [Header("Keyboard Debug")]
    public KeyCode placeKey = KeyCode.T;
    public KeyCode loadKey = KeyCode.L;
    public KeyCode deleteKey = KeyCode.Backspace;

    private GameObject currentMarker;
    private string filePath;

    void Start()
    {
        filePath = Path.Combine(Application.persistentDataPath, fileName);

        Debug.Log("Tx anchor path: " + filePath);

        if (placementSource == null)
        {
            Camera cam = Camera.main;

            if (cam != null)
            {
                placementSource = cam.transform;
                Debug.Log("TxAnchorManager using Main Camera as placement source.");
            }
            else
            {
                Debug.LogWarning("No placementSource assigned and no Main Camera found.");
            }
        }

        if (loadOnStart)
        {
            LoadTxAnchor();
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(placeKey))
        {
            PlaceTxAtSource();
        }

        if (Input.GetKeyDown(loadKey))
        {
            LoadTxAnchor();
        }

        if (Input.GetKeyDown(deleteKey))
        {
            DeleteTxAnchor();
        }
    }

    public void PlaceTxAtSource()
    {
        if (placementSource == null)
        {
            Debug.LogError("Cannot place Tx anchor: placementSource is null.");
            return;
        }

        Vector3 pos = placementSource.position + placementSource.forward * 1.5f;
        Quaternion rot = placementSource.rotation;

        CreateOrMoveMarker(pos, rot);
        SaveTxAnchor(pos, rot);

        Debug.Log($"Placed Tx anchor at {pos}");
    }

    void CreateOrMoveMarker(Vector3 pos, Quaternion rot)
    {
        if (currentMarker == null)
        {
            if (txMarkerPrefab != null)
            {
                currentMarker = Instantiate(txMarkerPrefab);
                currentMarker.name = "TX_MARKER_" + txId;
                currentMarker.SetActive(true);
            }
            else
            {
                currentMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                currentMarker.name = "TX_MARKER_" + txId;
                currentMarker.transform.localScale = Vector3.one * 0.3f;

                Renderer renderer = currentMarker.GetComponent<Renderer>();
                if (renderer != null)
                {
                    renderer.material.color = Color.magenta;
                }
            }
        }

        currentMarker.transform.position = pos;
        currentMarker.transform.rotation = rot;
    }

    void SaveTxAnchor(Vector3 pos, Quaternion rot)
    {
        TxAnchorData data = new TxAnchorData
        {
            txId = txId,
            posX = pos.x,
            posY = pos.y,
            posZ = pos.z,
            rotX = rot.x,
            rotY = rot.y,
            rotZ = rot.z,
            rotW = rot.w
        };

        string json = JsonUtility.ToJson(data, true);
        File.WriteAllText(filePath, json);

        Debug.Log("Saved Tx anchor JSON:\n" + json);
    }

    public void LoadTxAnchor()
    {
        if (!File.Exists(filePath))
        {
            Debug.LogWarning("No saved Tx anchor found at: " + filePath);
            return;
        }

        string json = File.ReadAllText(filePath);
        TxAnchorData data = JsonUtility.FromJson<TxAnchorData>(json);

        Vector3 pos = new Vector3(data.posX, data.posY, data.posZ);
        Quaternion rot = new Quaternion(data.rotX, data.rotY, data.rotZ, data.rotW);

        CreateOrMoveMarker(pos, rot);

        Debug.Log($"Loaded Tx anchor {data.txId} at {pos}");
    }

    public void DeleteTxAnchor()
    {
        if (currentMarker != null)
        {
            Destroy(currentMarker);
            currentMarker = null;
        }

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            Debug.Log("Deleted saved Tx anchor: " + filePath);
        }
    }

    public Vector3 GetTxPosition()
    {
        if (currentMarker != null)
        {
            return currentMarker.transform.position;
        }

        return Vector3.zero;
    }

    public bool HasTxMarker()
    {
        return currentMarker != null;
    }
}