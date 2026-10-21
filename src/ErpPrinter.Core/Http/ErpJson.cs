using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ErpPrinter.Http;

/// <summary>Small JSON helpers shared by the transport and the backends.</summary>
public static class ErpJson
{
    /// <summary>Compact output that keeps non-ASCII text readable (what PowerShell 7's <c>ConvertTo-Json -Compress</c> produces).</summary>
    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(JsonNode? node) => node is null ? "null" : node.ToJsonString(Compact);

    /// <summary>Parses text that may be JSON; null when it is not.</summary>
    public static JsonNode? TryParse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The string form of a JSON value the way PowerShell's <c>[string]$value</c> shows it: strings as they are,
    /// numbers and booleans as their JSON text, null as the empty string.
    /// </summary>
    public static string Text(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(Compact),
    };

    /// <summary>The first element of an array result, or the value itself when it is not an array (<c>@($x) | Select-Object -First 1</c>).</summary>
    public static JsonNode? First(JsonNode? node) => node is JsonArray array ? (array.Count > 0 ? array[0] : null) : node;

    /// <summary>A property of an object, or null when the node is not an object or has no such property.</summary>
    public static JsonNode? Get(JsonNode? node, string name) => node is JsonObject o && o.TryGetPropertyValue(name, out var v) ? v : null;

    /// <summary>PowerShell truthiness of a parsed JSON value: null, false, 0 and the empty string are false.</summary>
    public static bool IsTruthy(JsonNode? node)
    {
        switch (node)
        {
            case null: return false;
            case JsonValue v:
                if (v.TryGetValue<bool>(out var b)) return b;
                if (v.TryGetValue<string>(out var s)) return s.Length > 0;
                if (v.TryGetValue<double>(out var d)) return d != 0;
                return true;
            case JsonArray a: return a.Count > 0;
            default: return true;
        }
    }
}
