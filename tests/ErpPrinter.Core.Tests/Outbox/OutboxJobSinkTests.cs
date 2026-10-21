using System.Text;
using ErpPrinter.Logging;
using ErpPrinter.Outbox;
using ErpPrinter.Printing;
using ErpPrinter.Settings;

namespace ErpPrinter.Core.Tests;

/// <summary>Port of the 'Receiving a print job' Pester cases (tests/Unit/Diagnostics.Tests.ps1).</summary>
public sealed class OutboxJobSinkTests : IDisposable
{
    private readonly OutboxFixture _f = new();
    private readonly PrinterProfile _profile = PrinterProfile.Create("Send to Odoo", new Dictionary<string, object> { ["PipeName"] = "p" });
    private readonly OutboxJobSink _sink;

    private static readonly PrintJobInfo Job = new(4, "Send to Odoo", "Invoice 7", @"CORP\jdoe", JobStatusFlags.Printing, 1, 0, 0, null);

    public OutboxJobSinkTests() => _sink = new OutboxJobSink(_f.Log, _f.Clock, computerName: "PC1");

    public void Dispose() => _f.Dispose();

    private Task<OutboxMeta?> ReceiveAsync(Stream stream, PrintJobInfo? job = null, CancellationToken ct = default) =>
        _sink.ReceiveAsync(_profile, _f.Global, stream, job, ct);

    private static MemoryStream Ascii(string text) => new(Encoding.ASCII.GetBytes(text));

    private IEnumerable<string> Files(string pattern) => Directory.Exists(_f.Paths.Outbox) ? Directory.GetFiles(_f.Paths.Outbox, pattern) : [];

    [Fact]
    public async Task Queues_a_PDF_and_writes_its_sidecar()
    {
        var meta = await ReceiveAsync(Ascii("%PDF-1.4\nhello\n%%EOF"), Job);

        var pdf = Assert.Single(Files("*.pdf"));
        Assert.Single(Files("*.json"));
        Assert.Empty(Files("*.tmp"));
        var onDisk = _f.ReadMeta(_f.Paths.Outbox, Path.GetFileNameWithoutExtension(pdf));
        Assert.Equal("Invoice 7", onDisk.Title);
        Assert.Equal(@"CORP\jdoe", onDisk.User);
        Assert.Equal("4", onDisk.JobId);
        Assert.Equal(1, onDisk.Pages);
        Assert.Equal("PC1", onDisk.Computer);
        Assert.Equal(new FileInfo(pdf).Length, onDisk.SizeBytes);
        Assert.Equal(0, onDisk.Attempts);
        Assert.Equal(_f.Clock.GetLocalNow(), onDisk.Printed);
        Assert.NotNull(meta);
        Assert.Equal(32, meta.Id.Length);
        Assert.Contains(_f.Log.Entries, e =>
            e.Level == ErpLogLevel.Information && e.EventId == 1001 &&
            e.Message == $@"Queued 'Invoice 7' from CORP\jdoe (0 KB) on 'Send to Odoo' as {meta.Id}.");
    }

    [Fact]
    public async Task Uses_the_submitted_time_of_the_spooler_job_when_it_has_one()
    {
        var submitted = new DateTimeOffset(2026, 2, 27, 8, 15, 0, TimeSpan.FromHours(2));
        var meta = await ReceiveAsync(Ascii("%PDF-1.4 x"), Job with { Submitted = submitted });
        Assert.Equal(submitted, meta!.Printed);
    }

    [Fact]
    public async Task Reports_the_size_in_rounded_kilobytes()
    {
        await ReceiveAsync(new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.4").Concat(new byte[5000]).ToArray()), Job);
        Assert.Contains("(5 KB)", _f.LogText);
    }

    [Fact]
    public async Task Discards_an_empty_job_and_says_why()
    {
        var meta = await ReceiveAsync(new MemoryStream(), Job);
        Assert.Null(meta);
        Assert.Equal(0, OutboxFixture.Count(_f.Paths.Outbox));
        Assert.Contains(_f.Log.Entries, e =>
            e.Level == ErpLogLevel.Warning &&
            e.Message == "Discarded an empty job on 'Send to Odoo': the spooler connected to the pipe but sent no data.");
    }

    [Fact]
    public async Task Warns_with_the_first_bytes_when_the_data_is_not_a_PDF()
    {
        var meta = await ReceiveAsync(Ascii("%!PS-Adobe-3.0 postscript"), Job);
        Assert.NotNull(meta); // still queued, as in the PowerShell product
        Assert.Contains(_f.Log.Entries, e =>
            e.Level == ErpLogLevel.Warning &&
            e.Message == "Job on 'Send to Odoo' does not look like a PDF (starts with 25 21 50 53 2D, 25 bytes); is the queue using the 'Microsoft Print To PDF' driver?");
    }

    [Fact]
    public async Task Warns_when_the_spooler_lists_no_job_and_still_queues_the_document()
    {
        var meta = await ReceiveAsync(Ascii("%PDF-1.4 x"), job: null);
        Assert.Contains(_f.Log.Entries, e =>
            e.Level == ErpLogLevel.Warning &&
            e.Message == "A job arrived on 'Send to Odoo' but the spooler lists none, so the user and title are unknown.");
        Assert.Single(Files("*.json"));
        Assert.Equal("", meta!.Title);
        Assert.Equal("", meta.User);
    }

    [Fact]
    public async Task Does_not_leave_a_temp_file_behind_when_the_pipe_breaks_mid_job()
    {
        var ex = await Assert.ThrowsAsync<IOException>(() => ReceiveAsync(new BrokenStream(), Job));
        Assert.Contains("pipe broken", ex.Message);
        Assert.Empty(Files("*.tmp"));
        Assert.Empty(Files("*.pdf"));
        Assert.Empty(Files("*.json"));
    }

    [Fact]
    public async Task Does_not_leave_a_temp_file_behind_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReceiveAsync(Ascii("%PDF-1.4 x"), Job, cts.Token));
        Assert.Empty(Files("*.tmp"));
    }

    [Fact]
    public async Task Gives_every_job_its_own_id()
    {
        var a = await ReceiveAsync(Ascii("%PDF-1.4 a"), Job);
        var b = await ReceiveAsync(Ascii("%PDF-1.4 b"), Job);
        Assert.NotEqual(a!.Id, b!.Id);
        Assert.Equal(2, Files("*.pdf").Count());
    }

    [Fact]
    public async Task Queued_documents_are_picked_up_by_the_outbox_listing()
    {
        await ReceiveAsync(Ascii("%PDF-1.4 x"), Job);
        var item = Assert.Single(_f.Store.List(_f.Paths.Outbox));
        Assert.True(File.Exists(item.PdfPath));
    }

    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("pipe broken");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("pipe broken");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
