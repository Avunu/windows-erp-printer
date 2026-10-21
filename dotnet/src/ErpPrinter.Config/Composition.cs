using ErpPrinter.Secrets;
using ErpPrinter.Settings;

namespace ErpPrinter.Config;

/// <summary>The store and actions the main form runs on.</summary>
internal sealed record ConfigComposition(ConfigStore Store, IConfigActions Actions);

/// <summary>
/// Chooses what the form runs on. The real wiring lives in <c>ErpPrinter.Config.WindowsConfigActions</c>, a class that is added
/// when the Windows pieces (registry, DPAPI, service control, spooler) exist. To avoid a compile-time dependency on it, it is
/// found by name. Contract: a type <c>ErpPrinter.Config.WindowsConfigActions</c> in this assembly with a
/// <c>public static ConfigComposition Create()</c> that builds the real <see cref="ConfigStore"/> (RegistryStore, DPAPI) and
/// the actions, and throws with a readable message when that is impossible.
/// </summary>
internal static class Composition
{
    public const string WindowsActionsTypeName = "ErpPrinter.Config.WindowsConfigActions";

    /// <summary>The real composition, or null when <c>WindowsConfigActions</c> has not been added yet.</summary>
    public static ConfigComposition? TryCreateWindows()
    {
        var type = typeof(Composition).Assembly.GetType(WindowsActionsTypeName);
        if (type is null) return null;
        var factory = type.GetMethod("Create", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, Type.EmptyTypes)
            ?? throw new InvalidOperationException($"{WindowsActionsTypeName} must have a public static Create() returning ConfigComposition.");
        return factory.Invoke(null, null) as ConfigComposition
            ?? throw new InvalidOperationException($"{WindowsActionsTypeName}.Create() did not return a ConfigComposition.");
    }

    /// <summary>An in-memory configuration with sample printers, and fake actions; nothing touches the machine.</summary>
    public static ConfigComposition CreateDemo(TimeSpan? actionDelay = null)
    {
        var registry = new MemoryRegistryStore();
        var store = new ConfigStore(registry, new SecretStore(registry, new InMemorySecretProtector()));
        DemoData.Seed(registry, store);
        return new ConfigComposition(store, new DesignTimeConfigActions(store, actionDelay));
    }
}

internal static class DemoData
{
    public static void Seed(MemoryRegistryStore registry, ConfigStore store)
    {
        store.CreateProfile("Send to Odoo", new Dictionary<string, object?>
        {
            ["Backend"] = "OdooJson2",
            ["ServerUrl"] = "https://example.odoo.com",
            ["Database"] = "production",
            ["ApiKey"] = "demo-api-key",
            ["UserMapping"] = true,
        });
        store.CreateProfile("Vendor bills", new Dictionary<string, object?>
        {
            ["Backend"] = "OdooJsonRpc",
            ["ServerUrl"] = "https://erp.example.com",
            ["Username"] = "printer@example.com",
            ["ApiKey"] = "demo-api-key",
            ["OdooModel"] = "documents.document",
            ["OdooFileField"] = "datas",
        });
        store.CreateProfile("Warehouse ERPNext", new Dictionary<string, object?>
        {
            ["Backend"] = "ERPNext",
            ["ServerUrl"] = "https://erp.example.org",
            ["ApiKey"] = "demo-key",
            ["ApiSecret"] = "demo-secret",
            ["ERPNextDoctype"] = "Purchase Invoice",
            ["ERPNextDocname"] = "PINV-0001",
        });
        store.CreateProfile("Webhook archive", new Dictionary<string, object?>
        {
            ["Backend"] = "Webhook",
            ["ServerUrl"] = "https://hooks.example.com/print",
            ["Enabled"] = false,
        });

        // Group Policy: a managed global setting, a managed setting of a local profile, and a profile that exists only in policy.
        registry.SetValue(ConfigStore.PolicyRoot, "PollIntervalSeconds", 60, RegistryValueType.DWord);
        registry.SetValue($@"{ConfigStore.PolicyRoot}\Printers\Send to Odoo", "ServerUrl", "https://example.odoo.com", RegistryValueType.String);
        registry.SetValue($@"{ConfigStore.PolicyRoot}\Printers\Head office", "ServerUrl", "https://hq.example.com", RegistryValueType.String);
        registry.SetValue($@"{ConfigStore.PolicyRoot}\Printers\Head office", "Backend", "Webhook", RegistryValueType.String);
    }
}
