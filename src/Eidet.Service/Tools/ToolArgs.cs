using System.Text.Json;

namespace Eidet.Service.Tools;

/// <summary>
/// JsonElement-based argument lifters shared by all handlers. Mirrors the helpers that used to
/// live private inside McpServer so MCP and REST callers see identical argument semantics.
/// </summary>
public static class ToolArgs
{
    public static string? GetString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static int GetInt(JsonElement args, string name, int defaultValue = 0) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : defaultValue;

    public static float GetFloat(JsonElement args, string name, float defaultValue = 0f) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.Number
            ? v.GetSingle()
            : defaultValue;

    public static float? GetFloatOrNull(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.Number
            ? v.GetSingle()
            : null;

    public static bool GetBool(JsonElement args, string name, bool defaultValue = false) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var v) &&
        (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : defaultValue;

    public static bool RequireBool(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty(name, out var v) ||
            (v.ValueKind != JsonValueKind.True && v.ValueKind != JsonValueKind.False))
            throw new MissingToolArgumentException(name);
        return v.GetBoolean();
    }

    public static string RequireString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty(name, out var v) ||
            v.ValueKind != JsonValueKind.String)
            throw new MissingToolArgumentException(name);
        var s = v.GetString();
        if (string.IsNullOrEmpty(s))
            throw new MissingToolArgumentException(name);
        return s;
    }

    public static List<string> GetStringArray(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty(name, out var v) ||
            v.ValueKind != JsonValueKind.Array) return [];
        return v.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();
    }

    public static T? GetEnum<T>(JsonElement args, string name) where T : struct, Enum
    {
        var s = GetString(args, name);
        return s != null && Enum.TryParse<T>(s, true, out var v) ? v : null;
    }
}
