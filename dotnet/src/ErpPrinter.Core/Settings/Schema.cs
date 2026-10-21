namespace ErpPrinter.Settings;

/// <summary>The settings schema. Port of Private/Schema.ps1; names, defaults and ranges are unchanged so existing registry data keeps working.</summary>
public static class Schema
{
    public static readonly IReadOnlyList<string> BackendNames = ["OdooJson2", "OdooJsonRpc", "ERPNext", "Webhook"];
    public static readonly IReadOnlyList<string> OdooBackends = ["OdooJson2", "OdooJsonRpc"];

    public static IReadOnlyList<SettingDefinition> Global { get; } = BuildGlobal();
    public static IReadOnlyList<SettingDefinition> Profile { get; } = BuildProfile();

    public static IReadOnlyList<SettingDefinition> For(SettingScope scope) => scope == SettingScope.Global ? Global : Profile;

    public static SettingDefinition Find(SettingScope scope, string name) =>
        For(scope).FirstOrDefault(s => s.Name == name)
        ?? throw new ArgumentException($"Unknown {scope.ToString().ToLowerInvariant()} setting '{name}'.", nameof(name));

    private static IReadOnlyList<SettingDefinition> BuildGlobal() =>
    [
        new()
        {
            Name = "DataRoot", Type = SettingType.String, Group = "Storage", Label = "Data folder",
            DefaultFactory = _ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ErpPrinter"),
            DefaultText = "%ProgramData%\\ErpPrinter",
            Help = "Outbox, failed/sent archives, logs and downloaded updates are stored here. Locked to SYSTEM and Administrators.",
        },
        new()
        {
            Name = "PollIntervalSeconds", Type = SettingType.Int, Group = "Delivery", Label = "Poll interval (seconds)", Default = 15, Min = 2, Max = 3600,
            Help = "How often the uploader re-scans the outbox. New print jobs wake it immediately.",
        },
        new()
        {
            Name = "MaxAttempts", Type = SettingType.Int, Group = "Delivery", Label = "Max delivery attempts", Default = 0, Min = 0, Max = 100000,
            Help = "After this many failed attempts a document is moved to the failed folder. 0 retries forever.",
        },
        new()
        {
            Name = "RetryMaxBackoffMinutes", Type = SettingType.Int, Group = "Delivery", Label = "Max retry backoff (minutes)", Default = 60, Min = 1, Max = 1440,
            Help = "Retries back off exponentially from 30 seconds up to this ceiling.",
        },
        new()
        {
            Name = "MaxDocumentSizeMB", Type = SettingType.Int, Group = "Delivery", Label = "Max document size (MB)", Default = 64, Min = 1, Max = 2048,
            Help = "Larger documents are moved to the failed folder instead of being uploaded.",
        },
        new() { Name = "HttpTimeoutSeconds", Type = SettingType.Int, Group = "Delivery", Label = "HTTP timeout (seconds)", Default = 120, Min = 5, Max = 3600 },
        new()
        {
            Name = "ProxyUrl", Type = SettingType.String, Group = "Delivery", Label = "Proxy URL", Default = "",
            Help = "Optional, e.g. http://proxy:8080. The service runs as SYSTEM and does not see per-user proxy settings.",
        },
        new()
        {
            Name = "KeepSentDays", Type = SettingType.Int, Group = "Storage", Label = "Keep sent documents (days)", Default = 0, Min = 0, Max = 3650,
            Help = "0 deletes documents as soon as they are delivered.",
        },
        new()
        {
            Name = "LogLevel", Type = SettingType.Choice, Group = "Storage", Label = "Log level", Default = "Information",
            Choices = ["Error", "Warning", "Information", "Verbose"],
        },
        new() { Name = "LogRetentionDays", Type = SettingType.Int, Group = "Storage", Label = "Keep logs (days)", Default = 14, Min = 1, Max = 3650 },
        new() { Name = "AutoUpdate", Type = SettingType.Bool, Group = "Updates", Label = "Install updates automatically", Default = true },
        new()
        {
            Name = "UpdateManifestUrl", Type = SettingType.String, Group = "Updates", Label = "Update manifest URL",
            DefaultFactory = _ => BuildInfo.UpdateManifestUrl,
            DefaultText = "latest.json of the latest release of this project",
            Help = "HTTPS URL or UNC path of latest.json. Defaults to the latest GitHub release of this project.",
        },
        new() { Name = "RequireSignedUpdates", Type = SettingType.Bool, Group = "Updates", Label = "Require Authenticode-signed updates", Default = false },
        new()
        {
            Name = "UpdateSignerThumbprint", Type = SettingType.String, Group = "Updates", Label = "Update signer thumbprint", Default = "",
            Help = "If set, update packages must be signed by this certificate.",
        },
    ];

