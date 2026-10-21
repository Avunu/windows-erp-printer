namespace ErpPrinter.Diagnostics;

/// <summary>How far a test page got, in order.</summary>
public enum TestPrintStage
{
    /// <summary>The page could not be sent to the Windows printer.</summary>
    None,

    /// <summary>Sent to the spooler but never listed in the queue.</summary>
    Submitted,

    /// <summary>In the queue, never handed to the port.</summary>
    InQueue,

    /// <summary>The spooler handled it (or says it did) but the listener did not receive it.</summary>
    Spooled,

    /// <summary>The listener has it; the ERP does not.</summary>
    Received,

    /// <summary>In the ERP.</summary>
    Delivered,
}

/// <summary>
/// What was observed while a test page travelled through the pipeline. Fields are optional because the run may end early; an unset
/// (null) value means "not observed", which differs from an observed false for <see cref="PipeExists"/>, <see cref="ListenerConnected"/>
/// and <see cref="JobEverPrinting"/>.
/// </summary>
public sealed class TestPrintEvidence
{
    public bool Submitted { get; set; }
    public bool JobSeen { get; set; }
    public string LastJobStatus { get; set; } = "";
    public bool JobStuck { get; set; }
    public bool JobGone { get; set; }
    public bool SpoolerPrinted { get; set; }
    public string? SpoolerPort { get; set; }
    public string? ExpectedPort { get; set; }
    public bool? PipeExists { get; set; }
    public bool? ListenerConnected { get; set; }
    public string? ListenerError { get; set; }
    public bool Received { get; set; }
    public string? LastDeliveryError { get; set; }
    public bool Failed { get; set; }
    public string? FailedError { get; set; }
    public bool Delivered { get; set; }
    public bool? JobEverPrinting { get; set; }
    public int JobsAhead { get; set; }
    public string? JobsAheadText { get; set; }
    public string? PrinterProblem { get; set; }
    public string? PrinterHint { get; set; }
    public bool TimedOut { get; set; }
    public int ErrorEvents { get; set; }

    /// <summary>False when this session cannot read the data folder (not elevated), so the outbox and listener log are invisible.</summary>
    public bool DataFolderReadable { get; set; } = true;
}

