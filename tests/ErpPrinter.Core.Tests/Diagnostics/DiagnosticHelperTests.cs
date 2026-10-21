using ErpPrinter.Diagnostics;
using ErpPrinter.Printing;

namespace ErpPrinter.Core.Tests.Diagnostics;

/// <summary>Port of the 'Diagnostic helpers' and 'Report ordering' cases in Diagnostics.Tests.ps1, plus the printer state table.</summary>
public class DiagnosticHelperTests
{
    private static readonly DateTimeOffset Generated = new(2026, 10, 20, 17, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_failing_check_becomes_a_warning_instead_of_aborting()
    {
        var results = await DiagnosticStep.RunAsync("C", "N", _ => throw new InvalidOperationException("boom"));
        var r = Assert.Single(results);
        Assert.Equal(DiagnosticStatus.Warn, r.Status);
        Assert.Contains("boom", r.Detail);
    }

    [Fact]
    public async Task Results_added_before_the_failure_are_kept()
    {
        var results = await DiagnosticStep.RunAsync("C", "N", r =>
        {
            r.Add(DiagnosticResult.Pass("C", "first", "fine"));
            throw new InvalidOperationException("boom");
        });
        Assert.Equal(["first", "N"], results.Select(r => r.Name));
    }

    [Fact]
    public async Task Check_results_pass_through_unchanged()
    {
        var results = await DiagnosticStep.RunAsync("C", "N", r =>
        {
            r.Add(DiagnosticResult.Pass("C", "N", "fine"));
            return Task.CompletedTask;
        });
        Assert.Equal(DiagnosticStatus.Pass, Assert.Single(results).Status);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() => DiagnosticStep.RunAsync("C", "N", _ => throw new OperationCanceledException()));
    }

    [Fact]
    public void Translates_legacy_task_results()
    {
        Assert.Contains("successfully", LegacyTaskResult.Describe(0));
        Assert.StartsWith("0x00041301", LegacyTaskResult.Describe(267009));
        Assert.Contains("running", LegacyTaskResult.Describe(267009));
        Assert.StartsWith("0x80070002", LegacyTaskResult.Describe(-2147024894));
        Assert.Contains("file not found", LegacyTaskResult.Describe(-2147024894));
        Assert.Contains("unrecognized", LegacyTaskResult.Describe(12345));
    }

    [Theory]
    [InlineData("Error, Offline", true)]
    [InlineData("UserIntervention", true)]
    [InlineData("Printing", false)]
    [InlineData("Spooling", false)]
    [InlineData("", false)]
    public void Recognises_job_states_that_will_not_move_on_their_own(string status, bool bad)
    {
        Assert.Equal(bad, PrintJobText.IsBadStatus(status));
    }

    [Fact]
    public void Recognises_bad_job_status_flags()
    {
        Assert.True(PrintJobText.IsBadStatus(JobStatusFlags.Error | JobStatusFlags.Printing | JobStatusFlags.Retained));
        Assert.True(PrintJobText.IsBadStatus(JobStatusFlags.Restart));
        Assert.False(PrintJobText.IsBadStatus(JobStatusFlags.Spooling));
        Assert.False(PrintJobText.IsBadStatus(JobStatusFlags.None));
    }