    private static IReadOnlyList<SettingDefinition> BuildProfile() =>
    [
        new() { Name = "Enabled", Type = SettingType.Bool, Group = "Printer", Label = "Enabled", Default = true },
        new()
        {
            Name = "PrinterName", Type = SettingType.String, Group = "Printer", Label = "Printer name",
            DefaultFactory = name => name ?? "", Help = "Name of the Windows printer users will see.",
        },
        new()
        {
            Name = "PipeName", Type = SettingType.String, Group = "Printer", Label = "Pipe name",
            DefaultFactory = name => "ErpPrinter-" + Naming.ToSafeName(name ?? ""),
            Help = "The printer port is \\\\.\\pipe\\<name>. Change only if it collides with something else.",
        },
        new()
        {
            Name = "FileNameTemplate", Type = SettingType.String, Group = "Printer", Label = "File name template", Default = "{title}",
            Help = "Tokens: {title} {user} {username} {domain} {computer} {date} {time} {datetime} {id} {jobid} {profile}.",
        },
        new()
        {
            Name = "Backend", Type = SettingType.Choice, Group = "Connection", Label = "Backend", Default = "OdooJson2", Choices = BackendNames,
            Help = "OdooJson2: Odoo 19+ JSON-2 API. OdooJsonRpc: Odoo 14-18 /jsonrpc. ERPNext: Frappe upload_file. Webhook: POST to any URL.",
        },
        new()
        {
            Name = "ServerUrl", Type = SettingType.String, Group = "Connection", Label = "Server URL", Default = "", Required = true,
            Help = "Base URL of the ERP (https://example.odoo.com) or, for Webhook, the full endpoint URL.",
        },
        new()
        {
            Name = "Database", Type = SettingType.String, Group = "Connection", Label = "Database", Default = "", Backends = OdooBackends,
            Help = "Needed when the Odoo server hosts several databases.",
        },
        new()
        {
            Name = "Username", Type = SettingType.String, Group = "Connection", Label = "Login", Default = "", Backends = ["OdooJsonRpc"], Required = true,
            Help = "Login of the Odoo integration user that owns the API key.",
        },
        new()
        {
            Name = "ApiKey", Type = SettingType.Secret, Group = "Connection", Label = "API key", Default = "",
            Help = "Stored encrypted (DPAPI, machine scope) in a registry key only SYSTEM and Administrators can read. For Webhook this is the auth header value.",
        },
        new() { Name = "ApiSecret", Type = SettingType.Secret, Group = "Connection", Label = "API secret", Default = "", Backends = ["ERPNext"], Required = true },
        new()
        {
            Name = "OdooModel", Type = SettingType.String, Group = "Destination", Label = "Odoo model", Default = "ir.attachment", Backends = OdooBackends,
            Help = "Technical name of the model a record is created in. Any model the API user may create works: ir.attachment (every edition), documents.document (Documents app), edi.exchange.record (OCA EDI) or your own.",
        },
        new()
        {
            Name = "OdooFileField", Type = SettingType.String, Group = "Destination", Label = "File field", Default = "datas", Backends = OdooBackends,
            Help = "Binary field of the model that receives the PDF, e.g. datas on ir.attachment and documents.document, or exchange_file on edi.exchange.record.",
        },
        new()
        {
            Name = "OdooNameField", Type = SettingType.String, Group = "Destination", Label = "File name field", Default = "name", Backends = OdooBackends,
            Help = "Field that receives the file name, e.g. name, or exchange_filename on edi.exchange.record. Leave empty if the model has none.",
        },
        new()
        {
            Name = "OdooDefaultValues", Type = SettingType.String, Group = "Destination", Label = "Default values (JSON)", Default = "", Backends = OdooBackends,
            Help = "JSON object of field values set on every created record, like the default values of an Odoo mail alias, e.g. {\"folder_id\": 7, \"description\": \"Printed by {user}\"}. Template tokens are expanded in strings. These override the file name and user fields.",
        },
        new()
        {
            Name = "UserMapping", Type = SettingType.Bool, Group = "Destination", Label = "Map Windows users to Odoo users", Default = false, Backends = OdooBackends,
            Help = "Looks up the printing user in res.users (login or email) and writes the match to the user field.",
        },
        new()
        {
            Name = "OdooUserField", Type = SettingType.String, Group = "Destination", Label = "User field", Default = "owner_id", Backends = OdooBackends,
            Help = "Many2one field to res.users that receives the mapped user, e.g. owner_id on documents.document or user_id on most other models.",
        },
        new()
        {
            Name = "UserEmailDomain", Type = SettingType.String, Group = "Destination", Label = "User email domain", Default = "", Backends = OdooBackends,
            Help = "Optional. Also tries <username>@<domain> when mapping users. Active Directory mail/UPN are tried automatically.",
        },
        new() { Name = "ERPNextFolder", Type = SettingType.String, Group = "Destination", Label = "File folder", Default = "Home", Backends = ["ERPNext"] },
        new() { Name = "ERPNextDoctype", Type = SettingType.String, Group = "Destination", Label = "Attach to DocType", Default = "", Backends = ["ERPNext"] },
        new() { Name = "ERPNextDocname", Type = SettingType.String, Group = "Destination", Label = "Attach to document", Default = "", Backends = ["ERPNext"] },
        new() { Name = "ERPNextIsPrivate", Type = SettingType.Bool, Group = "Destination", Label = "Private file", Default = true, Backends = ["ERPNext"] },
        new()
        {
            Name = "WebhookFormat", Type = SettingType.Choice, Group = "Destination", Label = "Payload format", Default = "Multipart", Choices = ["Multipart", "Json"], Backends = ["Webhook"],
            Help = "Multipart: file plus metadata form fields. Json: base64 content plus metadata.",
        },
        new() { Name = "WebhookFileField", Type = SettingType.String, Group = "Destination", Label = "File field name", Default = "file", Backends = ["Webhook"] },
        new()
        {
            Name = "WebhookAuthHeader", Type = SettingType.String, Group = "Destination", Label = "Auth header name", Default = "Authorization", Backends = ["Webhook"],
            Help = "The API key is sent as the value of this header, if one is set.",
        },
    ];
}

public enum SettingScope
{
    Global,
    Profile,
}
