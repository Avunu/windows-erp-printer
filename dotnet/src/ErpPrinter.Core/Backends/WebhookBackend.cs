using System.Globalization;
using System.Text.Json.Nodes;
using ErpPrinter.Http;
using ErpPrinter.Outbox;
using ErpPrinter.Secrets;
using ErpPrinter.Settings;

namespace ErpPrinter.Backends;

/// <summary>
/// Generic webhook: POST the PDF plus metadata to any URL (n8n, Power Automate, Paperless-ngx, a custom endpoint...).
/// Retries reuse the document id, sent as Idempotency-Key, so receivers can de-duplicate.
/// </summary>
public sealed class WebhookBackend(ErpHttp http) : IBackend
{
    public string Name => "Webhook";

    private static Dictionary<string, string> Headers(PrinterProfile profile, BackendSecrets secrets, string eventName, string? id = null)
    {
        var headers = new Dictionary<string, string> { ["X-ErpPrinter-Event"] = eventName };
        if (!string.IsNullOrEmpty(id)) headers["Idempotency-Key"] = id;
        if (secrets.ApiKey.Length > 0 && profile.WebhookAuthHeader.Length > 0) headers[profile.WebhookAuthHeader] = secrets.ApiKey;
        return headers;
    }

    private static JsonObject Metadata(OutboxMeta meta, string fileName) => new()
    {
        ["id"] = meta.Id,
        ["fileName"] = fileName,
        ["title"] = meta.Title,
        ["user"] = meta.User,
        ["computer"] = meta.Computer ?? "",
        ["printed"] = meta.Printed?.ToString("o", CultureInfo.InvariantCulture) ?? "",
        ["jobId"] = meta.JobId,
        ["pages"] = meta.Pages,
        ["profile"] = meta.Profile,
    };

    public async Task<string> SendAsync(PrinterProfile profile, GlobalSettings global, DocumentToSend document, BackendSecrets secrets, CancellationToken cancellationToken)
    {
        var headers = Headers(profile, secrets, "document", document.Meta.Id);
        HttpResult response;
        if (profile.WebhookFormat == "Json")
        {
            var body = new JsonObject
            {
                ["contentType"] = "application/pdf",
                ["contentBase64"] = Base64JsonContent.Placeholder,
                ["metadata"] = Metadata(document.Meta, document.FileName),
            };
            using var content = new Base64JsonContent(body, document.PdfPath);
            response = await http.SendAsync(HttpMethod.Post, profile.ServerUrl, global, headers, content: content, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var metadata = Metadata(document.Meta, document.FileName);
            var fields = metadata.Select(p => new KeyValuePair<string, string>(p.Key, ErpJson.Text(p.Value))).ToList();
            fields.Add(new("metadata", ErpJson.Serialize(metadata)));
            var fileField = profile.WebhookFileField.Length > 0 ? profile.WebhookFileField : "file";
            using var content = MultipartContents.Create(fields, fileField, document.PdfPath, document.FileName);
            response = await http.SendAsync(HttpMethod.Post, profile.ServerUrl, global, headers, content: content, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        ErpHttp.AssertSuccess(response, "Webhook");

        return response.Json is JsonObject o && o.ContainsKey("id") ? ErpJson.Text(o["id"]) : $"HTTP {response.StatusCode}";
    }

    public async Task<string> TestConnectionAsync(PrinterProfile profile, GlobalSettings global, BackendSecrets secrets, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["event"] = "test", ["computer"] = Environment.MachineName, ["profile"] = profile.Name };
        var response = await http.SendAsync(HttpMethod.Post, profile.ServerUrl, global, Headers(profile, secrets, "test"),
            jsonBody: ErpJson.Serialize(body), cancellationToken: cancellationToken).ConfigureAwait(false);
        ErpHttp.AssertSuccess(response, "Webhook test");
        return $"Webhook answered HTTP {response.StatusCode}.";
    }
}