    [Fact]
    public void Treats_a_missing_folder_as_readable_so_other_checks_can_report_it()
    {
        Assert.True(FolderAccess.IsReadable(Path.Combine(Path.GetTempPath(), "erp-diag-nothing-here-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void Formats_an_event_line_with_collapsed_whitespace_and_a_length_limit()
    {
        var ev = new PrintServiceEvent(372, 3, "Warning", new DateTimeOffset(2026, 10, 20, 17, 27, 35, TimeSpan.Zero), "Document   3\r\nfailed " + new string('x', 400));
        var line = PrintServiceEventFormatter.Format(ev);
        Assert.StartsWith("2026-10-20 17:27:35 [Warning] id 372: Document 3 failed xxx", line);
        Assert.EndsWith("...", line);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void Renders_a_report_with_the_first_failure_called_out()
    {
        var results = new[]
        {
            DiagnosticResult.Pass("Tasks", "Listener task", "Running"),
            DiagnosticResult.Fail("Tasks", "Uploader task", "State Ready\nlast result 0x1", "Start it"),
            DiagnosticResult.Warn("Printing", "Spooler", "slow", "Look at it"),
        };
        var text = DiagnosticReportFormatter.Format(results, Generated, "PC1");
        Assert.Contains("1 fail, 1 warn, 1 pass, 0 info", text);
        Assert.Contains("Generated 2026-10-20 17:30:00 on PC1", text);
        Assert.Contains("Start with: Tasks / Uploader task", text);
        Assert.Contains("[FAIL] Uploader task: State Ready", text);
        Assert.Contains("       last result 0x1", text);
        Assert.Contains("-> Start it", text);
        Assert.Contains("== Printing ==", text);
        Assert.EndsWith(Environment.NewLine, text);
    }

    [Fact]
    public void Does_not_print_hints_of_passing_or_informational_results()
    {
        var text = DiagnosticReportFormatter.Format([new DiagnosticResult("C", "N", DiagnosticStatus.Info, "d", "hidden hint")], Generated, "PC1");
        Assert.DoesNotContain("hidden hint", text);
    }

    [Fact]
    public void Leads_with_the_job_analysis_rather_than_the_printer_state_it_caused()
    {
        var results = new[]
        {
            DiagnosticResult.Fail("Profile", "Printer state", "The spooler reports the printer as Error.", "Check the printer."),
            DiagnosticResult.Fail("Profile", "Print queue", "1 job", "See below."),
            DiagnosticResult.Fail("Profile", "Job 4 analysis", "died while converting", "Print to the built-in PDF printer."),
        };
        var text = DiagnosticReportFormatter.Format(results, Generated, "PC1");
        Assert.Contains("Start with: Profile / Job 4 analysis", text);
        Assert.Contains("Print to the built-in PDF printer.", text);
    }

    [Fact]
    public async Task Saves_the_report_next_to_the_logs()
    {
        using var dir = new DiagTempDir();
        var path = await DiagnosticReportFormatter.SaveAsync("report text", dir.Path, Generated);
        Assert.Equal(Path.Combine(dir.Path, "logs", "diagnostics-20261020-173000.log"), path);
        Assert.Equal("report text", await File.ReadAllTextAsync(path!));
    }

    // --- Printer state ------------------------------------------------------------------------

    private static PrinterStateInfo State(PrinterInfo printer, int hour = 12) => PrinterStateAnalyzer.Analyze(printer, TimeSpan.FromHours(hour));

    [Fact]
    public void A_normal_printer_can_print()
    {
        var s = State(DiagData.Printer());
        Assert.Null(s.Problem);
        Assert.Equal("Normal", s.Summary);
    }

    [Fact]
    public void A_printer_that_is_printing_is_fine()
    {
        var s = State(DiagData.Printer(status: PrinterStatusFlags.Printing));
        Assert.Null(s.Problem);
        Assert.Equal("Printing", s.Summary);
    }

    [Fact]
    public void Use_printer_offline_holds_every_job()
    {
        var s = State(DiagData.Printer(workOffline: true));
        Assert.Contains("Use Printer Offline", s.Problem);
        Assert.Contains("untick", s.Hint);
    }

    [Fact]
    public void A_paused_printer_is_reported_with_the_generic_hint()
    {
        var s = State(DiagData.Printer(status: PrinterStatusFlags.Paused));
        Assert.Equal("The spooler reports the printer as Paused.", s.Problem);
        Assert.Contains("Pause Printing", s.Hint);
    }

    [Fact]
    public void An_error_state_points_at_the_failed_job()
    {
        var s = State(DiagData.Printer(status: PrinterStatusFlags.Error));
        Assert.Equal("The spooler reports the printer as Error.", s.Problem);
        Assert.Contains("failed job in the queue", s.Hint);
    }

    [Theory]
    [InlineData(9, true)]
    [InlineData(17, false)]
    [InlineData(8, false)]
    [InlineData(20, false)]
    public void Honours_a_daytime_availability_window(int hour, bool inside)
    {
        var printer = DiagData.Printer() with { AvailableFromMinutes = 9 * 60, AvailableUntilMinutes = 17 * 60 };
        var s = State(printer, hour);
        Assert.Equal(inside, s.Problem is null);
        Assert.Contains("(available 09:00-17:00)", s.Summary);
        if (!inside)
        {
            Assert.Contains("only available between 09:00 and 17:00", s.Problem);
            Assert.Contains("Always available", s.Hint);
        }
    }

    [Theory]
    [InlineData(23, true)]
    [InlineData(3, true)]
    [InlineData(12, false)]
    public void Honours_a_window_that_spans_midnight(int hour, bool inside)
    {
        var printer = DiagData.Printer() with { AvailableFromMinutes = 22 * 60, AvailableUntilMinutes = 6 * 60 };
        Assert.Equal(inside, State(printer, hour).Problem is null);
    }

    [Fact]
    public void Equal_window_bounds_mean_always_available()
    {
        var printer = DiagData.Printer() with { AvailableFromMinutes = 0, AvailableUntilMinutes = 0 };
        var s = State(printer);
        Assert.Null(s.Problem);
        Assert.Equal("Normal", s.Summary);
    }

    [Fact]
    public void Formats_a_job_list()
    {
        var submitted = new DateTimeOffset(2026, 10, 20, 17, 0, 0, TimeSpan.Zero);
        var text = PrintJobText.FormatList([DiagData.Job(150, "Old", JobStatusFlags.Error, submitted), DiagData.Job(151, "New", JobStatusFlags.None)]);
        Assert.Equal("job 150 'Old' (Error, submitted 2026-10-20 17:00:00); job 151 'New' (Normal, submitted unknown)", text);
    }
}
