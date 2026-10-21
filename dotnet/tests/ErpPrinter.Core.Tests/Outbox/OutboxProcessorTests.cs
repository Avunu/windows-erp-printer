using ErpPrinter.Backends;
using ErpPrinter.Logging;
using ErpPrinter.Outbox;
using ErpPrinter.Secrets;
using ErpPrinter.Settings;

namespace ErpPrinter.Core.Tests;

/// <summary>Port of the 'Send-ErpOutboxItem' and 'Restore-ErpPrinterFailedDocument' Pester cases, plus the pass loop.</summary>
public sealed class OutboxProcessorTests : IDisposable
{
    private readonly OutboxFixture _f = new();
    private readonly string _id;

    public OutboxProcessorTests() => _id = _f.AddItem();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task Deletes_the_item_after_a_successful_delivery()
    {
        Assert.Equal(SendOutcome.Sent, await _f.SendAsync(_id));
        Assert.Equal(0, OutboxFixture.Count(_f.Paths.Outbox));
        Assert.Equal(0, OutboxFixture.Count(_f.Paths.Sent));
    }

    [Fact]
    public async Task Logs_the_delivery_with_event_id_1002()
    {
        await _f.SendAsync(_id);
        Assert.Contains(_f.Log.Entries, e =>
            e.Level == ErpLogLevel.Information && e.EventId == 1002 &&
            e.Message == $"Delivered 'Invoice 7' ({_id}) for profile 'Send to Odoo' to OdooJson2 (remote id: remote-42).");
        Assert.Contains(_f.Log.Entries, e =>
            e.Level == ErpLogLevel.Verbose && e.Message == $"Sending 'Invoice 7' ({_id}) for profile 'Send to Odoo' to OdooJson2 at https://erp.example (attempt 1).");
    }

    [Fact]
    public async Task Hands_the_backend_the_file_name_the_secrets_and_the_pdf()
    {
        _f.Secrets.Set(OutboxFixture.ProfileName, SecretNames.ApiKey, "key-1");
        await _f.SendAsync(_id);
        var call = Assert.Single(_f.Backend.Calls);
        Assert.Equal("Invoice 7.pdf", call.Document.FileName);
        Assert.Equal(Path.Combine(_f.Paths.Outbox, _id + ".pdf"), call.Document.PdfPath);
        Assert.Equal("key-1", call.Secrets.ApiKey);
        Assert.Equal(_id, call.Document.Meta.Id);
    }

    [Fact]
    public async Task Counts_deliveries_whether_or_not_sent_documents_are_kept()
    {
        Assert.Equal(SendOutcome.Sent, await _f.SendAsync(_id));
        var second = _f.AddItem();
        _f.Configure(new() { ["KeepSentDays"] = 7 });
        Assert.Equal(SendOutcome.Sent, await _f.SendAsync(second));

        var stats = _f.Store.GetStats(_f.Paths);
        Assert.Equal(2, stats.Count);
        Assert.Equal("Invoice 7", stats.LastTitle);
        Assert.Equal("remote-42", stats.LastRemoteId);
        Assert.NotNull(stats.LastSentAt);
    }

    [Fact]
    public async Task Still_reports_success_when_the_delivery_stats_cannot_be_written()
    {
        Directory.CreateDirectory(_f.Paths.Stats);
        Assert.Equal(SendOutcome.Sent, await _f.SendAsync(_id));
        Assert.Contains(_f.Log.Entries, e => e.Level == ErpLogLevel.Warning && e.Message.StartsWith("Could not update delivery stats"));
    }

    [Fact]
    public async Task Archives_delivered_items_when_KeepSentDays_is_set()
    {
        _f.Configure(new() { ["KeepSentDays"] = 7 });
        Assert.Equal(SendOutcome.Sent, await _f.SendAsync(_id));
        var meta = _f.ReadMeta(_f.Paths.Sent, _id);
        Assert.Equal("remote-42", meta.RemoteId);
        Assert.NotNull(meta.SentAt);
        Assert.True(File.Exists(Path.Combine(_f.Paths.Sent, _id + ".pdf")));
        Assert.Equal(0, OutboxFixture.Count(_f.Paths.Outbox));
    }

