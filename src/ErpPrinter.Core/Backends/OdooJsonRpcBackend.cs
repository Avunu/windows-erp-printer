using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ErpPrinter.Http;
using ErpPrinter.Secrets;
using ErpPrinter.Settings;

namespace ErpPrinter.Backends;

/// <summary>
/// Odoo 14-18 external API over /jsonrpc (execute_kw). Odoo is phasing this out in favour of JSON-2; use the
/// OdooJson2 backend on Odoo 19 and later.
/// </summary>
public sealed partial class OdooJsonRpcBackend(ErpHttp http, OdooCommon common) : IBackend
{
    private sealed record Session(string Database, int Uid);

    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public string Name => "OdooJsonRpc";

    // Errors about the request itself will not go away by retrying.
    [GeneratedRegex("(ValidationError|UserError|ValueError|MissingError|KeyError|TypeError)$", RegexOptions.IgnoreCase)]
    private static partial Regex PermanentErrorName();

    private async Task<JsonNode?> CallAsync(
        PrinterProfile profile, GlobalSettings global, string service, string method,
        JsonArray args, string? payloadPath, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "call",
            ["id"] = Random.Shared.Next(1000000),
            ["params"] = new JsonObject { ["service"] = service, ["method"] = method, ["args"] = args },
        };
        HttpContent? content = payloadPath is null ? null : new Base64JsonContent(body, payloadPath);
        HttpResult response;
        try
        {
            response = await http.SendAsync(HttpMethod.Post, ErpHttp.JoinUrl(profile.ServerUrl, "jsonrpc"), global,
                jsonBody: content is null ? ErpJson.Serialize(body) : null, content: content, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally { content?.Dispose(); }

        ErpHttp.AssertSuccess(response, $"Odoo {service}.{method}");
        if (response.Json is null) throw new ErpException($"Odoo returned a non-JSON response to {service}.{method}.");

        var error = ErpJson.Get(response.Json, "error");
        if (ErpJson.IsTruthy(error))
        {
            var name = "";
            var message = ErpJson.Text(ErpJson.Get(error, "message"));
            var data = ErpJson.Get(error, "data");
            if (ErpJson.IsTruthy(data))
            {
                name = ErpJson.Text(ErpJson.Get(data, "name"));
                var dataMessage = ErpJson.Get(data, "message");
                if (ErpJson.IsTruthy(dataMessage)) message = ErpJson.Text(dataMessage);
            }
            var permanent = PermanentErrorName().IsMatch(name);
            throw new ErpException($"Odoo {service}.{method} failed: {message}{(name.Length > 0 ? $" ({name})" : "")}", permanent);
        }
        return ErpJson.Get(response.Json, "result");
    }

    /// <summary>Resolves (and caches) the database name and uid for the profile's login + API key.</summary>
    private async Task<Session> GetSessionAsync(PrinterProfile profile, GlobalSettings global, BackendSecrets secrets, bool refresh, CancellationToken cancellationToken)
    {
        var cacheKey = $"{profile.ServerUrl}|{profile.Database}|{profile.Username}";
        if (!refresh && _sessions.TryGetValue(cacheKey, out var cached)) return cached;

        var database = profile.Database;
        if (database.Length == 0)
        {
            var listed = await CallAsync(profile, global, "db", "list", new JsonArray(), null, cancellationToken).ConfigureAwait(false);
            var count = listed is JsonArray databases ? databases.Count : listed is null ? 0 : 1;
            if (count != 1) throw new ErpException($"Set the Database setting; the server lists {count} databases.");
            database = ErpJson.Text(ErpJson.First(listed));
        }

        var uid = await CallAsync(profile, global, "common", "authenticate",
            new JsonArray(database, profile.Username, secrets.ApiKey, new JsonObject()), null, cancellationToken).ConfigureAwait(false);
        if (!ErpJson.IsTruthy(uid))
            throw new ErpException($"Odoo rejected login '{profile.Username}' with the stored API key on database '{database}'.");

        var session = new Session(database, Convert.ToInt32(ErpJson.Text(uid), CultureInfo.InvariantCulture));
        _sessions[cacheKey] = session;
        return session;
    }

    private async Task<JsonNode?> ExecuteKwAsync(
        PrinterProfile profile, GlobalSettings global, BackendSecrets secrets,
        string model, string method, JsonArray positional, JsonObject keyword, string? payloadPath, CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(profile, global, secrets, refresh: false, cancellationToken).ConfigureAwait(false);
        var args = new JsonArray(session.Database, session.Uid, secrets.ApiKey, model, method, positional, keyword);
        try
        {
            return await CallAsync(profile, global, "object", "execute_kw", args, payloadPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Re-authenticate next time in case the key was rotated or the user changed.
            _sessions.Clear();
            throw;
        }
    }

    public async Task<string> SendAsync(PrinterProfile profile, GlobalSettings global, DocumentToSend document, BackendSecrets secrets, CancellationToken cancellationToken)
    {
        var ownerId = await common.ResolveOwnerIdAsync(profile, document.Meta, async domain =>
        {
            var users = await ExecuteKwAsync(profile, global, secrets, "res.users", "search_read",
                new JsonArray(domain), new JsonObject { ["fields"] = new JsonArray("id"), ["limit"] = 1 }, null, cancellationToken).ConfigureAwait(false);
            var id = ErpJson.Get(ErpJson.First(users), "id");
            return id is null ? null : Convert.ToInt32(ErpJson.Text(id), CultureInfo.InvariantCulture);
        }, cancellationToken).ConfigureAwait(false);

        var vals = common.BuildVals(profile, document.Meta, document.FileName, ownerId);
        var result = await ExecuteKwAsync(profile, global, secrets, profile.OdooModel, "create",
            new JsonArray(vals), new JsonObject(), document.PdfPath, cancellationToken).ConfigureAwait(false);
        return ErpJson.Text(ErpJson.First(result));
    }

    public async Task<string> TestConnectionAsync(PrinterProfile profile, GlobalSettings global, BackendSecrets secrets, CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(profile, global, secrets, refresh: true, cancellationToken).ConfigureAwait(false);
        var count = await ExecuteKwAsync(profile, global, secrets, profile.OdooModel, "search_count",
            new JsonArray(new JsonArray()), new JsonObject(), null, cancellationToken).ConfigureAwait(false);
        var fieldCheck = await OdooCommon.FieldCheckMessageAsync(profile, (model, names) =>
            ExecuteKwAsync(profile, global, secrets, model, "fields_get",
                new JsonArray(new JsonArray(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray())),
                new JsonObject { ["attributes"] = new JsonArray("type") }, null, cancellationToken)).ConfigureAwait(false);
        return $"Connected to Odoo database '{session.Database}' as uid {session.Uid}. The API user can see {ErpJson.Text(count)} {profile.OdooModel} records." + fieldCheck;
    }
}
