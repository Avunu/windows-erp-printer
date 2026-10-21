using ErpPrinter.Http;
using ErpPrinter.Secrets;
using ErpPrinter.Settings;
using ErpPrinter.Templating;

namespace ErpPrinter.Backends;

/// <summary>ERPNext / Frappe: multipart POST to /api/method/upload_file with token auth.</summary>
public sealed class ERPNextBackend(ErpHttp http, TimeProvider? time = null) : IBackend
{
    public string Name => "ERPNext";

    private static Dictionary<string, string> Headers(BackendSecrets secrets) => new()
    {
        ["Authorization"] = $"token {secrets.ApiKey}:{secrets.ApiSecret}",
        ["Accept"] = "application/json",
    };

    public async Task<string> SendAsync(PrinterProfile profile, GlobalSettings global, DocumentToSend document, BackendSecrets secrets, CancellationToken cancellationToken)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("is_private", profile.ERPNextIsPrivate ? "1" : "0"),
            new("folder", profile.ERPNextFolder.Length > 0 ? profile.ERPNextFolder : "Home"),
            new("file_name", document.FileName),
        };
        if (profile.ERPNextDoctype.Length > 0 && profile.ERPNextDocname.Length > 0)
        {
            fields.Add(new("doctype", profile.ERPNextDoctype));
            fields.Add(new("docname", Template.Expand(profile.ERPNextDocname, TemplateTokens.Create(document.Meta, time))));
        }

        using var content = MultipartContents.Create(fields, "file", document.PdfPath, document.FileName);
        var response = await http.SendAsync(HttpMethod.Post, ErpHttp.JoinUrl(profile.ServerUrl, "api/method/upload_file"), global,
            Headers(secrets), content: content, cancellationToken: cancellationToken).ConfigureAwait(false);
        ErpHttp.AssertSuccess(response, "ERPNext upload_file");

        var message = ErpJson.Get(response.Json, "message");
        return ErpJson.IsTruthy(message) ? ErpJson.Text(ErpJson.Get(message, "name")) : "";
    }

    public async Task<string> TestConnectionAsync(PrinterProfile profile, GlobalSettings global, BackendSecrets secrets, CancellationToken cancellationToken)
    {
        var response = await http.SendAsync(HttpMethod.Get, ErpHttp.JoinUrl(profile.ServerUrl, "api/method/frappe.auth.get_logged_user"), global,
            Headers(secrets), cancellationToken: cancellationToken).ConfigureAwait(false);
        ErpHttp.AssertSuccess(response, "ERPNext login check");
        return $"Connected to ERPNext as {ErpJson.Text(ErpJson.Get(response.Json, "message"))}.";
    }
}
