namespace ErpPrinter.Diagnostics;

/// <summary>Outcome of one diagnostic check. Pass and Info need no action; Warn may; Fail breaks the pipeline.</summary>
public enum DiagnosticStatus
{
    Pass,
    Info,
    Warn,
    Fail,
}

/// <summary>
/// One line of a diagnostics report. Hints say what to do next, not just what is wrong. Port of New-ErpDiagnosticResult.
/// </summary>
public sealed record DiagnosticResult(string Category, string Name, DiagnosticStatus Status, string Detail = "", string Hint = "")
{
    public static DiagnosticResult Pass(string category, string name, string detail = "") => new(category, name, DiagnosticStatus.Pass, detail);
    public static DiagnosticResult Info(string category, string name, string detail = "") => new(category, name, DiagnosticStatus.Info, detail);
    public static DiagnosticResult Warn(string category, string name, string detail, string hint = "") => new(category, name, DiagnosticStatus.Warn, detail, hint);
    public static DiagnosticResult Fail(string category, string name, string detail, string hint = "") => new(category, name, DiagnosticStatus.Fail, detail, hint);
}

/// <summary>Runs one check so that a check which cannot run never hides the others. Port of Invoke-ErpDiagnosticStep.</summary>
public static class DiagnosticStep
{
    /// <summary>Synchronous form of <see cref="RunAsync"/>.</summary>
    public static IReadOnlyList<DiagnosticResult> Run(string category, string name, Action<ICollection<DiagnosticResult>> check)
    {
        var results = new List<DiagnosticResult>();
        try
        {
            check(results);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            results.Add(DiagnosticResult.Warn(category, name, $"The check itself failed: {ex.Message}"));
        }
        return results;
    }

    /// <summary>
    /// Runs <paramref name="check"/>, which adds its results to the sink it is given. If the check throws, the results it
    /// added before throwing are kept and one warning explains that the check itself failed. Cancellation is not swallowed.
    /// </summary>
    public static async Task<IReadOnlyList<DiagnosticResult>> RunAsync(string category, string name, Func<ICollection<DiagnosticResult>, Task> check)
    {
        var results = new List<DiagnosticResult>();
        try
        {
            await check(results).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            results.Add(DiagnosticResult.Warn(category, name, $"The check itself failed: {ex.Message}"));
        }
        return results;
    }
}
