using ErpPrinter.Logging;
using ErpPrinter.Outbox;

namespace ErpPrinter.Core.Tests;

/// <summary>Port of the 'Outbox files' and 'Get-ErpRetryDelaySeconds' Pester cases (tests/Unit/Outbox.Tests.ps1).</summary>
public sealed class OutboxFilesTests : IDisposable
{
    private readonly OutboxFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public void Derives_per_profile_folders_with_a_safe_name()
    {
        Assert.Contains(Path.Combine("spool", "Send_to_Odoo", "outbox"), _f.Paths.Outbox);
        Assert.EndsWith("outbox", _f.Paths.Outbox);
        Assert.True(Directory.Exists(_f.Paths.Failed));
        Assert.True(Directory.Exists(_f.Paths.Sent));
        Assert.Equal(Path.Combine(_f.Paths.Root, "delivered.json"), _f.Paths.Stats);
    }

    [Fact]
    public void Round_trips_sidecar_metadata()
    {
        var id = _f.AddItem();
        var items = _f.Store.List(_f.Paths.Outbox);
        var item = Assert.Single(items);
        Assert.Equal(@"CORP\jdoe", item.Meta.User);
        Assert.Equal(0, item.Meta.Attempts);
        Assert.Equal(Path.Combine(_f.Paths.Outbox, id + ".pdf"), item.PdfPath);
        Assert.Equal(Path.Combine(_f.Paths.Outbox, id + ".json"), item.JsonPath);
    }

    [Fact]
    public void Sidecar_uses_the_camelCase_names_and_iso_timestamps_of_the_powershell_product()
    {
        var id = _f.AddItem();
        var json = File.ReadAllText(Path.Combine(_f.Paths.Outbox, id + ".json"));
        Assert.Contains("\"sizeBytes\": 40", json);
        Assert.Contains("\"jobId\": \"3\"", json);
        Assert.Contains("\"printed\": \"2026-03-01T09:00:00", json);
        Assert.Contains("\"nextAttempt\": null", json);
        Assert.False(File.Exists(Path.Combine(_f.Paths.Outbox, id + ".json.tmp")));
    }

