using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ErpPrinter.Templating;

/// <summary>Placeholder expansion and title cleaning. Port of Expand-ErpTemplate, Get-ErpCleanTitle and ConvertFrom-ErpJsonObject.</summary>
public static partial class Template
{
    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"^Microsoft (Word|Excel|PowerPoint|Outlook) - ", RegexOptions.IgnoreCase)]
    private static partial Regex ApplicationPrefix();

    [GeneratedRegex(@"\.(docx?|docm|xlsx?|xlsm|pptx?|txt|rtf|odt|ods|odp|pdf|html?|xps|oxps|png|jpe?g|tiff?|msg|eml)$", RegexOptions.IgnoreCase)]
    private static partial Regex SourceExtension();

    /// <summary>Strips application prefixes and source-file extensions from spooler document names; "Untitled" when nothing is left.</summary>
    public static string CleanTitle(string? title)
    {
        var t = (title ?? "").Trim();
        t = ApplicationPrefix().Replace(t, "");
        t = SourceExtension().Replace(t, "");
        return t.Length == 0 ? "Untitled" : t;
    }

    /// <summary>
    /// Replaces <c>{token}</c> placeholders. Token names are case-insensitive (the keys of <paramref name="tokens"/>
    /// are lower case); unknown tokens are left as they are.
    /// </summary>
    /// <param name="jsonEscape">Escape values for embedding inside a JSON string literal.</param>
    public static string Expand(string template, IReadOnlyDictionary<string, string> tokens, bool jsonEscape = false) =>
        TokenPattern().Replace(template, m =>
        {
            var key = m.Groups[1].Value.ToLowerInvariant();
            if (!tokens.TryGetValue(key, out var value)) return m.Value;
            return jsonEscape ? JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString() : value;
        });

    /// <summary>Parses a JSON object. Throws <see cref="FormatException"/> with a short reason for arrays, scalars and malformed text.</summary>
    public static JsonObject ParseJsonObject(string text)
    {
        if (!text.TrimStart().StartsWith('{')) throw new FormatException("the text does not start with {.");
        try
        {
            return JsonNode.Parse(text) as JsonObject ?? throw new FormatException("the text is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new FormatException(ex.Message, ex);
        }
    }
}
