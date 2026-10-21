using System.Globalization;
using System.Text;
using ErpPrinter.Logging;
using ErpPrinter.Printing;
using ErpPrinter.Settings;

namespace ErpPrinter.Outbox;

/// <summary>
/// Streams one job from a connected pipe to <c>&lt;id&gt;.pdf</c> in the profile's outbox and writes the <c>&lt;id&gt;.json</c>
/// sidecar last. Port of Receive-ErpPrintJob.
/// </summary>
/// <param name="pdfDriverName">Named in the "does not look like a PDF" warning.</param>
/// <param name="computerName">Recorded in the sidecar; defaults to this machine's name.</param>
public sealed class OutboxJobSink(IErpLog log, TimeProvider time, string pdfDriverName = "Microsoft Print To PDF", string? computerName = null) : IJobSink
{
    private readonly OutboxStore _store = new(log, time);

    public async Task<OutboxMeta?> ReceiveAsync(PrinterProfile profile, GlobalSettings global, Stream source, PrintJobInfo? job, CancellationToken cancellationToken)
    {
        // The caller looked the job up while the spooler still showed it as printing.
        if (job is null)
            log.Warning($"A job arrived on '{profile.PrinterName}' but the spooler lists none, so the user and title are unknown.");

        var paths = OutboxPaths.For(global.DataRoot, profile.Name);
        paths.Initialize();
        var id = Guid.NewGuid().ToString("N");
        var pdf = Path.Combine(paths.Outbox, id + ".pdf");
        var tmp = pdf + ".tmp";
        long size;

        try
        {
            await using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await source.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }
            size = new FileInfo(tmp).Length;
            if (size == 0)
            {
                File.Delete(tmp);
                log.Warning($"Discarded an empty job on '{profile.PrinterName}': the spooler connected to the pipe but sent no data.");
                return null;
            }

            var header = new byte[5];
            await using (var stream = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var read = 0;
                while (read < header.Length)
                {
                    var n = await stream.ReadAsync(header.AsMemory(read), cancellationToken).ConfigureAwait(false);
                    if (n == 0) break;
                    read += n;
                }
            }
            if (Encoding.ASCII.GetString(header) != "%PDF-")
            {
                var hex = string.Join(' ', header.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
                log.Warning($"Job on '{profile.PrinterName}' does not look like a PDF (starts with {hex}, {size} bytes); is the queue using the '{pdfDriverName}' driver?");
            }
            File.Move(tmp, pdf);
        }
        catch
        {
            // Do not leave a half-written file behind for the orphan recovery to adopt.
            try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
            throw;
        }

        var meta = job is null
            ? OutboxStore.NewMeta(id, profile.Name, sizeBytes: size, computer: computerName, time: time)
            : OutboxStore.NewMeta(id, profile.Name, job.UserName, job.DocumentName, job.Id.ToString(CultureInfo.InvariantCulture), job.TotalPages, size,
                job.Submitted, computerName, time);
        _store.SaveMeta(paths.Outbox, meta);
        var kb = (long)Math.Round(size / 1024.0);
        log.Information($"Queued '{meta.Title}' from {meta.User} ({kb} KB) on '{profile.PrinterName}' as {id}.", 1001);
        return meta;
    }
}