    [Fact]
    public void Reads_a_powershell_written_sidecar_with_missing_fields()
    {
        // PowerShell 5.1 style: BOM, local offsets, and only some of the fields.
        const string id = "abc123";
        OutboxFixture.WritePdf(Path.Combine(_f.Paths.Outbox, id + ".pdf"));
        File.WriteAllText(Path.Combine(_f.Paths.Outbox, id + ".json"),
            "{\n  \"profile\": \"Send to Odoo\",\n  \"title\": \"Old doc\",\n  \"attempts\": 2,\n  \"nextAttempt\": \"2026-03-01T11:30:00.0000000+02:00\"\n}",
            new System.Text.UTF8Encoding(true));

        var item = Assert.Single(_f.Store.List(_f.Paths.Outbox));
        Assert.Equal(id, item.Meta.Id);
        Assert.Equal("Old doc", item.Meta.Title);
        Assert.Equal("", item.Meta.User);
        Assert.Equal(0, item.Meta.Pages);
        Assert.Equal(2, item.Meta.Attempts);
        Assert.Null(item.Meta.Computer);
        Assert.Null(item.Meta.Printed);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 9, 30, 0, TimeSpan.Zero), item.Meta.NextAttempt!.Value.ToUniversalTime());
    }

    [Fact]
    public void Skips_and_reports_unreadable_sidecars()
    {
        _f.AddItem();
        File.WriteAllText(Path.Combine(_f.Paths.Outbox, "broken.json"), "{ not json");
        Assert.Single(_f.Store.List(_f.Paths.Outbox));
        Assert.Contains(_f.Log.Entries, e => e.Level == ErpLogLevel.Warning && e.Message.StartsWith("Unreadable sidecar ") && e.Message.Contains("broken.json"));
    }

    [Fact]
    public void Lists_items_oldest_first()
    {
        var newer = _f.AddItem("b-newer");
        var older = _f.AddItem("a-older");
        File.SetLastWriteTimeUtc(Path.Combine(_f.Paths.Outbox, newer + ".json"), _f.Clock.Now.UtcDateTime);
        File.SetLastWriteTimeUtc(Path.Combine(_f.Paths.Outbox, older + ".json"), _f.Clock.Now.UtcDateTime.AddMinutes(-5));
        Assert.Equal([older, newer], _f.Store.List(_f.Paths.Outbox).Select(i => i.Meta.Id));
    }

    [Fact]
    public void Skips_items_whose_retry_time_has_not_come()
    {
        var id = _f.AddItem();
        var meta = _f.ReadMeta(_f.Paths.Outbox, id);
        meta.NextAttempt = _f.Clock.GetLocalNow().AddMinutes(10);
        _f.Store.SaveMeta(_f.Paths.Outbox, meta);

        Assert.Empty(_f.Store.List(_f.Paths.Outbox, dueOnly: true, now: _f.Clock.GetLocalNow()));
        Assert.Single(_f.Store.List(_f.Paths.Outbox, dueOnly: true, now: _f.Clock.GetLocalNow().AddMinutes(11)));
        Assert.Single(_f.Store.List(_f.Paths.Outbox));
    }

    [Fact]
    public void Adopts_orphaned_PDFs_older_than_five_minutes()
    {
        var lost = Path.Combine(_f.Paths.Outbox, "lost.pdf");
        var fresh = Path.Combine(_f.Paths.Outbox, "fresh.pdf");
        OutboxFixture.WritePdf(lost);
        OutboxFixture.WritePdf(fresh);
        File.SetLastWriteTimeUtc(lost, _f.Clock.Now.UtcDateTime.AddMinutes(-10));
        File.SetLastWriteTimeUtc(fresh, _f.Clock.Now.UtcDateTime);

        _f.Store.RepairOrphans(_f.Paths.Outbox, OutboxFixture.ProfileName);

        Assert.True(File.Exists(Path.Combine(_f.Paths.Outbox, "lost.json")));
        Assert.False(File.Exists(Path.Combine(_f.Paths.Outbox, "fresh.json")));
        var meta = _f.ReadMeta(_f.Paths.Outbox, "lost");
        Assert.Equal("Recovered print job", meta.Title);
        Assert.Equal(OutboxFixture.ProfileName, meta.Profile);
        Assert.Equal(new FileInfo(lost).Length, meta.SizeBytes);
        Assert.Contains(_f.Log.Entries, e => e.Level == ErpLogLevel.Warning && e.Message == "Recovered orphaned print job lost.pdf in profile 'Send to Odoo' (user and title unknown).");
    }

    [Fact]
    public void Does_not_touch_a_pdf_that_already_has_a_sidecar()
    {
        var id = _f.AddItem();
        File.SetLastWriteTimeUtc(Path.Combine(_f.Paths.Outbox, id + ".pdf"), _f.Clock.Now.UtcDateTime.AddHours(-1));
        var before = File.ReadAllText(Path.Combine(_f.Paths.Outbox, id + ".json"));
        _f.Store.RepairOrphans(_f.Paths.Outbox, OutboxFixture.ProfileName);
        Assert.Equal(before, File.ReadAllText(Path.Combine(_f.Paths.Outbox, id + ".json")));
    }

    [Fact]
    public void Deletes_stale_temp_files_but_not_recent_ones()
    {
        var stale = Path.Combine(_f.Paths.Outbox, "old.pdf.tmp");
        var recent = Path.Combine(_f.Paths.Outbox, "new.pdf.tmp");
        File.WriteAllText(stale, "x");
        File.WriteAllText(recent, "x");
        File.SetLastWriteTimeUtc(stale, _f.Clock.Now.UtcDateTime.AddMinutes(-90));
        File.SetLastWriteTimeUtc(recent, _f.Clock.Now.UtcDateTime.AddMinutes(-30));
        _f.Store.RepairOrphans(_f.Paths.Outbox, OutboxFixture.ProfileName);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void Move_carries_the_pdf_and_rewrites_the_sidecar_last()
    {
        var id = _f.AddItem();
        var item = _f.GetItem(id);
        item.Meta.LastError = "boom";
        _f.Store.Move(item, _f.Paths.Failed);

        Assert.Equal(0, OutboxFixture.Count(_f.Paths.Outbox));
        Assert.True(File.Exists(Path.Combine(_f.Paths.Failed, id + ".pdf")));
        Assert.Equal("boom", _f.ReadMeta(_f.Paths.Failed, id).LastError);
    }

    [Fact]
    public void Remove_deletes_both_files_and_tolerates_missing_ones()
    {
        var id = _f.AddItem();
        var item = _f.GetItem(id);
        File.Delete(item.PdfPath);
        _f.Store.Remove(item);
        Assert.Equal(0, OutboxFixture.Count(_f.Paths.Outbox));
    }

    [Fact]
    public void Delivery_stats_are_zeroed_when_missing_or_unreadable()
    {
        Assert.Equal(0, _f.Store.GetStats(_f.Paths).Count);
        File.WriteAllText(_f.Paths.Stats, "garbage");
        Assert.Equal(0, _f.Store.GetStats(_f.Paths).Count);
        Assert.Contains(_f.Log.Entries, e => e.Level == ErpLogLevel.Warning && e.Message.StartsWith("Unreadable delivery stats "));
    }

    [Fact]
    public void Reads_delivery_stats_written_by_powershell()
    {
        File.WriteAllText(_f.Paths.Stats, "{\n  \"count\": 4,\n  \"lastSentAt\": \"2026-02-28T10:00:00.0000000+00:00\",\n  \"lastTitle\": \"X\",\n  \"lastRemoteId\": \"9\"\n}");
        var stats = _f.Store.GetStats(_f.Paths);
        Assert.Equal(4, stats.Count);
        Assert.Equal("X", stats.LastTitle);
        Assert.Equal("9", stats.LastRemoteId);
        Assert.NotNull(stats.LastSentAt);
    }

    [Fact]
    public void Adding_a_delivery_stat_never_throws()
    {
        Directory.CreateDirectory(_f.Paths.Stats); // a directory where the file should be
        var meta = OutboxStore.NewMeta("x", "p");
        _f.Store.AddDeliveryStat(_f.Paths, meta);
        Assert.Contains(_f.Log.Entries, e => e.Level == ErpLogLevel.Warning && e.Message.StartsWith("Could not update delivery stats: "));
    }
}

public class OutboxRetryPolicyTests
{
    [Fact]
    public void Backs_off_exponentially_up_to_the_cap()
    {
        Assert.Equal([30, 60, 120, 240, 480, 960, 1920, 3600], Enumerable.Range(1, 8).Select(a => RetryPolicy.DelaySeconds(a, 60)));
        Assert.Equal(300, RetryPolicy.DelaySeconds(500, 5));
    }

    [Fact]
    public void Treats_attempt_zero_like_the_first_attempt()
    {
        Assert.Equal(30, RetryPolicy.DelaySeconds(0, 60));
    }
}
