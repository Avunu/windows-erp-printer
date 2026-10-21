using ErpPrinter.Secrets;
using ErpPrinter.Settings;

namespace ErpPrinter.Config;

/// <summary>
/// Fake <see cref="IConfigActions"/> with plausible data, used by <c>--demo</c> and the form tests. Nothing here touches the
/// machine. When a <see cref="ConfigStore"/> is given, the status rows follow its saved profiles.
/// </summary>
internal sealed class DesignTimeConfigActions(ConfigStore? store = null, TimeSpan? delay = null) : IConfigActions
{
    private readonly TimeSpan _delay = delay ?? TimeSpan.FromMilliseconds(600);
    private readonly List<string> _calls = [];

    /// <summary>The actions that were invoked, in order ("RestartService", "SyncPrinters", ...), for tests.</summary>
    public IReadOnlyList<string> Calls => _calls;

    /// <summary>When set, <see cref="SyncPrinters"/> throws this message, like a failing spooler would.</summary>
    public string? SyncFailure { get; set; }

    public string DataRoot { get; } = Path.Combine(Path.GetTempPath(), "ErpPrinter-demo");
    public string LogsPath => Path.Combine(DataRoot, "logs");

    public async Task<(bool Success, string Message)> TestConnectionAsync(PrinterProfile profile, BackendSecrets unsavedSecrets)
    {
        _calls.Add("TestConnection:" + profile.Name);
        await Task.Delay(_delay);
        return profile.ServerUrl.Contains("fail", StringComparison.OrdinalIgnoreCase)
            ? (false, $"Could not reach {profile.ServerUrl}: the remote name could not be resolved.")
            : (true, $"Connected to {profile.ServerUrl} as demo user ({profile.Backend}).");
    }

    public async Task<TestPrintResult> TestPrintAsync(string profileName, Action<string> progress)
    {
        _calls.Add("TestPrint:" + profileName);
        foreach (var step in new[] { "Printing a test page...", "Waiting for the document to arrive in the outbox...", "Uploading to the ERP..." })
        {
            progress(step);
            await Task.Delay(_delay);
        }
        return new TestPrintResult(true, $"""
            ERP Printer test print
            Profile : {profileName}
            Result  : delivered (demo data, nothing was printed)

            [1/3] Spooler accepted the job
            [2/3] Listener queued the document
            [3/3] Uploader delivered it, remote id 4711
            """);
    }

    public async Task<DiagnosticsResult> RunDiagnosticsAsync()
    {
        _calls.Add("Diagnostics");
        await Task.Delay(_delay);
        return new DiagnosticsResult("""
            ERP Printer diagnostics (demo data)

            [ OK ] Service ErpPrinter is running
            [ OK ] Named pipe listener is reachable
            [WARN] Printer 'Send to Odoo (old)' has no profile
            """, Path.Combine(DataRoot, "diagnostics-demo.txt"));
    }

    public StatusSnapshot GetStatus()
    {
        _calls.Add("GetStatus");
        var profiles = (store?.LoadProfiles() ?? []).Select((p, i) => i switch
        {
            0 => Status(p, ok: true, pending: 0, failed: 0, sent: 12, lastSent: new DateTime(2026, 10, 21, 9, 41, 0), remoteId: "1042", error: ""),
            1 => Status(p, ok: true, pending: 2, failed: 1, sent: 3, lastSent: new DateTime(2026, 10, 20, 16, 5, 0), remoteId: null, error: "HTTP 401: invalid API key"),
            2 => Status(p, ok: false, pending: 0, failed: 0, sent: 0, lastSent: null, remoteId: null, error: ""),
            _ => Status(p, ok: true, pending: 0, failed: 0, sent: 1, lastSent: new DateTime(2026, 10, 19, 11, 0, 0), remoteId: null, error: ""),
        }).ToList();
        return new StatusSnapshot("Running", BuildInfo.Version, profiles);
    }

    private ProfileStatus Status(PrinterProfile p, bool ok, int pending, int failed, int sent, DateTime? lastSent, string? remoteId, string error) =>
        new(p.Name, p.Enabled, p.Backend, p.PrinterName, ok && p.Enabled, pending, failed, sent, lastSent, remoteId, error,
            Path.Combine(DataRoot, "spool", Naming.ToSafeName(p.Name), "outbox"));

    public async Task SendNowAsync()
    {
        _calls.Add("SendNow");
        await Task.Delay(_delay);
    }

    public int RetryFailed()
    {
        _calls.Add("RetryFailed");
        return 1;
    }

    public void RestartService() => _calls.Add("RestartService");

    public void SyncPrinters()
    {
        _calls.Add("SyncPrinters");
        if (SyncFailure is not null) throw new InvalidOperationException(SyncFailure);
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        _calls.Add("CheckForUpdate");
        await Task.Delay(_delay);
        return new UpdateCheckResult(false, "You are running the latest version.", BuildInfo.Version, BuildInfo.Version);
    }

    public async Task<string> InstallUpdateAsync()
    {
        _calls.Add("InstallUpdate");
        await Task.Delay(_delay);
        return "Update installed (demo).";
    }
}