/// <summary>
/// Turns the evidence into a stage reached, a one-sentence summary and a next step. Free of side effects so the decision table can
/// be tested. Port of Get-ErpTestPrintVerdict.
/// </summary>
public sealed record TestPrintVerdict(TestPrintStage Stage, bool Success, string Summary, string Hint)
{
    public static TestPrintVerdict Evaluate(TestPrintEvidence e)
    {
        if (e.Delivered)
            return new(TestPrintStage.Delivered, true, "The test page went through the whole pipeline and reached the ERP.", "");

        if (e.Failed)
            return new(TestPrintStage.Received, false,
                $"The listener received the page but the ERP rejected it: {e.FailedError}",
                "Fix the cause shown, then use \"Retry failed\" in the Status tab.");

        if (e.Received)
        {
            var lastError = !string.IsNullOrEmpty(e.LastDeliveryError) ? $" The last delivery error was: {e.LastDeliveryError}" : "";
            return new(TestPrintStage.Received, false,
                $"The listener received the page and queued it, but it has not been delivered to the ERP yet.{lastError}",
                "The printing side works. Check that the ErpPrinter service is running (it hosts the uploader) and that this computer can reach the ERP as SYSTEM (proxy, firewall); the Status tab shows the error and the retry time.");
        }

        if (!string.IsNullOrEmpty(e.ListenerError))
            return new(TestPrintStage.Spooled, false,
                $"The spooler reached the listener, but the listener could not take the page: {e.ListenerError}",
                "This is a problem inside ERP Printer on this machine (empty data, wrong driver, or a file error in the data folder). The listener log has the details.");

        if (!e.Submitted)
            return new(TestPrintStage.None, false,
                "The test page could not be sent to the Windows printer.",
                "The printer does not exist or cannot be opened from this session. Use Repair printers.");

        if (e.JobStuck)
        {
            var port = string.IsNullOrEmpty(e.ExpectedPort) ? "the pipe" : e.ExpectedPort;
            var pipeNote = e.PipeExists == false ? $" Nothing is hosting {port}: the service is not running or has not opened it." : "";
            var neverPrinting = e.JobEverPrinting == false;
            var why = "";
            var hasPrinterProblem = !string.IsNullOrEmpty(e.PrinterProblem);
            if (hasPrinterProblem) why += $" The printer is not ready: {e.PrinterProblem}";
            var ahead = e.JobsAhead;
            if (ahead > 0)
                why += $" {ahead} job(s) were already in the queue ({e.JobsAheadText}); the spooler prints one job at a time per queue, so an older stuck job blocks this page.";
            if (neverPrinting && why.Length == 0) why = " The job never reached 'Printing', so the spooler never tried to open the pipe.";

            var hint = "Make sure the ErpPrinter service is running and the pipe is visible, that the printer is not set offline, and read the Print Spooler Admin log for the exact port error. Then delete the stuck job and try again.";
            if (hasPrinterProblem) hint = e.PrinterHint ?? "";
            else if (ahead > 0) hint = "Delete the older job(s) (Get-PrintJob -PrinterName <printer> | Remove-PrintJob) and print again. If they will not delete or come back, restart the Print Spooler service (Restart-Service Spooler).";
            else if (neverPrinting) hint = $"Nothing in the printer state or queue explains it, and the pipe is not involved yet. Restart the Print Spooler service (Restart-Service Spooler) and try again. If the job still sits at '{e.LastJobStatus}', check Event Viewer (PrintService Admin, and System for Print Spooler faults) and whether 'Microsoft Print to PDF' prints to a file on this machine.";

            return new(TestPrintStage.InQueue, false,
                $"The spooler accepted the page but never handed it to the printer port; the job is stuck as '{e.LastJobStatus}'.{why}{pipeNote}", hint);
        }

        // Unelevated: the spooler side is visible, the listener side is not. Say so instead of blaming the listener.
        if (!e.DataFolderReadable && (e.SpoolerPrinted || e.JobGone))
            return new(TestPrintStage.Spooled, false,
                "The spooler handed the page to the printer port. This session cannot read the data folder, so it cannot see whether the listener received and delivered it.",
                "Run the test print elevated to follow it to the ERP, or look in the Status tab or the Application event log (source ErpPrinter) for the 'Queued' and 'Delivered' lines of the test page.");

        if (e.SpoolerPrinted)
        {
            var port = !string.IsNullOrEmpty(e.SpoolerPort) ? $" to '{e.SpoolerPort}'" : "";
            var mismatch = "";
            if (!string.IsNullOrEmpty(e.SpoolerPort) && !string.IsNullOrEmpty(e.ExpectedPort) && !e.SpoolerPort.Contains(e.ExpectedPort, StringComparison.OrdinalIgnoreCase))
                mismatch = $" That is not the listener's pipe ({e.ExpectedPort}).";
            var pipeNote = e.PipeExists == false ? " No process is hosting the pipe, so the data had nowhere to go." : "";
            if (e.ListenerConnected == false) pipeNote += " The listener log shows no connection from the spooler.";
            return new(TestPrintStage.Spooled, false,
                $"The spooler reports the job as printed{port}, but the listener never received it.{mismatch}{pipeNote}",
                "Run Repair printers so the queue uses the right port, make sure only one ErpPrinter service process runs (and no old PowerShell listener), and check the listener log for a connection line (\"The spooler connected to pipe\"). If there is none, the data went somewhere else.");
        }

        if (e.JobGone)
            return new(TestPrintStage.Spooled, false,
                "The job left the print queue, but the listener never received it and the spooler did not log what it did.",
                "Enable the PrintService Operational log (wevtutil sl Microsoft-Windows-PrintService/Operational /e:true) and repeat; event 307 shows the port the spooler used.");

        if (e.JobSeen)
            return new(TestPrintStage.InQueue, false,
                $"The job is in the print queue (status '{e.LastJobStatus}') but is not moving.",
                "The spooler is waiting on something; check the printer state, the Spooler service and the Admin log.");

        return new(TestPrintStage.Submitted, false,
            "The test page was sent to the spooler but never appeared in the print queue.",
            "Nothing reached the Windows spooler for this printer. Check the Print Spooler service, and run the app that printed as an elevated user.");
    }
}
