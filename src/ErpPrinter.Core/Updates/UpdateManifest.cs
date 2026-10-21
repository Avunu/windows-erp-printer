using System.Text.Json;

namespace ErpPrinter.Updates;

/// <summary>
/// The <c>latest.json</c> published next to the MSI:
/// <code>{ "version": "1.4.0", "url": "ErpPrinter-1.4.0-x64.msi", "sha256": "&lt;hex&gt;", "notes": "&lt;url&gt;" }</code>
/// <c>url</c> may be absolute or relative to the manifest (see <see cref="ManifestUrl.ResolvePackage"/>). Fetching the manifest
/// (HTTP, file or UNC) is the updater's job; this type only parses and validates it.
/// </summary>
public sealed record UpdateManifest(string Version, string Url, string Sha256, string? Notes)
{
    private static readonly string[] RequiredFields = ["version", "url", "sha256"];

    /// <summary>Parses and validates manifest JSON. <paramref name="source"/> (the manifest's URL or path) appears in error messages.</summary>
    /// <exception cref="InvalidDataException">The JSON is invalid or a required field (version, url, sha256) is missing or empty.</exception>
    public static UpdateManifest Parse(string json, string source)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException ex) { throw new InvalidDataException($"Update manifest at {source} is not valid JSON: {ex.Message}", ex); }

        using (document)
        {
            var root = document.RootElement;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                    values[property.Name] = property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString() ?? "",
                        JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined => "",
                        _ => property.Value.GetRawText(),
                    };
            }

            foreach (var field in RequiredFields)
            {
                if (!values.TryGetValue(field, out var value) || value.Length == 0)
                    throw new InvalidDataException($"Update manifest at {source} is missing '{field}'.");
            }
            return new UpdateManifest(values["version"], values["url"], values["sha256"], values.GetValueOrDefault("notes") is { Length: > 0 } notes ? notes : null);
        }
    }

    /// <summary>
    /// Reads a manifest from a local file path or a <c>file://</c> URL (the UNC-share and air-gapped case).
    /// HTTP(S) manifests are downloaded by the updater and passed to <see cref="Parse"/>.
    /// </summary>
    public static UpdateManifest ReadFile(string pathOrFileUrl)
    {
        var path = ManifestUrl.ToLocalPath(pathOrFileUrl);
        return Parse(File.ReadAllText(path), pathOrFileUrl);
    }
}
