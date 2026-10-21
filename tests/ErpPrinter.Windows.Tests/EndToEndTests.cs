using System.IO.Pipes;
using System.Net;
using System.Text;
using ErpPrinter.Outbox;
using ErpPrinter.Printing;
using ErpPrinter.Settings;
using ErpPrinter.Windows.Hosting;
using ErpPrinter.Windows.Listening;
using ErpPrinter.Windows.Logging;

namespace ErpPrinter.Windows.Tests;

/// <summary>
/// The whole product in one process, with nothing faked except the print spooler and the registry: a document written
/// to a real named pipe is received by the real listener, queued in a real outbox folder, picked up by the real uploader
/// and POSTed by the real webhook backend to a loopback HTTP server. This is the path a printed page takes after the
/// spooler hands it over, so a regression anywhere in the port shows up here.
/// </summary>
public sealed class EndToEndTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "erp-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly HttpListener _webhook = new();
    private readonly string _webhookUrl;

    public EndToEndTests()
    {
        var port = FreePort();
        _webhookUrl = $"http://127.0.0.1:{port}/hook/";
        _webhook.Prefixes.Add(_webhookUrl);
        _webhook.Start();
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    /// <summary>The uploader normally takes a machine-wide mutex, which the PowerShell uploader on a developer machine may hold.</summary>
    private sealed class AlwaysFreeLock : ISingleInstanceLock
    {
        public IDisposable? TryAcquire() => new NoopHandle();
        private sealed class NoopHandle : IDisposable { public void Dispose() { } }
    }

    private sealed record Delivery(byte[] Body, string ContentType, string? IdempotencyKey);

    private async Task<Delivery> AcceptOneDeliveryAsync(CancellationToken ct)
    {
        var context = await _webhook.GetContextAsync().WaitAsync(ct);
        using var buffer = new MemoryStream();
        await context.Request.InputStream.CopyToAsync(buffer, ct);
        var delivery = new Delivery(buffer.ToArray(), context.Request.ContentType ?? "", context.Request.Headers["Idempotency-Key"]);
        var reply = Encoding.UTF8.GetBytes("""{"id": "e2e-1"}""");
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        await context.Response.OutputStream.WriteAsync(reply, ct);
        context.Response.Close();
        return delivery;
    }

    [Fact]
    public async Task A_document_travels_from_the_pipe_through_the_outbox_to_the_webhook()
    {
        var pipeName = "ErpPrinter-E2E-" + Guid.NewGuid().ToString("N")[..12];
        var spooler = new WinFakeSpooler();
        spooler.Jobs["E2E Printer"] =
        [
            new PrintJobInfo(7, "E2E Printer", "Quarterly report", @"CORP\jdoe", JobStatusFlags.Printing, 3, 0, 4096, DateTimeOffset.Now),
        ];

        using var context = new ErpPrinterContext("e2e", registry: new MemoryRegistryStore(), events: new NullEventLogSink(), spooler: spooler);
        context.Store.SetSetting(null, "DataRoot", _dataRoot);
        context.Store.SetSetting(null, "PollIntervalSeconds", 2);
        context.Store.CreateProfile("E2E Printer", new Dictionary<string, object?>
        {
            ["Backend"] = "Webhook", ["ServerUrl"] = _webhookUrl, ["PipeName"] = pipeName,
        });

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var signal = new InProcessOutboxSignal();
        var listener = Task.Run(() => context.CreateListener(signal).RunAsync(stop.Token));
        var uploader = Task.Run(() => context.CreateUploaderLoop(signal, instanceLock: new AlwaysFreeLock()).RunAsync(stop.Token));
        var delivery = AcceptOneDeliveryAsync(stop.Token);

        // The listener opens its pipe a moment after it starts.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (PipeVisibility.Exists(pipeName) != true)
        {
            Assert.True(DateTime.UtcNow < deadline, "The listener never opened its pipe.");
            await Task.Delay(100);
        }

        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% end-to-end test document\n%%EOF\n");
        await using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out))
        {
            await client.ConnectAsync(5000);
            await client.WriteAsync(pdf);
        }

        var received = await delivery.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.StartsWith("multipart/form-data", received.ContentType);
        Assert.NotNull(received.IdempotencyKey);
        var body = Encoding.Latin1.GetString(received.Body);
        Assert.Contains("%PDF-1.4", body);
        Assert.Contains("end-to-end test document", body);
        Assert.Contains("Quarterly report", body);   // the title, from the spooler's job record
        Assert.Contains(@"CORP\jdoe", body);          // the printing user

        // Delivered documents leave the outbox and are counted.
        var status = await WaitForAsync(() => context.Status.Get().Profiles.Single(), p => p.Sent == 1 && p.Pending == 0, TimeSpan.FromSeconds(20));
        Assert.Equal(1, status.Sent);
        Assert.Equal("e2e-1", status.LastRemoteId);
        Assert.Equal(0, status.Failed);

        stop.Cancel();
        await Task.WhenAll(listener, uploader).WaitAsync(TimeSpan.FromSeconds(20));

        var logs = string.Concat(Directory.GetFiles(Path.Combine(_dataRoot, "logs"), "*.log").Select(File.ReadAllText));
        Assert.Contains("The spooler connected to pipe", logs);
        Assert.Contains("Queued 'Quarterly report'", logs);
        Assert.Contains("Delivered 'Quarterly report'", logs);
        Assert.Contains("Listener stopped.", logs);
        Assert.Contains("Uploader stopped.", logs);
    }

    private static async Task<T> WaitForAsync<T>(Func<T> read, Func<T, bool> done, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (true)
        {
            var value = read();
            if (done(value) || DateTime.UtcNow > end) return value;
            await Task.Delay(100);
        }
    }

    public void Dispose()
    {
        _webhook.Close();
        try { if (Directory.Exists(_dataRoot)) Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { /* a log handle may still be closing; the temp folder is harmless */ }
    }
}
