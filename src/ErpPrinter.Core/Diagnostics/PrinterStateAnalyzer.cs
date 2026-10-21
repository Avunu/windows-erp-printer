using System.Text.RegularExpressions;
using ErpPrinter.Printing;

namespace ErpPrinter.Diagnostics;

/// <summary>Whether the spooler will process jobs for a printer at all, in one line. <see cref="Problem"/> is null when the printer can print.</summary>
public sealed record PrinterStateInfo(string? Problem, string Hint, string Summary);

/// <summary>
/// Paused, offline, "Use Printer Offline", an error state or an availability window that is closed all leave jobs waiting with
/// status Normal. Port of Get-ErpPrinterStateInfo, working on <see cref="PrinterInfo"/> instead of Get-Printer and Win32_Printer.
/// </summary>
public static class PrinterStateAnalyzer
{
    /// <summary>States in which the spooler does not send jobs on. Printing, processing, warming up, busy, I/O active, waiting and power save are normal.</summary>
    private const PrinterStatusFlags Blocking =
        PrinterStatusFlags.Paused | PrinterStatusFlags.Error | PrinterStatusFlags.PendingDeletion | PrinterStatusFlags.PaperJam |
        PrinterStatusFlags.PaperOut | PrinterStatusFlags.PaperProblem | PrinterStatusFlags.Offline | PrinterStatusFlags.OutputBinFull |
        PrinterStatusFlags.NotAvailable | PrinterStatusFlags.NoToner | PrinterStatusFlags.UserIntervention | PrinterStatusFlags.OutOfMemory |
        PrinterStatusFlags.DoorOpen | PrinterStatusFlags.ServerUnknown;

    /// <param name="printer">The printer as the spooler lists it.</param>
    /// <param name="localTimeOfDay">The current local time of day, for the availability window.</param>
    public static PrinterStateInfo Analyze(PrinterInfo printer, TimeSpan localTimeOfDay)
    {
        var blocking = printer.Status & Blocking;
        var statusText = StatusText(printer.Status);
        string? problem = null;
        var hint = "";
        var availability = "";

        if (printer.WorkOffline)
        {
            problem = "The printer is set to \"Use Printer Offline\", so the spooler holds every job and never sends it.";
            hint = "Open the printer queue, Printer menu, and untick \"Use Printer Offline\".";
        }
        else if (blocking != PrinterStatusFlags.None)
        {
            var names = string.Join(", ", Enum.GetValues<PrinterStatusFlags>().Where(f => f != PrinterStatusFlags.None && blocking.HasFlag(f)));
            problem = $"The spooler reports the printer as {names}.";
            hint = "Open the printer queue and clear that state (Printer menu: untick \"Pause Printing\" or \"Use Printer Offline\"); jobs are not sent while it lasts.";
            if (blocking.HasFlag(PrinterStatusFlags.Error))
                hint = "A failed job in the queue puts the whole printer into this state; see the job analysis below. Delete the failed job, and if the printer stays in Error, restart the Print Spooler service.";
        }
        else if (printer.AvailableFromMinutes is { } fromMinutes && printer.AvailableUntilMinutes is { } untilMinutes && fromMinutes != untilMinutes)
        {
            var from = TimeSpan.FromMinutes(fromMinutes);
            var until = TimeSpan.FromMinutes(untilMinutes);
            var inside = from <= until
                ? localTimeOfDay >= from && localTimeOfDay < until
                : localTimeOfDay >= from || localTimeOfDay < until;
            availability = $" (available {Clock(from)}-{Clock(until)})";
            if (!inside)
            {
                problem = $"The printer is only available between {Clock(from)} and {Clock(until)} and it is outside those hours, so the spooler holds jobs.";
                hint = "Printer properties, Advanced tab: choose \"Always available\".";
            }
        }

        return new PrinterStateInfo(problem, hint, statusText + availability);
    }

    private static string Clock(TimeSpan value) => $"{value.Hours:00}:{value.Minutes:00}";

    /// <summary>"Normal" when no flag is set, otherwise the flag names ("Paused", "Printing, WarmingUp").</summary>
    private static string StatusText(PrinterStatusFlags status)
    {
        if (status == PrinterStatusFlags.None) return "Normal";
        return string.Join(", ", Enum.GetValues<PrinterStatusFlags>().Where(f => f != PrinterStatusFlags.None && status.HasFlag(f)));
    }
}

/// <summary>Helpers for describing spooler jobs in reports.</summary>
public static partial class PrintJobText
{
    [GeneratedRegex("Error|Offline|Paused|Blocked|UserIntervention|PaperOut|Deleting|Restart")]
    private static partial Regex BadStatus();

    /// <summary>True for spooler job states that mean the job will not reach the port by itself. Port of Test-ErpBadJobStatus.</summary>
    public static bool IsBadStatus(string? status) => !string.IsNullOrEmpty(status) && BadStatus().IsMatch(status);

    public static bool IsBadStatus(JobStatusFlags status) => IsBadStatus(status.ToString());

    /// <summary>One-line-per-job description of queue contents, joined with "; ". Port of Format-ErpPrintJobList.</summary>
    public static string FormatList(IEnumerable<PrintJobInfo> jobs, int max = 5) =>
        string.Join("; ", jobs.Take(max).Select(j => $"job {j.Id} '{j.DocumentName}' ({j.StatusText}, submitted {Submitted(j)})"));

    public static string Submitted(PrintJobInfo job) => job.Submitted is { } t ? t.ToString("yyyy-MM-dd HH:mm:ss") : "unknown";
}
