using ErpPrinter.Diagnostics;
using ErpPrinter.Windows.Diagnostics;

namespace ErpPrinter.Windows.Tests.Diagnostics;

/// <summary>These tests never print: only the page text and the refusal of an unknown printer are exercised.</summary>
public class GdiTestPageSenderTests
{
    [Fact]
    public void Builds_the_same_page_text_as_the_PowerShell_version()
    {
        var request = new TestPageRequest("Send to Odoo", "Send to Odoo", "abcd1234", "ERP Printer test page [abcd1234]");

        var text = GdiTestPageSender.BuildPageText(request, "PC1", @"CORP\jdoe", new DateTimeOffset(2026, 10, 20, 17, 27, 35, TimeSpan.Zero));

        var nl = Environment.NewLine;
        Assert.Equal(
            "ERP Printer test page" + nl + nl +
            "Printer:  Send to Odoo" + nl +
            "Profile:  Send to Odoo" + nl +
            "Computer: PC1" + nl +
            @"User:     CORP\jdoe" + nl +
            "Time:     2026-10-20 17:27:35" + nl +
            "Test id:  abcd1234" + nl + nl +
            "If this page arrives in your ERP, printing works end to end.",
            text);
    }

    [Fact]
    public async Task Refuses_a_printer_that_does_not_exist_without_printing()
    {
        var request = new TestPageRequest("ERP Printer no such printer " + Guid.NewGuid().ToString("N"), "p", "abcd1234", "ERP Printer test page [abcd1234]");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new GdiTestPageSender().SendAsync(request));

        Assert.Contains("is not valid for printing", ex.Message);
    }
}
