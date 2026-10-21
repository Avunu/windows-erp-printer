using ErpPrinter.Config.Model;
using ErpPrinter.Settings;

namespace ErpPrinter.Config.Tests;

public class CompositionTests
{
    [Fact]
    public void The_windows_composition_is_absent_until_WindowsConfigActions_is_added() =>
        // When ErpPrinter.Config.WindowsConfigActions exists, this test must be replaced by one that checks its Create() contract.
        Assert.Null(Composition.TryCreateWindows());

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
