using ErpPrinter.Config.Model;
using ErpPrinter.Settings;

namespace ErpPrinter.Config.Tests;

public class CompositionTests
{
    [Fact]
    public void The_windows_composition_exists_and_honours_the_Create_contract()
    {
        // Calling Create() would open the real HKLM configuration, so check the contract the loader relies on instead.
        var type = typeof(Composition).Assembly.GetType(Composition.WindowsActionsTypeName);
        Assert.NotNull(type);
        Assert.True(typeof(IConfigActions).IsAssignableFrom(type));
        var create = type.GetMethod("Create", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, Type.EmptyTypes);
        Assert.NotNull(create);
        Assert.Equal(typeof(ConfigComposition), create.ReturnType);
    }

    [Fact]
    public void The_demo_composition_has_sample_printers_policy_values_and_stored_secrets()
    {
        var demo = Composition.CreateDemo(TimeSpan.Zero);
        var profiles = demo.Store.LoadProfiles();

        Assert.Equal(["Head office", "Send to Odoo", "Vendor bills", "Warehouse ERPNext", "Webhook archive"], profiles.Select(p => p.Name));
        Assert.True(profiles.Single(p => p.Name == "Head office").PolicyDefined);
        Assert.Contains("PollIntervalSeconds", demo.Store.LoadGlobal().Managed);
        Assert.True(demo.Store.Secrets.Has("Send to Odoo", "ApiKey"));
        Assert.Empty(new ConfigFormModel(demo.Store).Validate());
    }

    [Fact]
    public void The_demo_status_follows_the_saved_profiles()
    {
        var demo = Composition.CreateDemo(TimeSpan.Zero);
        var status = demo.Actions.GetStatus();
        Assert.Equal(demo.Store.LoadProfiles().Select(p => p.Name), status.Profiles.Select(p => p.Profile));
        Assert.Equal("Running", status.ServiceState);
    }
}
