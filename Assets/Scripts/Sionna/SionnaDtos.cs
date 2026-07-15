using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

public static class SionnaProtocol
{
    public const string CoordinateFrame = "mruk_room_local_unity_lh_y_up_m_v1";
}

[Serializable]
public class SionnaPositionDto
{
    public float[] position;

    public SionnaPositionDto(Vector3 value)
    {
        position = new[] { value.x, value.y, value.z };
    }
}

[Serializable]
public class SionnaTraceRequestDto
{
    public string request_id;
    public string room_id;
    public string coordinate_frame;
    public string localization_id;
    public string mesh_version;
    public SionnaPositionDto tx;
    public SionnaPositionDto rx;
    public long frequency;
    public int max_depth;
    public int top_k;
}

[Serializable]
public class SionnaRoomMetadataDto
{
    public string room_id;
    public string coordinate_frame;
    public string mesh_version;
}

public sealed class SionnaPathDto
{
    public Vector3[] vertices;
    public double pathGain;
    public double delaySeconds;
}

public sealed class SionnaTraceResponseDto
{
    public string requestId;
    public string roomId;
    public string coordinateFrame;
    public string localizationId;
    public string meshVersion;
    public readonly List<SionnaPathDto> paths = new List<SionnaPathDto>();
}

// JsonUtility cannot deserialize the server's nested [[x,y,z], ...] vertex arrays.
// This focused parser accepts the documented response without introducing a JSON package.
public static class SionnaResponseParser
{
    private const string NumberPattern = @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?";

    public static bool TryParse(string json, out SionnaTraceResponseDto response, out string error)
    {
        response = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json)) { error = "Response body is empty."; return false; }
        try
        {
            SionnaTraceResponseDto parsed = new SionnaTraceResponseDto
            {
                requestId = ReadString(json, "request_id"),
                roomId = ReadString(json, "room_id"),
                coordinateFrame = ReadString(json, "coordinate_frame"),
                localizationId = ReadString(json, "localization_id"),
                meshVersion = ReadString(json, "mesh_version")
            };
            int pathsKey = json.IndexOf("\"paths\"", StringComparison.Ordinal);
            if (pathsKey < 0) { error = "Response has no paths field."; return false; }
            int arrayStart = json.IndexOf('[', pathsKey);
            if (arrayStart < 0 || !TryFindMatching(json, arrayStart, '[', ']', out int arrayEnd))
            { error = "Response paths array is malformed."; return false; }

            foreach (string pathJson in ExtractObjects(json, arrayStart + 1, arrayEnd))
            {
                int verticesKey = pathJson.IndexOf("\"vertices\"", StringComparison.Ordinal);
                if (verticesKey < 0) continue;
                int verticesStart = pathJson.IndexOf('[', verticesKey);
                if (verticesStart < 0 || !TryFindMatching(pathJson, verticesStart, '[', ']', out int verticesEnd)) continue;
                MatchCollection numbers = Regex.Matches(
                    pathJson.Substring(verticesStart, verticesEnd - verticesStart + 1), NumberPattern,
                    RegexOptions.CultureInvariant);
                if (numbers.Count < 6 || numbers.Count % 3 != 0) continue;
                Vector3[] vertices = new Vector3[numbers.Count / 3];
                for (int i = 0; i < vertices.Length; i++)
                {
                    vertices[i] = new Vector3(
                        ParseFloat(numbers[i * 3].Value),
                        ParseFloat(numbers[i * 3 + 1].Value),
                        ParseFloat(numbers[i * 3 + 2].Value));
                }
                parsed.paths.Add(new SionnaPathDto
                {
                    vertices = vertices,
                    pathGain = ReadNumber(pathJson, "path_gain"),
                    delaySeconds = ReadNumber(pathJson, "delay")
                });
            }
            response = parsed;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static string ReadString(string json, string key)
    {
        Match match = Regex.Match(json, "\\\"" + Regex.Escape(key) + "\\\"\\s*:\\s*\\\"(?<v>(?:\\\\.|[^\\\"])*)\\\"");
        return match.Success ? Regex.Unescape(match.Groups["v"].Value) : null;
    }

    private static double ReadNumber(string json, string key)
    {
        Match match = Regex.Match(json, "\\\"" + Regex.Escape(key) + "\\\"\\s*:\\s*(?<v>" + NumberPattern + ")");
        return match.Success ? double.Parse(match.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture) : 0d;
    }

    private static float ParseFloat(string value) =>
        float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static IEnumerable<string> ExtractObjects(string json, int start, int end)
    {
        int depth = 0;
        int objectStart = -1;
        bool inString = false;
        bool escaped = false;
        for (int i = start; i < end; i++)
        {
            char value = json[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (value == '\\') escaped = true;
                else if (value == '"') inString = false;
                continue;
            }
            if (value == '"') { inString = true; continue; }
            if (value == '{') { if (depth++ == 0) objectStart = i; }
            else if (value == '}' && --depth == 0 && objectStart >= 0)
            { yield return json.Substring(objectStart, i - objectStart + 1); objectStart = -1; }
        }
    }

    private static bool TryFindMatching(string text, int start, char open, char close, out int end)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int i = start; i < text.Length; i++)
        {
            char value = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (value == '\\') escaped = true;
                else if (value == '"') inString = false;
                continue;
            }
            if (value == '"') { inString = true; continue; }
            if (value == open) depth++;
            else if (value == close && --depth == 0) { end = i; return true; }
        }
        end = -1;
        return false;
    }
}
