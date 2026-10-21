using System.Text.RegularExpressions;
using ErpPrinter.Printing;

namespace ErpPrinter.Diagnostics;

/// <summary>What the spooler did with one queued job: where it stopped, the evidence, and what to do about it.</summary>
public sealed record JobAnalysis(SpoolerStage Stage, bool Failed, string Detail, string Hint);

/// <summary>
/// Explains what the spooler did with a queued print job. A job stuck in the queue tells you nothing by itself; the PrintService
/// Operational log and the spool shadow file say which stage it reached and in what format the application sent it.
/// Port of Get-ErpJobAnalysis.
/// </summary>
public sealed partial class JobAnalyzer(IPrintServiceLog log, ISpoolFileInspector spoolFiles, TimeProvider? time = null)
{
    private static readonly int[] StageEventIds = [800, 801, 805, 842, 307];

    [GeneratedRegex(@"\d+")]
    private static partial Regex FirstNumber();

    [GeneratedRegex(@"0x[0-9A-Fa-f]+")]
    private static partial Regex HexCode();

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<JobAnalysis> AnalyzeAsync(PrintJobInfo job, CancellationToken cancellationToken = default)
    {
        var since = job.Submitted is { } submitted ? submitted.AddMinutes(-1) : _time.GetLocalNow().AddDays(-1);

        // Every one of these messages starts with, or contains first, the job number.
        var read = await log.ReadAsync(PrintServiceLogKind.Operational, since, null, StageEventIds, 300, cancellationToken).ConfigureAwait(false);
        var events = read.Where(e => MentionsJob(e.Message, job.Id)).OrderBy(e => e.Time).ToList();

        var code = "";
        foreach (var ev in events.Where(e => e.Id == 842))
        {
            var codes = HexCode().Matches(ev.Message);
            if (codes.Count > 0) code = codes[^1].Value;
        }
        var verdict = SpoolerStageVerdict.Evaluate(events.Select(e => e.Id), code);

        var details = new List<string>
        {
            $"job {job.Id} '{job.DocumentName}' ({Math.Round(job.SizeBytes / 1024.0)} KB, {job.TotalPages} page(s), status {job.StatusText})",
        };
        if (!string.IsNullOrEmpty(job.Datatype)) details.Add($"datatype {job.Datatype}");
        var format = spoolFiles.GetFormat(job.Id);
        if (!string.IsNullOrEmpty(format)) details.Add($"spool file {format}");

        string Name(int id) => id switch
        {
            800 => "spooled",
            801 => "printing started",
            805 => "rendering",
            842 => $"print processor finished ({code})",
            307 => "written to port",
            _ => id.ToString(),
        };
        var timeline = events.Count > 0
            ? string.Join(" -> ", events.Select(e => $"{e.Time:HH:mm:ss} {Name(e.Id)}"))
            : "no spooler events";

        var detail = string.Join("; ", details) + Environment.NewLine + "Spooler timeline: " + timeline + Environment.NewLine + verdict.Summary;
        return new JobAnalysis(verdict.Stage, verdict.Failed, detail, verdict.Hint);
    }

    private static bool MentionsJob(string message, int jobId)
    {
        var match = FirstNumber().Match(message);
        return match.Success && int.TryParse(match.Value, out var number) && number == jobId;
    }
}
