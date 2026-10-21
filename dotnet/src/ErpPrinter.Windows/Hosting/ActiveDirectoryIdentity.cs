using System.DirectoryServices;
using System.Text;
using ErpPrinter.Backends;
using ErpPrinter.Logging;

namespace ErpPrinter.Windows.Hosting;

/// <summary>
/// Looks up the mail and userPrincipalName of a Windows account in Active Directory, to map the printing user to an
/// Odoo user. Returns nothing off-domain or on any failure (the PowerShell product did the same).
/// </summary>
public sealed class ActiveDirectoryIdentity(IErpLog? log = null) : IDirectoryIdentity
{
    private readonly IErpLog _log = log ?? NullLog.Instance;

    public Task<IReadOnlyList<string>> LookupAsync(string samAccountName, CancellationToken cancellationToken) =>
        // DirectorySearcher is synchronous and can block for its 5 second client timeout, so keep it off the caller's thread.
        Task.Run<IReadOnlyList<string>>(() => Lookup(samAccountName), cancellationToken);

    private IReadOnlyList<string> Lookup(string samAccountName)
    {
        try
        {
            using var searcher = new DirectorySearcher
            {
                Filter = $"(&(objectCategory=person)(sAMAccountName={EscapeFilter(samAccountName)}))",
                ClientTimeout = TimeSpan.FromSeconds(5),
            };
            searcher.PropertiesToLoad.Add("mail");
            searcher.PropertiesToLoad.Add("userprincipalname");
            var result = searcher.FindOne();
            if (result is null) return [];
            var values = new List<string>();
            foreach (var name in new[] { "mail", "userprincipalname" })
                foreach (var value in result.Properties[name])
                    if (value?.ToString() is { Length: > 0 } text) values.Add(text);
            return values;
        }
        catch (Exception ex)
        {
            _log.Verbose($"Directory lookup for '{samAccountName}' skipped: {ex.Message}");
            return [];
        }
    }

    /// <summary>Escapes the characters that are special in an LDAP filter (RFC 4515).</summary>
    internal static string EscapeFilter(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\\' or '*' or '(' or ')' or '\0') builder.Append('\\').Append(((int)c).ToString("x2"));
            else builder.Append(c);
        }
        return builder.ToString();
    }
}
