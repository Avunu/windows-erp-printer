using ErpPrinter.Diagnostics;

namespace ErpPrinter.Core.Tests.Diagnostics;

/// <summary>Port of the 'Test print verdict' cases in Diagnostics.Tests.ps1.</summary>
public class TestPrintVerdictTests
{
    private static TestPrintVerdict Verdict(TestPrintEvidence evidence) => TestPrintVerdict.Evaluate(evidence);

    [Fact]
    public void Reports_success_once_the_page_is_delivered()
    {
        var v = Verdict(new() { Submitted = true, Received = true, Delivered = true });
        Assert.True(v.Success);
        Assert.Equal(TestPrintStage.Delivered, v.Stage);
    }

    [Fact]
    public void Blames_the_ERP_when_the_document_was_received_but_rejected()
    {
        var v = Verdict(new() { Submitted = true, Received = true, Failed = true, FailedError = "HTTP 400: bad field" });
        Assert.False(v.Success);
        Assert.Equal(TestPrintStage.Received, v.Stage);
        Assert.Contains("HTTP 400: bad field", v.Summary);
    }

    [Fact]
    public void Says_the_printing_side_works_when_only_the_upload_is_missing()
    {
        var v = Verdict(new() { Submitted = true, Received = true, LastDeliveryError = "Could not reach https://erp" });
        Assert.Equal(TestPrintStage.Received, v.Stage);
        Assert.Contains("Could not reach https://erp", v.Summary);
        Assert.Contains("printing side works", v.Hint);
    }

    [Fact]
    public void Reports_a_job_stuck_in_the_queue_naming_the_missing_pipe()
    {
        var v = Verdict(new() { Submitted = true, JobSeen = true, JobStuck = true, LastJobStatus = "Error, Offline", PipeExists = false, ExpectedPort = @"\\.\pipe\x" });
        Assert.Equal(TestPrintStage.InQueue, v.Stage);
        Assert.Contains("stuck as 'Error, Offline'", v.Summary);
        Assert.Contains(@"Nothing is hosting \\.\pipe\x", v.Summary);
    }

    [Fact]
    public void Explains_a_job_that_never_leaves_Normal_because_an_older_job_is_ahead_of_it()
    {
        var v = Verdict(new()
        {
            Submitted = true, JobSeen = true, JobStuck = true, LastJobStatus = "Normal", JobEverPrinting = false, PipeExists = true,
            JobsAhead = 1, JobsAheadText = "job 150 'Old' (Error)",
        });
        Assert.Equal(TestPrintStage.InQueue, v.Stage);
        Assert.Contains("stuck as 'Normal'", v.Summary);
        Assert.Contains("1 job(s) were already in the queue (job 150 'Old' (Error))", v.Summary);
        Assert.Contains("Remove-PrintJob", v.Hint);
    }

    [Fact]
    public void Blames_a_printer_that_is_paused_or_offline_before_anything_else()
    {
        var v = Verdict(new()
        {
            Submitted = true, JobSeen = true, JobStuck = true, LastJobStatus = "Normal", JobEverPrinting = false, JobsAhead = 2, JobsAheadText = "x",
            PrinterProblem = "The spooler reports the printer as Paused.", PrinterHint = "Resume it.",
        });
        Assert.Contains("printer is not ready: The spooler reports the printer as Paused.", v.Summary);
        Assert.Equal("Resume it.", v.Hint);
    }

    [Fact]
    public void Says_the_pipe_is_not_involved_when_the_spooler_never_starts_printing_and_nothing_else_explains_it()
    {
        var v = Verdict(new() { Submitted = true, JobSeen = true, JobStuck = true, LastJobStatus = "Normal", JobEverPrinting = false, PipeExists = true, JobsAhead = 0 });
        Assert.Matches("never reached 'Printing'.*never tried to open the pipe", v.Summary);
        Assert.Contains("Restart-Service Spooler", v.Hint);
    }

    [Fact]
    public void Catches_a_spooler_that_printed_to_a_different_port()
    {
        var v = Verdict(new() { Submitted = true, SpoolerPrinted = true, SpoolerPort = "PORTPROMPT:", ExpectedPort = @"\\.\pipe\x" });
        Assert.Equal(TestPrintStage.Spooled, v.Stage);
        Assert.Contains("printed to 'PORTPROMPT:'", v.Summary);
        Assert.Contains("not the listener's pipe", v.Summary);
    }

    [Fact]
    public void Notes_when_the_spooler_printed_to_the_right_port_but_the_listener_saw_nothing()
    {
        var v = Verdict(new()
        {
            Submitted = true, SpoolerPrinted = true, SpoolerPort = @"\\.\pipe\x", ExpectedPort = @"\\.\pipe\x", PipeExists = true, ListenerConnected = false,
        });
        Assert.Equal(TestPrintStage.Spooled, v.Stage);
        Assert.DoesNotContain("not the listener's pipe", v.Summary);
        Assert.Contains("no connection from the spooler", v.Summary);
    }

    [Fact]
    public void Surfaces_a_listener_side_failure()
    {
        var v = Verdict(new() { Submitted = true, JobGone = true, ListenerConnected = true, ListenerError = "Discarded an empty job" });
        Assert.Contains("Discarded an empty job", v.Summary);
    }

    [Fact]
    public void Explains_a_job_that_vanished_without_a_trace()
    {
        var v = Verdict(new() { Submitted = true, JobSeen = true, JobGone = true });
        Assert.Equal(TestPrintStage.Spooled, v.Stage);
        Assert.Contains("PrintService Operational", v.Hint);
    }

    [Fact]
    public void Reports_a_page_that_never_reached_the_queue()
    {
        var v = Verdict(new() { Submitted = true });
        Assert.Equal(TestPrintStage.Submitted, v.Stage);
        Assert.False(v.Success);
    }

    [Fact]
    public void Reports_a_page_that_could_not_be_submitted()
    {
        Assert.Equal(TestPrintStage.None, Verdict(new() { Submitted = false }).Stage);
    }

    [Fact]
    public void Reports_a_job_that_is_in_the_queue_but_not_moving()
    {
        var v = Verdict(new() { Submitted = true, JobSeen = true, LastJobStatus = "Spooling" });
        Assert.Equal(TestPrintStage.InQueue, v.Stage);
        Assert.Contains("status 'Spooling'", v.Summary);
    }

    [Fact]
    public void Notes_that_nothing_hosts_the_pipe_when_the_spooler_says_it_printed()
    {
        var v = Verdict(new() { Submitted = true, SpoolerPrinted = true, PipeExists = false });
        Assert.Contains("No process is hosting the pipe", v.Summary);
    }
}