    [Fact]
    public async Task Schedules_a_retry_on_transient_errors()
    {
        _f.Backend.OnSend = _ => throw new HttpRequestException("Could not reach server");
        Assert.Equal(SendOutcome.Retry, await _f.SendAsync(_id));
        var meta = _f.ReadMeta(_f.Paths.Outbox, _id);
        Assert.Equal(1, meta.Attempts);
        Assert.Contains("Could not reach", meta.LastError);
        Assert.Equal(_f.Clock.GetLocalNow().AddSeconds(30), meta.NextAttempt);
        Assert.Equal(_f.Clock.GetLocalNow(), meta.LastAttempt);
        Assert.Contains(_f.Log.Entries, e =>
            e.Level == ErpLogLevel.Warning && e.EventId == 2002 &&
            e.Message == $"Attempt 1 for 'Invoice 7' ({_id}) for profile 'Send to Odoo' failed, retrying in 30 s: Could not reach server");
    }

    [Fact]
    public async Task Doubles_the_delay_with_each_attempt()
    {
        _f.Backend.OnSend = _ => throw new HttpRequestException("down");
        await _f.SendAsync(_id);
        await _f.SendAsync(_id);
        var meta = _f.ReadMeta(_f.Paths.Outbox, _id);
        Assert.Equal(2, meta.Attempts);
        Assert.Equal(_f.Clock.GetLocalNow().AddSeconds(60), meta.NextAttempt);
    }

    [Fact]
    public async Task Moves_the_item_to_failed_on_permanent_errors()
    {
        _f.Backend.OnSend = _ => throw new ErpException("HTTP 400: bad field", permanent: true);
        Assert.Equal(SendOutcome.Failed, await _f.SendAsync(_id));
        Assert.Contains("bad field", _f.ReadMeta(_f.Paths.Failed, _id).LastError);
        Assert.True(File.Exists(Path.Combine(_f.Paths.Failed, _id + ".pdf")));
        Assert.Equal(0, OutboxFixture.Count(_f.Paths.Outbox));
        Assert.Contains(_f.Log.Entries, e =>
            e.Level == ErpLogLevel.Error && e.EventId == 3002 &&
            e.Message == $"Moved 'Invoice 7' ({_id}) for profile 'Send to Odoo' to the failed folder (permanent error): HTTP 400: bad field");
    }

    [Fact]
    public async Task Finds_a_permanent_error_wrapped_in_another_exception()
    {
        _f.Backend.OnSend = _ => throw new InvalidOperationException("outer", new ErpException("inner", permanent: true));
        Assert.Equal(SendOutcome.Failed, await _f.SendAsync(_id));
    }

    [Fact]
    public async Task Gives_up_after_MaxAttempts()
    {
        _f.Configure(new() { ["MaxAttempts"] = 2 });
        _f.Backend.OnSend = _ => throw new TimeoutException("timeout");
        Assert.Equal(SendOutcome.Retry, await _f.SendAsync(_id));
        Assert.Equal(SendOutcome.Failed, await _f.SendAsync(_id));
        Assert.Equal(2, _f.ReadMeta(_f.Paths.Failed, _id).Attempts);
        Assert.Null(_f.ReadMeta(_f.Paths.Failed, _id).NextAttempt);
        Assert.Contains(_f.Log.Entries, e => e.EventId == 3002 && e.Message.Contains("(gave up after 2 attempts): timeout"));
    }

    [Fact]
    public async Task Rejects_documents_over_the_size_limit_without_calling_the_backend()
    {
        var big = _f.AddItem(extraBytes: 1024 * 1024 + 10);
        _f.Configure(new() { ["MaxDocumentSizeMB"] = 1 });
        Assert.Equal(SendOutcome.Failed, await _f.SendAsync(big));
        Assert.Empty(_f.Backend.Calls);
        Assert.Contains("larger than MaxDocumentSizeMB (1 MB)", _f.ReadMeta(_f.Paths.Failed, big).LastError);
    }

    [Fact]
    public async Task Keeps_items_of_disabled_profiles_for_later()
    {
        Assert.Equal(SendOutcome.Retry, await _f.SendAsync(_id, enabled: false));
        Assert.Empty(_f.Backend.Calls);
        Assert.Equal("The printer profile is disabled.", _f.ReadMeta(_f.Paths.Outbox, _id).LastError);
    }

