using System.Security.AccessControl;
using System.Security.Principal;

namespace ErpPrinter.Windows.Setup;

public static class WindowsAdministrator
{
    /// <summary>Whether the process runs as SYSTEM or in an elevated Administrators token (<c>Test-ErpAdministrator</c>).</summary>
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value == "S-1-5-18") return true;
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void Assert()
    {
        if (!IsAdministrator()) throw new UnauthorizedAccessException("This operation needs an elevated (Administrator) session.");
    }
}

public static class DataRootInitializer
{
    public static readonly IReadOnlyList<string> SubFolders = ["spool", "logs", "updates"];

    /// <summary>
    /// Creates the data folder with its sub folders and locks it to SYSTEM and Administrators, by SID
    /// (<c>icacls /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F</c>). Print jobs can contain sensitive documents.
    /// </summary>
    public static void Initialize(string path)
    {
        var root = Directory.CreateDirectory(path);
        foreach (var sub in SubFolders) Directory.CreateDirectory(Path.Combine(path, sub));
        // Setting the ACL last propagates the inheritable entries to everything already inside (like icacls does).
        root.SetAccessControl(CreateSecurity());
    }

    internal static DirectorySecurity CreateSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        return security;
    }
}
