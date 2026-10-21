using System.Text;
using ErpPrinter.Backends;
using ErpPrinter.Logging;
using ErpPrinter.Outbox;
using ErpPrinter.Secrets;
using ErpPrinter.Settings;

namespace ErpPrinter.Core.Tests;

/// <summary>A clock the test moves by hand. Local time is UTC so file times and sidecar times line up.</summary>
internal sealed class OutboxFakeClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public void Advance(TimeSpan by) => _now += by;
    public DateTimeOffset Now => _now;
}

internal sealed class OutboxFakeProtector : ISecretProtector
{
    public byte[] Protect(string plainText) => Encoding.UTF8.GetBytes("enc:" + plainText);
    public string Unprotect(byte[] cipher) => Encoding.UTF8.GetString(cipher)["enc:".Length..];
}

/// <summary>A backend whose behaviour the test sets and whose calls it can inspect.</summary>
internal sealed class OutboxFakeBackend : IBackend
{
    public string Name => "OdooJson2";
    public Func<DocumentToSend, string> OnSend { get; set; } = _ => "remote-42";
    public List<(PrinterProfile Profile, DocumentToSend Document, BackendSecrets Secrets)> Calls { get; } = [];

    public Task<string> SendAsync(PrinterProfile profile, GlobalSettings global, DocumentToSend document, BackendSecrets secrets, CancellationToken cancellationToken)
    {
        Calls.Add((profile, document, secrets));
        return Task.FromResult(OnSend(document));
    }

    public Task<string> TestConnectionAsync(PrinterProfile profile, GlobalSettings global, BackendSecrets secrets, CancellationToken cancellationToken) =>
        Task.FromResult("ok");
}

internal sealed class OutboxFakeBackendProvider(IBackend backend) : IBackendProvider
{
    public IBackend Get(string backendName) => backend;
}

/// <summary>A temp data folder, a fake clock, a fake backend and the engine wired to them.</summary>
internal sealed class OutboxFixture : IDisposable
{
    public const string ProfileName = "Send to Odoo";

    public OutboxFixture(Dictionary<string, object>? globalOverrides = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "erp-outbox-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Clock = new OutboxFakeClock(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
        Log = new MemoryLog();
        Registry = new MemoryRegistryStore();
        Secrets = new SecretStore(Registry, new OutboxFakeProtector());
        Backend = new OutboxFakeBackend();
        Paths = OutboxPaths.For(Root, ProfileName);
        Paths.Initialize();
        Global = MakeGlobal(globalOverrides);
        Profile = PrinterProfile.Create(ProfileName, new Dictionary<string, object> { ["Backend"] = "OdooJson2", ["ServerUrl"] = "https://erp.example" });
        Processor = new OutboxProcessor(Log, new OutboxFakeBackendProvider(Backend), Secrets, (_, meta) => meta.Title + ".pdf", Clock);
        Store = Processor.Store;
    }

    public string Root { get; }
    public OutboxFakeClock Clock { get; }
    public MemoryLog Log { get; }
    public MemoryRegistryStore Registry { get; }
    public SecretStore Secrets { get; }
    public OutboxFakeBackend Backend { get; }
    public OutboxPaths Paths { get; }
    public GlobalSettings Global { get; private set; }
    public PrinterProfile Profile { get; set; }
    public OutboxProcessor Processor { get; }
    public OutboxStore Store { get; }

    public ErpConfig Config => new(Global, [Profile]);

    public GlobalSettings MakeGlobal(Dictionary<string, object>? overrides)
    {
        var values = Schema.Global.ToDictionary(s => s.Name, s => s.DefaultFor(null));
        values["DataRoot"] = Root;
        if (overrides is not null)
            foreach (var (key, value) in overrides) values[key] = value;
        return new GlobalSettings(values);
    }

    public void Configure(Dictionary<string, object> overrides) => Global = MakeGlobal(overrides);

    public string LogText => string.Join("\n", Log.Entries.Select(e => $"{e.Level}|{e.EventId}|{e.Message}"));

    /// <summary>Writes a PDF and its sidecar into the outbox (the PowerShell tests' Add-TestItem).</summary>
    public string AddItem(string? id = null, int extraBytes = 0, string? folder = null)
    {
        id ??= Guid.NewGuid().ToString("N");
        folder ??= Paths.Outbox;
        WritePdf(Path.Combine(folder, id + ".pdf"), extraBytes);
        var meta = OutboxStore.NewMeta(id, ProfileName, @"CORP\jdoe", "Invoice 7", "3", 1, 40, Clock.GetLocalNow(), "PC1", Clock);
        Store.SaveMeta(folder, meta);
        return id;
    }

    public OutboxItem GetItem(string id, string? folder = null) => Store.List(folder ?? Paths.Outbox).Single(i => i.Meta.Id == id);

    public Task<SendOutcome> SendAsync(string id, bool? enabled = null)
    {
        if (enabled is { } flag && flag != Profile.Enabled)
            Profile = PrinterProfile.Create(ProfileName, new Dictionary<string, object> { ["Backend"] = "OdooJson2", ["ServerUrl"] = "https://erp.example", ["Enabled"] = flag });
        return Processor.SendItemAsync(GetItem(id), Profile, Global, Paths, Clock.GetLocalNow(), CancellationToken.None);
    }

    public static void WritePdf(string path, int extraBytes = 0) =>
        File.WriteAllText(path, "%PDF-1.4\n% test document\n" + new string('x', extraBytes) + "\n%%EOF\n");

    public OutboxMeta ReadMeta(string folder, string id) =>
        System.Text.Json.JsonSerializer.Deserialize<OutboxMeta>(File.ReadAllText(Path.Combine(folder, id + ".json")), OutboxStore.JsonOptions)!;

    public static int Count(string folder) => Directory.Exists(folder) ? Directory.GetFileSystemEntries(folder).Length : 0;

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { /* temp folder; the OS cleans up */ }
    }
}
