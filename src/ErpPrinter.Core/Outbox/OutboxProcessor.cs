using ErpPrinter.Backends;
using ErpPrinter.Logging;
using ErpPrinter.Secrets;
using ErpPrinter.Settings;

namespace ErpPrinter.Outbox;

public enum SendOutcome
{
    Sent,
    Retry,
    Failed,
}

/// <summary>What one delivery pass did for one profile.</summary>
public sealed record ProfilePassResult(string Profile, int Sent, int Retrying, int Failed);

/// <summary>
/// The upload/retry engine: one pass delivers every due document of every profile, moves permanent failures to the failed
/// folder and schedules transient ones with exponential backoff. Port of Invoke-ErpOutboxPass / Send-ErpOutboxItem.
/// </summary>
/// <param name="fileNamer">Builds the file name a document is sent under from the profile's template (<c>FileNameTemplate</c>).</param>
/// <param name="signal">Set after failed documents are restored so the uploader wakes immediately; optional.</param>
public sealed class OutboxProcessor(
    IErpLog log,
    IBackendProvider backends,
    SecretStore secrets,
    Func<PrinterProfile, OutboxMeta, string> fileNamer,
    TimeProvider time,
    IOutboxSignal? signal = null)
{
    private readonly OutboxStore _store = new(log, time);

    public OutboxStore Store => _store;

    /// <summary>Runs a single delivery pass over all outboxes and returns per-profile counts (profiles without an outbox folder are skipped).</summary>
    /// <param name="ignoreBackoff">Try every waiting document now, ignoring retry times.</param>
    public async Task<IReadOnlyList<ProfilePassResult>> RunPassAsync(ErpConfig config, bool ignoreBackoff, CancellationToken cancellationToken)
    {
        var now = time.GetLocalNow();
        var results = new List<ProfilePassResult>();
        foreach (var profile in config.Profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var paths = OutboxPaths.For(config.Global.DataRoot, profile.Name);
            if (!Directory.Exists(paths.Outbox)) continue;
            _store.RepairOrphans(paths.Outbox, profile.Name);

            int sent = 0, retrying = 0, failed = 0;
            foreach (var item in _store.List(paths.Outbox, dueOnly: !ignoreBackoff, now))
            {
                SendOutcome outcome;
                try { outcome = await SendItemAsync(item, profile, config.Global, paths, now, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Deviation from PowerShell, where this aborted the whole pass: one stuck item must not block the others.
                    log.Exception($"Could not process '{item.Meta.Title}' ({item.Meta.Id}) for profile '{profile.Name}'", ex);
                    outcome = SendOutcome.Retry;
                }
                switch (outcome)
                {
                    case SendOutcome.Sent: sent++; break;
                    case SendOutcome.Failed: failed++; break;
                    default: retrying++; break;
                }
            }
            results.Add(new ProfilePassResult(profile.Name, sent, retrying, failed));
        }
        return results;
    }

    /// <summary>Tries one item and moves or updates its files accordingly.</summary>
    public async Task<SendOutcome> SendItemAsync(
        OutboxItem item, PrinterProfile profile, GlobalSettings global, OutboxPaths paths, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var meta = item.Meta;
        meta.Attempts++;
        meta.LastAttempt = now;
        var label = $"'{meta.Title}' ({meta.Id}) for profile '{profile.Name}'";
        try
        {
            if (!File.Exists(item.PdfPath)) throw new ErpException("The PDF file is missing.", permanent: true);
            var maxBytes = (long)global.MaxDocumentSizeMB * 1024 * 1024;
            if (new FileInfo(item.PdfPath).Length > maxBytes)
                throw new ErpException($"Document is larger than MaxDocumentSizeMB ({global.MaxDocumentSizeMB} MB).", permanent: true);
            if (!profile.Enabled) throw new ErpException("The printer profile is disabled.");

            log.Verbose($"Sending {label} to {profile.Backend} at {profile.ServerUrl} (attempt {meta.Attempts}).");
            var backend = backends.Get(profile.Backend);
            var document = new DocumentToSend(meta, item.PdfPath, fileNamer(profile, meta));
            var remoteId = await backend.SendAsync(profile, global, document, secrets.Get(profile.Name), cancellationToken).ConfigureAwait(false);
            meta.RemoteId = remoteId;
            meta.SentAt = now;
            meta.LastError = null;
            meta.NextAttempt = null;
            if (global.KeepSentDays > 0) _store.Move(item, paths.Sent);
            else _store.Remove(item);
            _store.AddDeliveryStat(paths, meta);
            log.Information($"Delivered {label} to {profile.Backend} (remote id: {remoteId}).", 1002);
            return SendOutcome.Sent;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            var message = ex.Message;
            var permanent = ErpException.IsPermanent(ex);
            meta.LastError = message;
            var exhausted = global.MaxAttempts > 0 && meta.Attempts >= global.MaxAttempts;
            if (permanent || exhausted)
            {
                meta.NextAttempt = null;
                _store.Move(item, paths.Failed);
                var why = permanent ? "permanent error" : $"gave up after {meta.Attempts} attempts";
                log.Error($"Moved {label} to the failed folder ({why}): {message}", 3002);
                return SendOutcome.Failed;
            }
            var delay = RetryPolicy.DelaySeconds(meta.Attempts, global.RetryMaxBackoffMinutes);
            meta.NextAttempt = now.AddSeconds(delay);
            _store.SaveMeta(paths.Outbox, meta);
            log.Warning($"Attempt {meta.Attempts} for {label} failed, retrying in {delay} s: {message}", 2002);
            return SendOutcome.Retry;
        }
    }

    /// <summary>Moves failed documents back to the outbox with their attempt counter reset, then wakes the uploader.</summary>
    /// <param name="profileName">Only this profile (case-insensitive); null for all.</param>
    /// <param name="id">Only this document; null for all.</param>
    /// <returns>The number of documents restored.</returns>
    public int RetryFailed(ErpConfig config, string? profileName = null, string? id = null)
    {
        var count = 0;
        foreach (var profile in config.Profiles.Where(p => string.IsNullOrEmpty(profileName) || string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase)))
        {
            var paths = OutboxPaths.For(config.Global.DataRoot, profile.Name);
            foreach (var item in _store.List(paths.Failed))
            {
                if (!string.IsNullOrEmpty(id) && !string.Equals(item.Meta.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
                item.Meta.Attempts = 0;
                item.Meta.NextAttempt = null;
                _store.Move(item, paths.Outbox);
                count++;
            }
        }
        try { signal?.Set(); }
        catch (Exception ex) { log.Verbose($"Could not signal uploader: {ex.Message}"); }
        return count;
    }
}
