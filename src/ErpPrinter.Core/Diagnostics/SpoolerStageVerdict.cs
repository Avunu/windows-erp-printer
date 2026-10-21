using System.Text.RegularExpressions;

namespace ErpPrinter.Diagnostics;

public enum SpoolerStage
{
    /// <summary>Event 307: the spooler wrote the job to the port.</summary>
    Printed,

    /// <summary>Event 842 with a non-zero Win32 code: the print processor failed.</summary>
    ProcessorFailed,

    /// <summary>Event 805 without 307: rendered but never written to the port.</summary>
    Rendered,

    /// <summary>Event 801 only: printing started, the print processor never finished.</summary>
    Printing,

    /// <summary>Event 800 only: received but never started.</summary>
    Spooled,

    /// <summary>No events for this job.</summary>
    Unknown,
}

/// <summary>
/// Says how far a print job got inside the spooler, from the PrintService Operational event ids it produced: 800 spooled,
/// 801 printing started, 842 print processor finished (with a Win32 code), 805 rendering, 307 written to the port.
/// Pure, so the decision table is testable. Port of Get-ErpSpoolerStageVerdict.
/// </summary>
public sealed partial record SpoolerStageVerdict(SpoolerStage Stage, bool Failed, string Summary, string Hint)
{
    [GeneratedRegex(@"^0x0+$", RegexOptions.IgnoreCase)]
    private static partial Regex ZeroCode();

    public static SpoolerStageVerdict Evaluate(IEnumerable<int> eventIds, string processorCode = "")
    {
        var ids = eventIds.ToHashSet();
        var badCode = processorCode.Length > 0 && !ZeroCode().IsMatch(processorCode);

        if (ids.Contains(307))
            return new(SpoolerStage.Printed, false,
                "The spooler finished this job and wrote it to the port.",
                "If it did not arrive, look for \"The spooler connected to pipe\" in the listener log.");

        if (badCode)
            return new(SpoolerStage.ProcessorFailed, true,
                $"The print processor failed with code {processorCode} before anything was written to the port.",
                "The document could not be converted for the driver. Print the same document to the built-in \"Microsoft Print to PDF\" printer; if that fails too, the cause is the document or Windows, not ERP Printer.");

        if (ids.Contains(805))
            return new(SpoolerStage.Rendered, true,
                "The spooler rendered the job but never completed writing it to the port.",
                "This points at the pipe: check that the ErpPrinter service is running and hosting it, the spooler Admin log, and the listener log for a connection.");

        if (ids.Contains(801))
            return new(SpoolerStage.Printing, true,
                "The spooler started printing but its print processor never finished (there is no 'sent through the print processor' event) and nothing reached the port. Either the spooler could not open the printer port as the user who printed, or the driver failed while converting the document to PDF.",
                "The spooler opens the port while impersonating the user who printed and needs write and delete access to it; listeners from ERP Printer 0.5.0 and older granted only write, so prints from non-administrators fail while elevated test prints work. Update ERP Printer (the listener re-creates the pipe with the right access when it starts). If the job came from an administrator, print the same document to the built-in 'Microsoft Print to PDF' printer: if that fails too, the cause is the document or Windows. Then delete the failed job: it blocks every job behind it.");

        if (ids.Contains(800))
            return new(SpoolerStage.Spooled, true,
                "The spooler received the job but never started printing it.",
                "The queue is blocked by an earlier job, paused, offline, or outside its availability hours.");

        return new(SpoolerStage.Unknown, false,
            "The spooler log has no events for this job.",
            "The PrintService Operational log may be disabled or may have rolled over.");
    }
}
