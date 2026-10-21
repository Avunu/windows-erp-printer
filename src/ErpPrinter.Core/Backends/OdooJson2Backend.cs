using System.Text.Json.Nodes;
using ErpPrinter.Http;
using ErpPrinter.Secrets;
using ErpPrinter.Settings;

namespace ErpPrinter.Backends;

/// <summary>Odoo 19+ external JSON-2 API: POST /json/2/&lt;model&gt;/&lt;method&gt; with a bearer API key.</summary>
public sealed class OdooJson2Backend(ErpHttp http, OdooCommon common) : IBackend
{
    public string Name => "OdooJson2";

    private static Dictionary<string, string> Headers(PrinterProfile profile, BackendSecrets secrets)
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = $"bearer {secrets.ApiKey}" };
        if (profile.Database.Length > 0) headers["X-Odoo-Database"] = profile.Database;
        return headers;
    }

    private async Task<JsonNode?> InvokeAsync(
        PrinterProfile profile, GlobalSettings global, BackendSecrets secrets,
        string model, string method, JsonObject body, string? payloadPath, CancellationToken cancellationToken)
    {
        var uri = ErpHttp.JoinUrl(profile.ServerUrl, $"json/2/{model}/{method}");
        HttpContent? content = payloadPath is null ? null : new Base64JsonContent(body, payloadPath);
        HttpResult response;
        try
        {
            response = await http.SendAsync(HttpMethod.Post, uri, global, Headers(profile, secrets),
                jsonBody: content is null ? ErpJson.Serialize(body) : null, content: content, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally { content?.Dispose(); }

        if (response is { StatusCode: 404, Json: null })
            throw new ErpException($"The server has no JSON-2 API at {profile.ServerUrl} (HTTP 404). For Odoo 18 and older choose the OdooJsonRpc backend.");
        ErpHttp.AssertSuccess(response, $"Odoo {model}.{method}");
        return response.Json;
    }

    public async Task<string> SendAsync(PrinterProfile profile, GlobalSettings global, DocumentToSend document, BackendSecrets secrets, CancellationToken cancellationToken)
    {
        var ownerId = await common.ResolveOwnerIdAsync(profile, document.Meta, async domain =>
        {
            var users = await InvokeAsync(profile, global, secrets, "res.users", "search_read",
                new JsonObject { ["domain"] = domain, ["fields"] = new JsonArray("id"), ["limit"] = 1 }, null, cancellationToken).ConfigureAwait(false);
            var id = ErpJson.Get(ErpJson.First(users), "id");
            return id is null ? null : Convert.ToInt32(ErpJson.Text(id), System.Globalization.CultureInfo.InvariantCulture);
        }, cancellationToken).ConfigureAwait(false);

        var vals = common.BuildVals(profile, document.Meta, document.FileName, ownerId);
        var result = await InvokeAsync(profile, global, secrets, profile.OdooModel, "create",
            new JsonObject { ["vals_list"] = new JsonArray(vals) }, document.PdfPath, cancellationToken).ConfigureAwait(false);
        return ErpJson.Text(ErpJson.First(result));
    }

    public async Task<string> TestConnectionAsync(PrinterProfile profile, GlobalSettings global, BackendSecrets secrets, CancellationToken cancellationToken)
    {
        var count = await InvokeAsync(profile, global, secrets, profile.OdooModel, "search_count",
            new JsonObject { ["domain"] = new JsonArray() }, null, cancellationToken).ConfigureAwait(false);
        var fieldCheck = await OdooCommon.FieldCheckMessageAsync(profile, (model, names) =>
            InvokeAsync(profile, global, secrets, model, "fields_get",
                new JsonObject
                {
                    ["allfields"] = new JsonArray(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
                    ["attributes"] = new JsonArray("type"),
                }, null, cancellationToken)).ConfigureAwait(false);
        return $"Connected to Odoo (JSON-2). The API user can see {ErpJson.Text(count)} {profile.OdooModel} records." + fieldCheck;
    }
}
