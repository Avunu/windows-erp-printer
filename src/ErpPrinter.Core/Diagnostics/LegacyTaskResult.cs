namespace ErpPrinter.Diagnostics;

/// <summary>Translates a legacy scheduled task's LastTaskResult into something a person can act on. Port of ConvertTo-ErpTaskResultText.</summary>
public static class LegacyTaskResult
{
    public static string Describe(long code)
    {
        var unsigned = code < 0 ? code + 4294967296L : code;
        var hex = $"0x{unsigned:X8}";
        var text = unsigned switch
        {
            0 => "completed successfully",
            1 => "the script exited with an error (see the cli-*.log files in the log folder)",
            267008 => "ready to run",
            267009 => "currently running",
            267010 => "disabled",
            267011 => "has not run yet",
            267014 => "was stopped",
            2147942402 => "file not found (the install folder or ErpPrinter.ps1 is missing)",
            2147942405 => "access denied",
            2147943645 => "the task could not start (check the account and logon rights)",
            2147946720 => "a duplicate start was ignored because the task is already running; normal for the watchdog",
            3221225786 => "terminated by Ctrl+C or a console close",
            _ => "unrecognized result",
        };
        return $"{hex} ({text})";
    }
}
