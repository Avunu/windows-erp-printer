using ErpPrinter.Diagnostics;
using ErpPrinter.Printing;

namespace ErpPrinter.Core.Tests.Diagnostics;

/// <summary>Port of JobAnalysis.Tests.ps1.</summary>
public class JobAnalysisTests
{
    private static SpoolerStageVerdict Stage(int[] ids, string code = "") => SpoolerStageVerdict.Evaluate(ids, code);

    [Fact]
    public void A_job_that_was_written_to_the_port_is_printed()
    {
        var v = Stage([800, 801, 842, 805, 307], "0x0");
        Assert.Equal(SpoolerStage.Printed, v.Stage);
        Assert.False(v.Failed);
    }

    [Fact]
    public void Blames_the_conversion_not_the_pipe_when_printing_started_but_the_print_processor_never_finished()
    {
        var v = Stage([800, 801]);
        Assert.Equal(SpoolerStage.Printing, v.Stage);
        Assert.True(v.Failed);
        Assert.Contains("before the ERP Printer pipe was involved", v.Summary);
        Assert.Contains("Microsoft Print to PDF", v.Hint);
        Assert.Contains("Ctrl+Shift+P", v.Hint);
    }

    [Fact]
    public void Reports_a_print_processor_error_code()
    {
        var v = Stage([800, 801, 842], "0x5");
        Assert.Equal(SpoolerStage.ProcessorFailed, v.Stage);
        Assert.Contains("code 0x5", v.Summary);
    }

    [Theory]
    [InlineData("0x0")]
    [InlineData("0x00000000")]
    [InlineData("")]
    public void A_zero_processor_code_is_not_an_error(string code)
    {
        Assert.NotEqual(SpoolerStage.ProcessorFailed, Stage([800, 801, 842], code).Stage);
    }

    [Fact]
    public void Points_at_the_pipe_when_the_job_was_rendered_but_never_written_to_the_port()
    {
        var v = Stage([800, 801, 842, 805], "0x0");
        Assert.Equal(SpoolerStage.Rendered, v.Stage);
        Assert.Contains("pipe", v.Hint);
    }

    [Fact]
    public void Reports_a_job_that_was_never_started()
    {
        var v = Stage([800]);
        Assert.Equal(SpoolerStage.Spooled, v.Stage);
        Assert.True(v.Failed);
    }

    [Fact]
    public void Admits_when_the_log_has_nothing()
    {
        var v = Stage([]);
        Assert.Equal(SpoolerStage.Unknown, v.Stage);
        Assert.False(v.Failed);
    }

    [Fact]
    public async Task Reproduces_the_diagnosis_of_a_job_that_died_in_the_print_processor()
    {
        // A Microsoft Edge XPS job that died while the driver converted it to PDF (field report, 2026-10-20).
        var t = new DateTimeOffset(2026, 10, 20, 17, 27, 35, TimeSpan.Zero);
        var log = new DiagPrintServiceLog();
        log.Operational.Add(DiagPrintServiceLog.Event(307, @"Document 3, Print Document owned by jdoe on \\PC was printed on Send to Odoo through port \\.\pipe\x. Size in bytes: 92349.", t));
        log.Operational.Add(DiagPrintServiceLog.Event(800, "Spooling job 4.", t));
        log.Operational.Add(DiagPrintServiceLog.Event(801, "Printing job 4.", t.AddSeconds(5)));
        var job = new PrintJobInfo(4, "Send to Odoo", "Wikipedia, the free encyclopedia", "jdoe",
            JobStatusFlags.Error | JobStatusFlags.Printing | JobStatusFlags.Retained, 4, 0, 1002815, t, "RAW");

        var analysis = await new JobAnalyzer(log, new DiagSpoolFiles("TYPE_XPS_OPEN")).AnalyzeAsync(job);

        Assert.Equal(SpoolerStage.Printing, analysis.Stage);
        Assert.True(analysis.Failed);
        Assert.Contains("job 4 'Wikipedia, the free encyclopedia' (979 KB, 4 page(s)", analysis.Detail);
        Assert.Contains("status Error, Printing, Retained", analysis.Detail);
        Assert.Contains("datatype RAW", analysis.Detail);
        Assert.Contains("spool file TYPE_XPS_OPEN", analysis.Detail);
        Assert.Contains("17:27:35 spooled -> 17:27:40 printing started", analysis.Detail);
        // Job 3's events belong to another job and must not leak into this timeline.
        Assert.DoesNotContain("written to port", analysis.Detail);
        Assert.Contains("before the ERP Printer pipe was involved", analysis.Detail);
        Assert.Contains("Microsoft Print to PDF", analysis.Hint);
    }

    [Fact]
    public async Task Shows_the_print_processor_code_in_the_timeline()
    {
        var t = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        var log = new DiagPrintServiceLog();
        log.Operational.Add(DiagPrintServiceLog.Event(800, "Spooling job 7.", t));
        log.Operational.Add(DiagPrintServiceLog.Event(801, "Printing job 7.", t.AddSeconds(1)));
        log.Operational.Add(DiagPrintServiceLog.Event(842, "7 was sent through the print processor winprint with error 0x00000005.", t.AddSeconds(2)));
        var job = DiagData.Job(7, "Report", JobStatusFlags.Error, t);

        var analysis = await new JobAnalyzer(log, new DiagSpoolFiles()).AnalyzeAsync(job);

        Assert.Equal(SpoolerStage.ProcessorFailed, analysis.Stage);
        Assert.Contains("09:00:02 print processor finished (0x00000005)", analysis.Detail);
        Assert.DoesNotContain("spool file", analysis.Detail);
        Assert.DoesNotContain("datatype", analysis.Detail);
    }

    [Fact]
    public async Task Says_so_when_the_spooler_logged_nothing()
    {
        var analysis = await new JobAnalyzer(new DiagPrintServiceLog(), new DiagSpoolFiles())
            .AnalyzeAsync(DiagData.Job(9, "Report", JobStatusFlags.None, new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero)));
        Assert.Equal(SpoolerStage.Unknown, analysis.Stage);
        Assert.False(analysis.Failed);
        Assert.Contains("Spooler timeline: no spooler events", analysis.Detail);
    }

    [Fact]
    public async Task Looks_a_day_back_when_the_job_has_no_submission_time()
    {
        var clock = new DiagClock();
        var log = new DiagPrintServiceLog();
        log.Operational.Add(DiagPrintServiceLog.Event(800, "Spooling job 2.", clock.GetUtcNow().AddHours(-20)));
        log.Operational.Add(DiagPrintServiceLog.Event(800, "Spooling job 2.", clock.GetUtcNow().AddDays(-3)));

        var analysis = await new JobAnalyzer(log, new DiagSpoolFiles(), clock).AnalyzeAsync(DiagData.Job(2, "Old", JobStatusFlags.None));

        Assert.Equal(SpoolerStage.Spooled, analysis.Stage);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(analysis.Detail, "spooled"));
    }
}
