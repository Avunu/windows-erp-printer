using System.Drawing;
using System.Drawing.Printing;
using ErpPrinter.Diagnostics;

namespace ErpPrinter.Windows.Diagnostics;

/// <summary>
/// Prints a one-page document through the Windows printing API (GDI), as any application would. The document name carries the
/// test token so the test print can find the job in the queue, the spooler log and the outbox. Port of Send-ErpTestPage.
/// </summary>
public sealed class GdiTestPageSender(TimeProvider? time = null) : ITestPageSender
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Task<string> SendAsync(TestPageRequest request, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var text = BuildPageText(request, Environment.MachineName, $"{Environment.UserDomainName}\\{Environment.UserName}", _time.GetLocalNow());
            using var document = new PrintDocument();
            document.DocumentName = request.DocumentName;
            document.PrinterSettings.PrinterName = request.PrinterName;
            if (!document.PrinterSettings.IsValid)
                throw new InvalidOperationException($"Printer '{request.PrinterName}' is not valid for printing from this session.");
            document.PrintController = new StandardPrintController();
            document.PrintPage += (_, e) =>
            {
                using var font = new Font("Segoe UI", 14);
                e.Graphics?.DrawString(text, font, Brushes.Black, 72, 72);
                e.HasMorePages = false;
            };
            document.Print();
            return request.DocumentName;
        }, cancellationToken);

    /// <summary>The text printed on the page.</summary>
    internal static string BuildPageText(TestPageRequest request, string computer, string user, DateTimeOffset now) =>
        string.Join(Environment.NewLine,
        [
            "ERP Printer test page",
            "",
            $"Printer:  {request.PrinterName}",
            $"Profile:  {request.ProfileName}",
            $"Computer: {computer}",
            $"User:     {user}",
            $"Time:     {now:yyyy-MM-dd HH:mm:ss}",
            $"Test id:  {request.Token}",
            "",
            "If this page arrives in your ERP, printing works end to end.",
        ]);
}
