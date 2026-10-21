using System.Text;
using System.Text.Json.Nodes;
using ErpPrinter.Backends;
using ErpPrinter.Http;
using ErpPrinter.Logging;
using ErpPrinter.Outbox;
using ErpPrinter.Secrets;
using ErpPrinter.Settings;

namespace ErpPrinter.Core.Tests;

public sealed class BackendFakeDirectory(params string[] values) : IDirectoryIdentity
{
    public Task<IReadOnlyList<string>> LookupAsync(string samAccountName, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(values);
}

/// <summary>Fixture for the backend tests: a fake network, a fresh registry (so the session and user caches start empty) and a test PDF.</summary>
public abstract class BackendTestBase : IDisposable
{
    protected readonly BackendFakeHandler Handler = new();
    protected readonly HttpTestFiles Files = new();
    protected readonly MemoryLog Log = new();
    private readonly ErpHttp _http;
    private readonly string _pdf;

    protected BackendTestBase()
    {
        _http = new ErpHttp(Handler);
        _pdf = Files.CreatePdf();
        DirectoryLookup = new BackendFakeDirectory("john.doe@corp.example");
        Meta = new OutboxMeta
        {
            Id = "doc1", Profile = "P", User = @"CORP\jdoe", Title = "Invoice 7", Computer = "PC-01",
            JobId = "3", Pages = 2, Printed = DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
        };
    }

    protected IDirectoryIdentity DirectoryLookup { get; set; }
    protected OutboxMeta Meta { get; }
    protected GlobalSettings Global { get; } = GlobalSettings.Defaults();
    protected IReadOnlyList<BackendCall> Calls => Handler.Calls;

    private BackendRegistry? _registry;
    private BackendRegistry Registry => _registry ??= new BackendRegistry(_http, directory: DirectoryLookup, log: Log);

    public void Dispose()
    {
        _http.Dispose();
        Files.Dispose();
    }

    protected void Reply(int status, string body) => Handler.Reply(status, body);

    protected static PrinterProfile NewProfile(string backend, Dictionary<string, object>? overrides = null)
    {
        var values = new Dictionary<string, object> { ["Backend"] = backend, ["ServerUrl"] = "https://erp.example.com" };
        if (overrides is not null)
            foreach (var (key, value) in overrides) values[key] = value;
        return PrinterProfile.Create("P", values);
    }

    protected static readonly BackendSecrets KeyAndSecret = new("key", "secret");

    protected Task<string> SendAsync(PrinterProfile profile, BackendSecrets? secrets = null) =>
        Registry.Get(profile.Backend).SendAsync(profile, Global, new DocumentToSend(Meta, _pdf, "Invoice 7.pdf"), secrets ?? KeyAndSecret, CancellationToken.None);

    protected Task<string> TestAsync(PrinterProfile profile, BackendSecrets? secrets = null) =>
        Registry.Get(profile.Backend).TestConnectionAsync(profile, Global, secrets ?? KeyAndSecret, CancellationToken.None);

    protected static JsonNode Json(BackendCall call) => JsonNode.Parse(call.Body)!;

    /// <summary>The decoded text of a base64 value, to check that the PDF travelled.</summary>
    protected static string Decoded(JsonNode? base64) => Encoding.ASCII.GetString(Convert.FromBase64String((string)base64!));

    protected static List<string> Names(JsonNode? node) => ((JsonObject)node!).Select(p => p.Key).ToList();
}
