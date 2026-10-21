using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using ErpPrinter.Http;
using ErpPrinter.Logging;
using ErpPrinter.Outbox;
using ErpPrinter.Settings;
using ErpPrinter.Templating;

namespace ErpPrinter.Backends;

/// <summary>Looks up the mail address and user principal name of a Windows account in the directory (Active Directory). Implemented by the Windows layer.</summary>
public interface IDirectoryIdentity
{
    /// <summary>Returns the account's mail and userPrincipalName values; empty when off-domain or on any lookup failure.</summary>
    Task<IReadOnlyList<string>> LookupAsync(string samAccountName, CancellationToken cancellationToken);
}

public sealed class NullDirectoryIdentity : IDirectoryIdentity
{
    public static readonly NullDirectoryIdentity Instance = new();
    public Task<IReadOnlyList<string>> LookupAsync(string samAccountName, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
}

/// <summary>
/// Shared by the OdooJson2 and OdooJsonRpc backends: the create() values, the field check of the connection test
/// and the Windows-user to res.users mapping with its cache. Port of Odoo.Common.ps1.
/// </summary>
public sealed class OdooCommon(TimeProvider? time = null, IDirectoryIdentity? directory = null, IErpLog? log = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly IDirectoryIdentity _directory = directory ?? NullDirectoryIdentity.Instance;
    private readonly IErpLog _log = log ?? NullLog.Instance;
    private readonly ConcurrentDictionary<string, (int Id, DateTimeOffset Expires)> _userCache = new();

    public TimeProvider Time => _time;

    /// <summary>
    /// Builds the create() values, the way an Odoo mail alias builds a record: the file name and the mapped user
    /// first, then the profile's default values on top. The file field comes last and holds
    /// <see cref="Base64JsonContent.Placeholder"/>, replaced with base64 at send time.
    /// </summary>
    public JsonObject BuildVals(PrinterProfile profile, OutboxMeta meta, string fileName, int ownerId = 0)
    {
        if (profile.OdooFileField.Length == 0)
            throw new ErpException("The profile has no File field to put the document in.", permanent: true);

        var vals = new JsonObject();
        if (profile.OdooNameField.Length > 0) vals[profile.OdooNameField] = fileName;
        if (ownerId > 0 && profile.OdooUserField.Length > 0) vals[profile.OdooUserField] = ownerId;
        if (profile.OdooDefaultValues.Length > 0)
        {
            var expanded = Template.Expand(profile.OdooDefaultValues, TemplateTokens.Create(meta, _time), jsonEscape: true);
            JsonObject defaults;
            try { defaults = Template.ParseJsonObject(expanded); }
            catch (FormatException ex) { throw new ErpException($"Default values are not a valid JSON object: {ex.Message}", permanent: true); }
            foreach (var (name, value) in defaults.ToArray())
            {
                defaults.Remove(name); // detach from the parent so it can be added to vals
                vals[name] = value;
            }
        }
        vals[profile.OdooFileField] = Base64JsonContent.Placeholder;
        return vals;
    }

    /// <summary>
    /// For Test connection: confirms the model has every field the profile writes and that the file field is binary,
    /// so a typo shows up now and not on the first printed document. Returns " Fields found: a, b."
    /// </summary>
    /// <param name="fieldsGet">Backend-specific: (model, field names) to the field definitions by name.</param>
    public static async Task<string> FieldCheckMessageAsync(
        PrinterProfile profile,
        Func<string, IReadOnlyList<string>, Task<JsonNode?>> fieldsGet)
    {
        var names = new List<string> { profile.OdooFileField };
        if (profile.OdooNameField.Length > 0) names.Add(profile.OdooNameField);
        if (profile.UserMapping && profile.OdooUserField.Length > 0) names.Add(profile.OdooUserField);
        if (profile.OdooDefaultValues.Length > 0)
        {
            JsonObject defaults;
            try { defaults = Template.ParseJsonObject(profile.OdooDefaultValues); }
            catch (FormatException ex) { throw new ErpException($"Default values are not a valid JSON object: {ex.Message}", permanent: true); }
            names.AddRange(defaults.Select(p => p.Key));
        }
        names = names.Distinct(StringComparer.Ordinal).ToList();

        var known = await fieldsGet(profile.OdooModel, names).ConfigureAwait(false) as JsonObject;
        var missing = names.Where(n => known is null || !known.ContainsKey(n)).ToList();
        if (missing.Count > 0)
            throw new ErpException($"Model {profile.OdooModel} has no field named {string.Join(", ", missing)}.");

        var fileType = ErpJson.Text(ErpJson.Get(known![profile.OdooFileField], "type"));
        if (fileType != "binary")
            throw new ErpException($"File field {profile.OdooFileField} is of type {fileType}; it must be a binary field.");
        return $" Fields found: {string.Join(", ", names)}.";
    }

    /// <summary>
    /// The strings an Odoo login or email might match for a Windows user: the account as given, the bare user name,
    /// &lt;user&gt;@&lt;UserEmailDomain&gt;, and the directory's mail and UPN when available. Lower case, distinct.
    /// </summary>
    public async Task<IReadOnlyList<string>> IdentityCandidatesAsync(string user, string emailDomain, CancellationToken cancellationToken)
    {
        if (user.Length == 0) return [];
        var sam = user;
        var backslash = user.IndexOf('\\');
        if (backslash > 0 && backslash < user.Length - 1) sam = user[(backslash + 1)..];

        var candidates = new List<string> { user, sam };
        if (emailDomain.Length > 0) candidates.Add($"{sam}@{emailDomain.TrimStart('@')}");
        candidates.AddRange(await _directory.LookupAsync(sam, cancellationToken).ConfigureAwait(false));
        return candidates.Where(c => !string.IsNullOrEmpty(c)).Select(c => c.ToLowerInvariant()).Distinct().ToList();
    }

    /// <summary>
    /// Maps the printing Windows user to a res.users id, caching hits for a day and misses for an hour; 0 when
    /// mapping is off, the user is unknown or the lookup failed (failures are logged, not cached).
    /// </summary>
    /// <param name="searchUser">Backend-specific: an Odoo domain to the id of the first matching user, or null.</param>
    public async Task<int> ResolveOwnerIdAsync(
        PrinterProfile profile,
        OutboxMeta meta,
        Func<JsonArray, Task<int?>> searchUser,
        CancellationToken cancellationToken)
    {
        if (!profile.UserMapping || profile.OdooUserField.Length == 0 || string.IsNullOrEmpty(meta.User)) return 0;

        var cacheKey = $"{profile.Name}|{meta.User}".ToLowerInvariant();
        if (_userCache.TryGetValue(cacheKey, out var cached) && cached.Expires > _time.GetUtcNow()) return cached.Id;

        var candidates = await IdentityCandidatesAsync(meta.User, profile.UserEmailDomain, cancellationToken).ConfigureAwait(false);
        JsonArray Names() => new(candidates.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
        var domain = new JsonArray("|", new JsonArray("login", "in", Names()), new JsonArray("email", "in", Names()));

        var id = 0;
        try
        {
            id = await searchUser(domain).ConfigureAwait(false) ?? 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _log.Warning($"User lookup for '{meta.User}' failed: {ex.Message}");
            return 0;
        }

        _userCache[cacheKey] = (id, _time.GetUtcNow().AddHours(id != 0 ? 24 : 1));
        if (id == 0) _log.Information($"No Odoo user matches '{meta.User}' (tried: {string.Join(", ", candidates)}).");
        return id;
    }
}
