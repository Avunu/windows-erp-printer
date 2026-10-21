using System.Text.Json;
using ErpPrinter.Diagnostics;
using ErpPrinter.Logging;
using ErpPrinter.Outbox;

namespace ErpPrinter.Core.Tests.Diagnostics;

/// <summary>The log-file reader (port of Get-ErpRecentLogEntries) and the outbox scanner (port of Find-ErpTestPrintItem).</summary>
public class LogAndOutboxReaderTests : IDisposable
{
    private readonly DiagTempDir _dir = new();
    private readonly DiagClock _clock = new();

    public void Dispose() => _dir.Dispose();

    private static string Line(string time, string level, string component, string message) =>
        $"{time} [{level,-11}] [{component}] {message}";

    private void WriteLog(string name, params string[] lines) => File.WriteAllLines(Path.Combine(_dir.Path, name), lines);

    private LogFileReader Reader(string? sub = null) => new(sub is null ? _dir.Path : Path.Combine(_dir.Path, sub), _clock);

    [Fact]
    public async Task Parses_recent_entries_back_filtered_by_level_and_component()
    {
        WriteLog("listener-20261020.log",
            Line("2026-10-20 16:00:00.000", "Information", "listener", "Listener started (version 1, PID 2)."),
            "a continuation line that is not a log line",
            Line("2026-10-20 16:00:05.250", "Warning", "listener", "Discarded an empty job."));
        WriteLog("uploader-20261020.log", Line("2026-10-20 16:30:00.000", "Information", "uploader", "Delivered x."));

        var all = await Reader().ReadRecentAsync(1 + 24);
        Assert.Equal(3, all.Count);
        Assert.StartsWith("Listener started", all[0].Message);
        Assert.Equal("listener", all[0].Component);
        Assert.Equal(ErpLogLevel.Information, all[0].Level);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 16, 0, 5, 250, TimeSpan.Zero), all[1].Time);

        var warnings = await Reader().ReadRecentAsync(24, [ErpLogLevel.Warning]);
        Assert.Equal("Discarded an empty job.", Assert.Single(warnings).Message);

        var uploader = await Reader().ReadRecentAsync(24, null, "uploader");
        Assert.Equal("Delivered x.", Assert.Single(uploader).Message);
    }

    [Fact]
    public async Task Returns_nothing_for_a_missing_folder()
    {
        Assert.Empty(await Reader("nope").ReadRecentAsync(24));
    }

    [Fact]
    public async Task Drops_entries_older_than_the_window()
    {
        WriteLog("listener-20261020.log",
            Line("2026-10-20 10:00:00.000", "Information", "listener", "morning"),
            Line("2026-10-20 17:00:00.000", "Information", "listener", "recent"));

        var entries = await Reader().ReadRecentAsync(1);

        Assert.Equal("recent", Assert.Single(entries).Message);
    }

    [Fact]
    public async Task Reads_a_log_file_the_writer_still_holds_open()
    {
        var path = Path.Combine(_dir.Path, "listener-20261020.log");
        await using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Line("2026-10-20 17:00:00.000", "Information", "listener", "still open") + "\n"));
        await writer.FlushAsync();

        var entries = await Reader().ReadRecentAsync(24);

        Assert.Equal("still open", Assert.Single(entries).Message);
    }

    // --- Outbox -------------------------------------------------------------------------------

    private ProfileFolders Folders => ProfileFolders.For(_dir.Path, "Send to Odoo");

    private static void Sidecar(string folder, string id, string title, string? lastError = null)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, id + ".json"), JsonSerializer.Serialize(new OutboxMeta { Id = id, Title = title, LastError = lastError }));
    }

    [Fact]
    public void Builds_the_profile_folders_from_the_safe_name()
    {
        var folders = ProfileFolders.For(@"C:\ProgramData\ErpPrinter", "Send to Odoo");
        Assert.Equal(Path.Combine(@"C:\ProgramData\ErpPrinter", "spool", "Send_to_Odoo", "outbox"), folders.Outbox);
        Assert.EndsWith("delivered.json", folders.Stats);
    }

    [Fact]
    public async Task Finds_the_test_page_by_its_token_in_any_folder_and_ignores_known_documents()
    {
        Sidecar(Folders.Outbox, "old", "Invoice ERP Printer test page [abcd1234]");
        Sidecar(Folders.Sent, "mine", "ERP Printer test page [abcd1234]");
        Sidecar(Folders.Outbox, "other", "Something else");
        var scanner = new FileSystemOutboxScanner();
        var known = new HashSet<string> { "old" };

        var item = await scanner.FindTestPageAsync(Folders, "abcd1234", known);

        Assert.NotNull(item);
        Assert.Equal("mine", item.Id);
        Assert.Equal("sent", item.Folder);
    }

    [Fact]
    public async Task Accepts_a_single_new_untitled_document()
    {
        Sidecar(Folders.Outbox, "untitled", "");
        var scanner = new FileSystemOutboxScanner();

        var item = await scanner.FindTestPageAsync(Folders, "abcd1234", new HashSet<string>());

        Assert.Equal("untitled", item?.Id);
    }

    [Fact]
    public async Task Refuses_to_guess_between_several_untitled_documents()
    {
        Sidecar(Folders.Outbox, "u1", "");
        Sidecar(Folders.Failed, "u2", "");

        Assert.Null(await new FileSystemOutboxScanner().FindTestPageAsync(Folders, "abcd1234", new HashSet<string>()));
    }

    [Fact]
    public async Task Collects_the_known_ids_of_all_three_folders()
    {
        Sidecar(Folders.Outbox, "a", "A");
        Sidecar(Folders.Failed, "b", "B");
        Sidecar(Folders.Sent, "c", "C");

        var known = await new FileSystemOutboxScanner().GetKnownIdsAsync(Folders);

        Assert.Equal(["a", "b", "c"], known.Order());
    }

    [Fact]
    public async Task Tolerates_a_sidecar_with_a_null_title()
    {
        Directory.CreateDirectory(Folders.Outbox);
        await File.WriteAllTextAsync(Path.Combine(Folders.Outbox, "n.json"), """{"id":"n","title":null}""");

        var item = await new FileSystemOutboxScanner().FindTestPageAsync(Folders, "abcd1234", new HashSet<string>());

        Assert.Equal("n", item?.Id);
    }
}
