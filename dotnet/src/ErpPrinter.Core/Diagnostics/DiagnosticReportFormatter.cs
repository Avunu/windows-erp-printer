using System.Text;
using System.Text.RegularExpressions;

namespace ErpPrinter.Diagnostics;

/// <summary>Renders results as plain text that can be pasted into a ticket. Port of Format-ErpDiagnosticReport.</summary>
public static partial class DiagnosticReportFormatter
{
    [GeneratedRegex(@"^Job .* analysis$", RegexOptions.IgnoreCase)]
    private static partial Regex JobAnalysisName();

    [GeneratedRegex(@"\r?\n")]
    private static partial Regex LineBreak();

    public static string Format(IReadOnlyList<DiagnosticResult> results, DateTimeOffset generated, string computerName, string title = "ERP Printer diagnostics")
    {
        var builder = new StringBuilder();
        builder.AppendLine(title);
        builder.AppendLine($"Generated {generated:yyyy-MM-dd HH:mm:ss} on {computerName}");
        string Count(DiagnosticStatus status) => $"{results.Count(r => r.Status == status)} {status.ToString().ToLowerInvariant()}";
        builder.AppendLine(string.Join(", ", new[] { DiagnosticStatus.Fail, DiagnosticStatus.Warn, DiagnosticStatus.Pass, DiagnosticStatus.Info }.Select(Count)));

        // A job that died inside the spooler explains the printer, queue and pipe symptoms around it, so lead with it.
        var first = results.FirstOrDefault(r => r.Status == DiagnosticStatus.Fail && JobAnalysisName().IsMatch(r.Name))
                    ?? results.FirstOrDefault(r => r.Status == DiagnosticStatus.Fail);
        if (first is not null)
        {
            builder.AppendLine();
            builder.AppendLine($"Start with: {first.Category} / {first.Name}");
            if (first.Hint.Length > 0) builder.AppendLine($"  {first.Hint}");
        }

        foreach (var group in results.GroupBy(r => r.Category))
        {
            builder.AppendLine();
            builder.AppendLine($"== {group.Key} ==");
            foreach (var result in group)
            {
                var lines = LineBreak().Split(result.Detail);
                builder.AppendLine($"[{Label(result.Status)}] {result.Name}: {lines[0]}");
                foreach (var line in lines.Skip(1)) builder.AppendLine($"       {line}");
                if (result.Hint.Length > 0 && result.Status is DiagnosticStatus.Fail or DiagnosticStatus.Warn)
                    builder.AppendLine($"       -> {result.Hint}");
            }
        }
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string Label(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Pass => "PASS",
        DiagnosticStatus.Info => "INFO",
        DiagnosticStatus.Warn => "WARN",
        _ => "FAIL",
    };

    /// <summary>
    /// Writes the report next to the logs (or to the temp folder if that is not writable) and returns the path, or null.
    /// </summary>
    public static async Task<string?> SaveAsync(string report, string dataRoot, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var name = $"diagnostics-{now:yyyyMMdd-HHmmss}.log";
        foreach (var dir in new[] { Path.Combine(dataRoot, "logs"), Path.GetTempPath() })
        {
            try
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, name);
                await File.WriteAllTextAsync(path, report, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Try the next location.
            }
        }
        return null;
    }
}
