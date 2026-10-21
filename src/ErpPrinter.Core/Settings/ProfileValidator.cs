using System.Text.Json;
using System.Text.RegularExpressions;

namespace ErpPrinter.Settings;

/// <summary>Validates a profile; port of Test-ErpPrinterProfile. An empty result means the profile is usable.</summary>
public static partial class ProfileValidator
{
    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,200}$")]
    private static partial Regex PipeNamePattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex FieldPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)*$")]
    private static partial Regex ModelPattern();

    /// <param name="hasSecret">Whether a secret ("ApiKey" or "ApiSecret") is available, stored or entered but not saved yet.</param>
    /// <param name="others">The other profiles, to detect clashing printer and pipe names.</param>
    public static IReadOnlyList<string> Validate(PrinterProfile profile, Func<string, bool> hasSecret, IEnumerable<PrinterProfile>? others = null)
    {
        var problems = new List<string>();
        var backend = profile.Backend;
        if (!Schema.BackendNames.Contains(backend)) problems.Add($"Unknown backend '{backend}'.");

        if (profile.ServerUrl.Length == 0)
            problems.Add("Server URL is required.");
        else if (!Uri.TryCreate(profile.ServerUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            problems.Add($"Server URL '{profile.ServerUrl}' is not an http(s) URL.");

        if (profile.PrinterName.Length == 0) problems.Add("Printer name is required.");
        if (!PipeNamePattern().IsMatch(profile.PipeName)) problems.Add("Pipe name may only contain letters, digits, dot, dash and underscore.");

        if (backend != "Webhook" && !hasSecret(Secrets.SecretNames.ApiKey)) problems.Add("An API key is required.");
        if (backend == "ERPNext" && !hasSecret(Secrets.SecretNames.ApiSecret)) problems.Add("ERPNext needs an API secret as well as an API key.");
        if (backend == "OdooJsonRpc" && profile.Username.Length == 0) problems.Add("Odoo JSON-RPC needs the login of the API user.");

        if (Schema.OdooBackends.Contains(backend))
        {
            if (!ModelPattern().IsMatch(profile.OdooModel)) problems.Add("Odoo model must be a technical model name such as ir.attachment.");
            if (!FieldPattern().IsMatch(profile.OdooFileField)) problems.Add("File field must be the technical name of a binary field, such as datas.");
            if (profile.OdooNameField.Length > 0 && !FieldPattern().IsMatch(profile.OdooNameField)) problems.Add("File name field must be a technical field name such as name, or empty.");
            if (profile.UserMapping && !FieldPattern().IsMatch(profile.OdooUserField)) problems.Add("User field must be a technical field name such as owner_id when users are mapped.");
            if (profile.OdooDefaultValues.Length > 0 && JsonObjectProblem(profile.OdooDefaultValues) is { } why)
                problems.Add($"Default values are not a valid JSON object: {why}");
        }

        if (backend == "ERPNext" && (profile.ERPNextDoctype.Length > 0) != (profile.ERPNextDocname.Length > 0))
            problems.Add("Set both \"Attach to DocType\" and \"Attach to document\", or neither.");

        foreach (var other in others ?? [])
        {
            if (string.Equals(other.Name, profile.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (other.PrinterName == profile.PrinterName) problems.Add($"Printer name '{profile.PrinterName}' is also used by profile '{other.Name}'.");
            if (other.PipeName == profile.PipeName) problems.Add($"Pipe name '{profile.PipeName}' is also used by profile '{other.Name}'.");
        }
        return problems;
    }

    /// <summary>A short reason when <paramref name="text"/> is not a JSON object, or null when it is one.</summary>
    public static string? JsonObjectProblem(string text)
    {
        if (!text.TrimStart().StartsWith('{')) return "the text does not start with {.";
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? null : "the text is not an object.";
        }
        catch (JsonException ex)
        {
            return ex.Message;
        }
    }
}
