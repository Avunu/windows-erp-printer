namespace ErpPrinter.Updates;

/// <summary>
/// When the updater next checks for a new version. The PowerShell product ran a scheduled task daily at 03:00 with a random
/// delay of up to 3 hours (so a fleet does not hit the release server at once) and "start when available" for missed runs.
/// </summary>
public static class UpdateSchedule
{
    public static readonly TimeSpan MaxStartDelay = TimeSpan.FromHours(3);
    public static readonly TimeSpan CatchUpDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <param name="now">The current local time.</param>
    /// <param name="lastCheck">When the last check finished, or null if there never was one.</param>
    /// <param name="randomFraction">A value in [0, 1) that picks the delay within <see cref="MaxStartDelay"/>.</param>
    public static DateTimeOffset NextRun(DateTimeOffset now, DateTimeOffset? lastCheck, double randomFraction)
    {
        // A check was missed (the machine was off at 03:00, or this is a new install): run soon after start-up.
        if (lastCheck is null || now - lastCheck.Value >= Interval) return now + CatchUpDelay;

        var today = new DateTimeOffset(now.Year, now.Month, now.Day, 3, 0, 0, now.Offset);
        var start = today > now ? today : today.AddDays(1);
        return start + TimeSpan.FromTicks((long)(MaxStartDelay.Ticks * Math.Clamp(randomFraction, 0, 0.999999)));
    }
}