    [Fact]
    public async Task Fails_permanently_when_the_pdf_is_missing()
    {
        File.Delete(Path.Combine(_f.Paths.Outbox, _id + ".pdf"));
        Assert.Equal(SendOutcome.Failed, await _f.SendAsync(_id));
        Assert.Equal("The PDF file is missing.", _f.ReadMeta(_f.Paths.Failed, _id).LastError);
        Assert.Empty(_f.Backend.Calls);
    }

    [Fact]
    public async Task Fails_permanently_when_the_backend_is_not_available()
    {
        var processor = new OutboxProcessor(_f.Log, new MissingBackendProvider(), _f.Secrets, (_, m) => m.Title, _f.Clock);
        var outcome = await processor.SendItemAsync(_f.GetItem(_id), _f.Profile, _f.Global, _f.Paths, _f.Clock.GetLocalNow(), CancellationToken.None);
        Assert.Equal(SendOutcome.Failed, outcome);
        Assert.Contains("not available", _f.ReadMeta(_f.Paths.Failed, _id).LastError);
    }

    [Fact]
    public async Task Cancellation_stops_the_send_without_counting_an_attempt()
    {
        using var cts = new CancellationTokenSource();
        _f.Backend.OnSend = _ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _f.Processor.SendItemAsync(_f.GetItem(_id), _f.Profile, _f.Global, _f.Paths, _f.Clock.GetLocalNow(), cts.Token));
        Assert.Equal(0, _f.ReadMeta(_f.Paths.Outbox, _id).Attempts);
    }

    [Fact]
    public async Task A_timeout_that_is_not_our_cancellation_is_a_transient_failure()
    {
        _f.Backend.OnSend = _ => throw new TaskCanceledException("The request timed out");
        Assert.Equal(SendOutcome.Retry, await _f.SendAsync(_id));
    }

    // --- Restore-ErpPrinterFailedDocument ----------------------------------------------------

    [Fact]
    public void Moves_failed_items_back_with_a_reset_attempt_counter_and_wakes_the_uploader()
    {
        var failedId = _f.AddItem(folder: _f.Paths.Failed);
        var meta = _f.ReadMeta(_f.Paths.Failed, failedId);
        meta.Attempts = 9;
        meta.NextAttempt = _f.Clock.GetLocalNow().AddHours(1);
        meta.LastError = "old error";
        _f.Store.SaveMeta(_f.Paths.Failed, meta);

        var signal = new InProcessOutboxSignal();
        var processor = new OutboxProcessor(_f.Log, new OutboxFakeBackendProvider(_f.Backend), _f.Secrets, (_, m) => m.Title, _f.Clock, signal);

        Assert.Equal(1, processor.RetryFailed(_f.Config));
        Assert.Equal(0, _f.ReadMeta(_f.Paths.Outbox, failedId).Attempts);
        Assert.Null(_f.ReadMeta(_f.Paths.Outbox, failedId).NextAttempt);
        Assert.True(File.Exists(Path.Combine(_f.Paths.Outbox, failedId + ".pdf")));
        Assert.Equal(0, OutboxFixture.Count(_f.Paths.Failed));
        Assert.True(signal.Wait(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public void Retry_failed_can_be_limited_to_a_profile_and_a_document()
    {
        var a = _f.AddItem("aaa", folder: _f.Paths.Failed);
        var b = _f.AddItem("bbb", folder: _f.Paths.Failed);

        Assert.Equal(0, _f.Processor.RetryFailed(_f.Config, "Some other profile"));
        Assert.Equal(0, _f.Processor.RetryFailed(_f.Config, id: "nope"));
        Assert.Equal(1, _f.Processor.RetryFailed(_f.Config, "send to odoo", "AAA"));
        Assert.True(File.Exists(Path.Combine(_f.Paths.Outbox, a + ".json")));
        Assert.True(File.Exists(Path.Combine(_f.Paths.Failed, b + ".json")));
        Assert.Equal(1, _f.Processor.RetryFailed(_f.Config, null, null));
    }

    // --- Invoke-ErpOutboxPass ------------------------------------------------------------------

    [Fact]
    public async Task A_pass_delivers_due_items_and_counts_per_profile()
    {
        var ok = _f.AddItem("ok");
        var transient = _f.AddItem("transient");
        var permanent = _f.AddItem("permanent");
        _f.Backend.OnSend = d => d.Meta.Id switch
        {
            "transient" => throw new HttpRequestException("down"),
            "permanent" => throw new ErpException("rejected", permanent: true),
            _ => "r-1",
        };

        var result = Assert.Single(await _f.Processor.RunPassAsync(_f.Config, ignoreBackoff: false, CancellationToken.None));

        // The fixture's own item (_id) is delivered as well.
        Assert.Equal(new ProfilePassResult(OutboxFixture.ProfileName, 2, 1, 1), result);
        Assert.True(File.Exists(Path.Combine(_f.Paths.Outbox, transient + ".json")));
        Assert.True(File.Exists(Path.Combine(_f.Paths.Failed, permanent + ".json")));
        Assert.False(File.Exists(Path.Combine(_f.Paths.Outbox, ok + ".json")));
    }

    [Fact]
    public async Task A_pass_honours_backoff_unless_told_to_ignore_it()
    {
        _f.Backend.OnSend = _ => throw new HttpRequestException("down");
        await _f.Processor.RunPassAsync(_f.Config, false, CancellationToken.None);
        Assert.Single(_f.Backend.Calls);

        var skipped = Assert.Single(await _f.Processor.RunPassAsync(_f.Config, false, CancellationToken.None));
        Assert.Equal(new ProfilePassResult(OutboxFixture.ProfileName, 0, 0, 0), skipped);
        Assert.Single(_f.Backend.Calls);

        var forced = Assert.Single(await _f.Processor.RunPassAsync(_f.Config, true, CancellationToken.None));
        Assert.Equal(1, forced.Retrying);
        Assert.Equal(2, _f.Backend.Calls.Count);

        _f.Clock.Advance(TimeSpan.FromMinutes(10));
        await _f.Processor.RunPassAsync(_f.Config, false, CancellationToken.None);
        Assert.Equal(3, _f.Backend.Calls.Count);
    }

    [Fact]
    public async Task A_pass_skips_profiles_without_an_outbox_folder()
    {
        var other = PrinterProfile.Create("Other");
        var results = await _f.Processor.RunPassAsync(new ErpConfig(_f.Global, [other, _f.Profile]), false, CancellationToken.None);
        Assert.Equal([OutboxFixture.ProfileName], results.Select(r => r.Profile));
    }

    [Fact]
    public async Task A_pass_adopts_orphans_before_delivering()
    {
        var pdf = Path.Combine(_f.Paths.Outbox, "lost.pdf");
        OutboxFixture.WritePdf(pdf);
        File.SetLastWriteTimeUtc(pdf, _f.Clock.Now.UtcDateTime.AddMinutes(-10));
        var result = Assert.Single(await _f.Processor.RunPassAsync(_f.Config, false, CancellationToken.None));
        Assert.Equal(2, result.Sent);
        Assert.Contains(_f.Backend.Calls, c => c.Document.Meta.Title == "Recovered print job");
    }

    [Fact]
    public async Task A_pass_keeps_going_when_one_item_cannot_be_processed()
    {
        // The failed folder cannot be created because a file sits in its place.
        var good = _f.AddItem("good");
        _f.Backend.OnSend = d => d.Meta.Id == _id ? throw new ErpException("no", permanent: true) : "r";
        Directory.Delete(_f.Paths.Failed);
        File.WriteAllText(_f.Paths.Failed, "in the way");
        File.SetLastWriteTimeUtc(Path.Combine(_f.Paths.Outbox, _id + ".json"), _f.Clock.Now.UtcDateTime.AddMinutes(-5));

        var result = Assert.Single(await _f.Processor.RunPassAsync(_f.Config, false, CancellationToken.None));

        Assert.Equal(1, result.Sent);
        Assert.Equal(1, result.Retrying);
        Assert.False(File.Exists(Path.Combine(_f.Paths.Outbox, good + ".json")));
        Assert.Contains(_f.Log.Entries, e => e.Level == ErpLogLevel.Error && e.Message.StartsWith("Could not process 'Invoice 7'"));
    }

    private sealed class MissingBackendProvider : IBackendProvider
    {
        public IBackend Get(string backendName) => throw new ErpException($"The backend '{backendName}' is not available in this version.", permanent: true);
    }
}
