using ErpPrinter.Secrets;
using ErpPrinter.Settings;

namespace ErpPrinter.Config;

/// <summary>
/// Everything the configuration window does outside <see cref="ConfigStore"/> and <see cref="SecretStore"/>: talking to the
/// service, the spooler, the ERP and the updater. The form never calls Windows plumbing directly, so it can run against
/// <see cref="DesignTimeConfigActions"/> (fake data) and be tested without a service.
/// </summary>
/// <remarks>
/// Threading: <see cref="GetStatus"/>, <see cref="RetryFailed"/>, <see cref="RestartService"/> and <see cref="SyncPrinters"/>
/// are called on the UI thread and should return quickly (or accept a short freeze, like the PowerShell GUI did). The
/// <c>Task</c>-returning methods are awaited, so they may do real work off the UI thread. <c>progress</c> callbacks of
/// <see cref="TestPrintAsync"/> may come from any thread; the form marshals them.
/// </remarks>
public interface IConfigActions
{
    /// <summary>The data folder (outbox, logs); re-read when needed, because saving can change it.</summary>
    string DataRoot { get; }

    /// <summary>The log folder, normally <c>&lt;DataRoot&gt;\logs</c>.</summary>
    string LogsPath { get; }

    /// <summary>Tests the connection with the profile as edited. <paramref name="unsavedSecrets"/> holds secrets typed but not saved yet (empty entries mean "use the stored one").</summary>
    Task<(bool Success, string Message)> TestConnectionAsync(PrinterProfile profile, BackendSecrets unsavedSecrets);

    /// <summary>Prints a test page through the real printer and follows it to the ERP. The profile must be saved and applied.</summary>
    Task<TestPrintResult> TestPrintAsync(string profileName, Action<string> progress);

    /// <summary>Runs the diagnostics; the report is shown in a dialog.</summary>
    Task<DiagnosticsResult> RunDiagnosticsAsync();

    /// <summary>Service state, version and the outbox/printer state per profile.</summary>
    StatusSnapshot GetStatus();

    /// <summary>Asks the uploader to process the outbox now.</summary>
    Task SendNowAsync();

    /// <summary>Moves failed documents back to the outbox; returns how many.</summary>
    int RetryFailed();

    void RestartService();

    /// <summary>Creates, repairs and removes the Windows printers to match the saved profiles. May throw; the message is shown as a warning after saving.</summary>
    void SyncPrinters();

    /// <summary>Checks the update manifest without installing.</summary>
    Task<UpdateCheckResult> CheckForUpdateAsync();

    /// <summary>Downloads and installs the available update; returns a message for the user.</summary>
    Task<string> InstallUpdateAsync();
}

public sealed record TestPrintResult(bool Success, string Report);

/// <param name="Report">Text for the report dialog.</param>
/// <param name="ReportPath">Where the report was saved, if it was.</param>
public sealed record DiagnosticsResult(string Report, string? ReportPath = null);

public sealed record UpdateCheckResult(bool UpdateAvailable, string Message, string? AvailableVersion = null, string? CurrentVersion = null);

/// <param name="ServiceState">Text for the status tab, for example "Running" or "Stopped".</param>
public sealed record StatusSnapshot(string ServiceState, string Version, IReadOnlyList<ProfileStatus> Profiles);

/// <summary>Outbox and printer state of one profile.</summary>
/// <param name="PrinterOk">The Windows printer exists and is healthy.</param>
/// <param name="LastSent">Local time of the last delivery, if any.</param>
/// <param name="LastError">Empty when there is none.</param>
/// <param name="OutboxPath">Opened by "Open data folder" when the row is selected.</param>
public sealed record ProfileStatus(
    string Profile,
    bool Enabled,
    string Backend,
    string PrinterName,
    bool PrinterOk,
    int Pending,
    int Failed,
    int Sent,
    DateTime? LastSent,
    string? LastRemoteId,
    string LastError,
    string OutboxPath);
