using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

public class RadioLensSionnaClient : MonoBehaviour
{
    public event Action<string> StatusChanged;
    [Header("Sionna RT Server")]
    [SerializeField, Tooltip("LAN URL reachable from Quest. Do not use localhost.")]
    private string serverBaseUrl = "http://192.168.1.20:8000";
    [SerializeField, Min(1f)] private float requestTimeoutSeconds = 60f;
    [SerializeField] private long frequencyHz = 5580000000L;
    [SerializeField, Min(0)] private int maxDepth = 2;
    [SerializeField, Min(1)] private int topK = 5;

    public string ServerBaseUrl => serverBaseUrl;
    public int TopK => Mathf.Max(1, topK);

    public IEnumerator CheckHealth(Action<bool, string> completed)
    {
        if (!TryBuildUrl("/health", out string url, out string error))
        { completed(false, error); yield break; }
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            Configure(request);
            if (!TryBegin(request, out UnityWebRequestAsyncOperation operation, out error))
            { completed(false, error); yield break; }
            yield return operation;
            completed(IsSuccess(request), IsSuccess(request) ? null : DescribeFailure(request));
        }
    }

    public IEnumerator EnsureRoom(RadioLensRoomContext context, Action<bool, string> completed)
    {
        if (context == null || !context.IsReady) { completed(false, "Current MRUK room mesh is not ready."); yield break; }
        string query = "?coordinate_frame=" + UnityWebRequest.EscapeURL(SionnaProtocol.CoordinateFrame) +
            "&mesh_version=" + UnityWebRequest.EscapeURL(context.MeshVersion);
        if (!TryBuildUrl("/v1/rooms/" + UnityWebRequest.EscapeURL(context.RoomId) + query, out string lookupUrl, out string error))
        { completed(false, error); yield break; }
        StatusChanged?.Invoke("Checking room cache...");
        using (UnityWebRequest lookup = UnityWebRequest.Get(lookupUrl))
        {
            Configure(lookup);
            if (!TryBegin(lookup, out UnityWebRequestAsyncOperation operation, out error))
            { completed(false, error); yield break; }
            yield return operation;
            if (IsSuccess(lookup))
            {
                context.IsMeshRegistered = true;
                completed(true, "cached");
                yield break;
            }
            if (lookup.responseCode != 404 && lookup.responseCode != 409)
            {
                completed(false, "Room lookup failed: " + DescribeFailure(lookup));
                yield break;
            }
        }

        StatusChanged?.Invoke("Room not registered. Uploading...");

        SionnaRoomMetadataDto metadata = new SionnaRoomMetadataDto
        {
            room_id = context.RoomId,
            coordinate_frame = SionnaProtocol.CoordinateFrame,
            mesh_version = context.MeshVersion
        };
        List<IMultipartFormSection> sections = new List<IMultipartFormSection>
        {
            new MultipartFormDataSection("metadata", JsonUtility.ToJson(metadata)),
            new MultipartFormFileSection("mesh", context.ObjBytes, "room.obj", "model/obj")
        };
        if (!TryBuildUrl("/v1/rooms/" + UnityWebRequest.EscapeURL(context.RoomId) + "/scene", out string uploadUrl, out error))
        { completed(false, error); yield break; }
        using (UnityWebRequest upload = UnityWebRequest.Post(uploadUrl, sections))
        {
            StatusChanged?.Invoke("Uploading room...");
            upload.method = UnityWebRequest.kHttpVerbPUT;
            Configure(upload);
            if (!TryBegin(upload, out UnityWebRequestAsyncOperation operation, out error))
            { completed(false, error); yield break; }
            yield return operation;
            if (!IsSuccess(upload)) { completed(false, "Room upload failed: " + DescribeFailure(upload)); yield break; }
            context.IsMeshRegistered = true;
            completed(true, "uploaded");
        }
    }

    public IEnumerator EnsureScene(RadioLensRoomContext context, byte[] objBytes, string meshVersion,
        Action<bool, string> completed)
    {
        if (context == null || !context.IsReady || objBytes == null || objBytes.Length == 0 ||
            string.IsNullOrEmpty(meshVersion))
        { completed(false, "Candidate scene is not ready."); yield break; }
        string query = "?coordinate_frame=" + UnityWebRequest.EscapeURL(SionnaProtocol.CoordinateFrame) +
            "&mesh_version=" + UnityWebRequest.EscapeURL(meshVersion);
        if (!TryBuildUrl("/v1/rooms/" + UnityWebRequest.EscapeURL(context.RoomId) + query,
            out string lookupUrl, out string error))
        { completed(false, error); yield break; }
        using (UnityWebRequest lookup = UnityWebRequest.Get(lookupUrl))
        {
            Configure(lookup);
            if (!TryBegin(lookup, out UnityWebRequestAsyncOperation operation, out error))
            { completed(false, error); yield break; }
            yield return operation;
            if (IsSuccess(lookup)) { completed(true, "cached"); yield break; }
            if (lookup.responseCode != 404 && lookup.responseCode != 409)
            { completed(false, "Candidate scene lookup failed: " + DescribeFailure(lookup)); yield break; }
        }

        SionnaRoomMetadataDto metadata = new SionnaRoomMetadataDto
        {
            room_id = context.RoomId,
            coordinate_frame = SionnaProtocol.CoordinateFrame,
            mesh_version = meshVersion
        };
        List<IMultipartFormSection> sections = new List<IMultipartFormSection>
        {
            new MultipartFormDataSection("metadata", JsonUtility.ToJson(metadata)),
            new MultipartFormFileSection("mesh", objBytes, "candidate-room.obj", "model/obj")
        };
        if (!TryBuildUrl("/v1/rooms/" + UnityWebRequest.EscapeURL(context.RoomId) + "/scene",
            out string uploadUrl, out error))
        { completed(false, error); yield break; }
        using (UnityWebRequest upload = UnityWebRequest.Post(uploadUrl, sections))
        {
            upload.method = UnityWebRequest.kHttpVerbPUT;
            Configure(upload);
            if (!TryBegin(upload, out UnityWebRequestAsyncOperation operation, out error))
            { completed(false, error); yield break; }
            yield return operation;
            if (!IsSuccess(upload))
            { completed(false, "Candidate scene upload failed: " + DescribeFailure(upload)); yield break; }
            completed(true, "uploaded");
        }
    }

    public IEnumerator Trace(RadioLensRoomContext context, Vector3 txLocal, Vector3 rxLocal, string requestId,
        Action<bool, string, SionnaTraceResponseDto> completed)
    {
        yield return TraceScene(context, context != null ? context.MeshVersion : null, txLocal, rxLocal,
            requestId, completed);
    }

    public IEnumerator TraceScene(RadioLensRoomContext context, string meshVersion, Vector3 txLocal,
        Vector3 rxLocal, string requestId, Action<bool, string, SionnaTraceResponseDto> completed)
    {
        if (!TryBuildUrl("/v1/trace", out string url, out string error))
        { completed(false, error, null); yield break; }
        SionnaTraceRequestDto dto = new SionnaTraceRequestDto
        {
            request_id = requestId,
            room_id = context.RoomId,
            coordinate_frame = SionnaProtocol.CoordinateFrame,
            localization_id = context.LocalizationId,
            mesh_version = meshVersion,
            tx = new SionnaPositionDto(txLocal),
            rx = new SionnaPositionDto(rxLocal),
            frequency = frequencyHz,
            max_depth = maxDepth,
            top_k = TopK
        };
        byte[] body = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(dto));
        using (UnityWebRequest request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
        {
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            Configure(request);
            if (!TryBegin(request, out UnityWebRequestAsyncOperation operation, out error))
            { completed(false, error, null); yield break; }
            yield return operation;
            if (!IsSuccess(request)) { completed(false, DescribeFailure(request), null); yield break; }
            string json = request.downloadHandler != null ? request.downloadHandler.text : null;
            if (!SionnaResponseParser.TryParse(json, out SionnaTraceResponseDto response, out error))
            { completed(false, "Malformed trace response: " + error, null); yield break; }
            completed(true, null, response);
        }
    }

    private void Configure(UnityWebRequest request)
    {
        request.timeout = Mathf.Max(1, Mathf.CeilToInt(requestTimeoutSeconds));
    }

    private static bool TryBegin(UnityWebRequest request, out UnityWebRequestAsyncOperation operation, out string error)
    {
        try
        {
            operation = request.SendWebRequest();
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            operation = null;
            error = exception.Message;
            Debug.LogError("[SionnaClient] Request could not start: " + exception);
            return false;
        }
    }

    private bool TryBuildUrl(string path, out string url, out string error)
    {
        url = null; error = null;
        string root = serverBaseUrl != null ? serverBaseUrl.Trim().TrimEnd('/') : "";
        if (string.IsNullOrEmpty(root) || !Uri.TryCreate(root, UriKind.Absolute, out Uri parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        { error = "Invalid Sionna server URL."; return false; }
        url = root + path;
        return true;
    }

    private static bool IsSuccess(UnityWebRequest request) => request.result == UnityWebRequest.Result.Success &&
        request.responseCode >= 200 && request.responseCode < 300;

    private static string DescribeFailure(UnityWebRequest request)
    {
        string body = request.downloadHandler != null ? request.downloadHandler.text : null;
        string detail = string.IsNullOrWhiteSpace(body) ? request.error : body;
        if (detail != null && detail.Length > 300) detail = detail.Substring(0, 300);
        return "HTTP " + request.responseCode + " " + detail;
    }
}
