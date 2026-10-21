using System.Globalization;
using ErpPrinter.Config.Model;

namespace ErpPrinter.Config.Tests;

public class StatusFormatterTests
{
    private static ProfileStatus Status(bool enabled = true, bool printerOk = true, int pending = 0, int failed = 0, int sent = 0,
        DateTime? lastSent = null, string? remoteId = null, string error = "") =>
        new("Send to Odoo", enabled, "OdooJson2", "Send to Odoo (printer)", printerOk, pending, failed, sent, lastSent, remoteId, error, @"C:\data\spool\Send_to_Odoo\outbox");

    [Theory]
    [InlineData(true, true, "OK")]
    [InlineData(true, false, "Missing")]
    [InlineData(false, false, "Disabled")]
    [InlineData(false, true, "OK")]
    public void Queue_text_distinguishes_ok_missing_and_disabled(bool enabled, bool printerOk, string expected) =>
        Assert.Equal(expected, StatusFormatter.QueueText(Status(enabled, printerOk)));

    [Theory]
    [InlineData(true, true, 0, false)]
    [InlineData(true, true, 1, true)]      // failed documents
    [InlineData(true, false, 0, true)]     // enabled but the printer is gone
    [InlineData(false, false, 0, false)]   // disabled and absent is expected
    [InlineData(false, false, 2, true)]
    public void Rows_with_failures_or_a_missing_printer_are_problems(bool enabled, bool printerOk, int failed, bool problem) =>
        Assert.Equal(problem, StatusFormatter.IsProblem(Status(enabled, printerOk, failed: failed)));

    [Fact]
    public void Last_sent_is_empty_without_a_delivery_and_includes_the_remote_id()
    {
        var when = new DateTime(2026, 10, 21, 9, 41, 0);
        Assert.Equal("", StatusFormatter.LastSentText(Status()));
        Assert.Equal(when.ToString("g", CultureInfo.CurrentCulture), StatusFormatter.LastSentText(Status(lastSent: when)));
        Assert.Equal(when.ToString("g", CultureInfo.CurrentCulture) + " (id 1042)", StatusFormatter.LastSentText(Status(lastSent: when, remoteId: "1042")));
    }

    [Fact]
    public void A_row_has_one_cell_per_column_in_order()
    {
        var row = StatusFormatter.Row(Status(pending: 2, failed: 1, sent: 7, error: "HTTP 401"));
        Assert.Equal(StatusFormatter.Columns.Count, row.Length);
        Assert.Equal(["Printer profile", "Windows printer", "Queue", "Pending", "Failed", "Sent", "Last sent", "Last error"], StatusFormatter.Columns.Select(c => c.Header));
        Assert.Equal(["Send to Odoo", "Send to Odoo (printer)", "OK", "2", "1", "7", "", "HTTP 401"], row);
    }

    [Fact]
    public void The_service_line_shows_version_and_state() =>
        Assert.Equal("Version 1.2.3    Service: Running", StatusFormatter.ServiceLine(new StatusSnapshot("Running", "1.2.3", [])));
}
