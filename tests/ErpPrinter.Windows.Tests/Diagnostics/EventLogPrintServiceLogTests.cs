using ErpPrinter.Diagnostics;
using ErpPrinter.Windows.Diagnostics;

namespace ErpPrinter.Windows.Tests.Diagnostics;

/// <summary>Reads the real PrintService logs of the machine, read-only, in a time window where nothing can have happened.</summary>
public class EventLogPrintServiceLogTests
{
    [Theory]
    [InlineData(PrintServiceLogKind.Operational)]
    [InlineData(PrintServiceLogKind.Admin)]
    public async Task Reads_nothing_in_a_window_that_has_not_happened_yet(PrintServiceLogKind kind)
    {
        var events = await new EventLogPrintServiceLog().ReadAsync(kind, DateTimeOffset.UtcNow.AddHours(1));

        Assert.Empty(events);
    }

    [Fact]
    public async Task Reads_nothing_when_the_filters_cannot_match()
    {
        var events = await new EventLogPrintServiceLog().ReadAsync(PrintServiceLogKind.Operational, DateTimeOffset.UtcNow.AddHours(1), [1, 2, 3], [800, 801, 805, 842, 307], 300);

        Assert.Empty(events);
    }

    [Theory]
    [InlineData(PrintServiceLogKind.Operational)]
    [InlineData(PrintServiceLogKind.Admin)]
    public async Task Reports_the_state_of_the_log_without_throwing(PrintServiceLogKind kind)
    {
        var state = await new EventLogPrintServiceLog().GetStateAsync(kind);

        Assert.True(Enum.IsDefined(state));
    }

    [Fact]
    public async Task Returns_events_newest_first_with_a_level_name_when_the_log_has_any()
    {
        var events = await new EventLogPrintServiceLog().ReadAsync(PrintServiceLogKind.Admin, DateTimeOffset.UtcNow.AddYears(-1), null, null, 5);

        Assert.True(events.Count <= 5);
        Assert.Equal(events.OrderByDescending(e => e.Time).Select(e => e.Time), events.Select(e => e.Time));
        Assert.All(events, e => Assert.NotEqual("", e.LevelDisplayName));
    }

    [Fact]
    public void Builds_an_XPath_query_from_the_filters()
    {
        var since = new DateTimeOffset(2026, 10, 20, 17, 27, 35, 123, TimeSpan.FromHours(2));

        Assert.Equal(
            "*[System[(Level=1 or Level=2 or Level=3) and (EventID=800 or EventID=307) and TimeCreated[@SystemTime>='2026-10-20T15:27:35.123Z']]]",
            EventLogPrintServiceLog.BuildQuery(since, [1, 2, 3], [800, 307]));
        Assert.Equal("*[System[TimeCreated[@SystemTime>='2026-10-20T15:27:35.123Z']]]", EventLogPrintServiceLog.BuildQuery(since, null, null));
    }
}
