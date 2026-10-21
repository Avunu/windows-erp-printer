using ErpPrinter.Outbox;
using ErpPrinter.Secrets;
using ErpPrinter.Settings;

namespace ErpPrinter.Backends;

/// <summary>One document ready for delivery: its sidecar, the PDF on disk and the file name to send it under.</summary>
public sealed record DocumentToSend(OutboxMeta Meta, string PdfPath, string FileName);

/// <summary>
/// An ERP destination. Implementations throw <see cref="ErpException"/> for delivery failures, with
/// <see cref="ErpException.Permanent"/> telling the outbox whether to retry. Backends are discovered by name
/// (<see cref="Schema.BackendNames"/>); see <c>BackendRegistry</c>.
/// </summary>
public interface IBackend
{
    string Name { get; }

    /// <summary>Delivers one document and returns the remote id.</summary>
    Task<string> SendAsync(PrinterProfile profile, GlobalSettings global, DocumentToSend document, BackendSecrets secrets, CancellationToken cancellationToken);

    /// <summary>Checks that the ERP is reachable and the credentials work; returns a human-readable success message.</summary>
    Task<string> TestConnectionAsync(PrinterProfile profile, GlobalSettings global, BackendSecrets secrets, CancellationToken cancellationToken);
}
