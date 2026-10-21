using System.Security.AccessControl;
using System.Security.Principal;
using ErpPrinter.Logging;
using ErpPrinter.Printing;
using ErpPrinter.Settings;
using ErpPrinter.Windows.Printing;
using ErpPrinter.Windows.Setup;

namespace ErpPrinter.Windows.Tests;

/// <summary>Read-only calls against the real spooler. They work without elevation and change nothing.</summary>
public sealed class WinspoolSpoolerReadTests
{
    private readonly WinspoolSpooler _spooler = new();

    [Fact]
    public void The_spooler_service_is_running()
    {
        Assert.True(_spooler.IsSpoolerRunning());
    }

    [Fact]
    public void The_PDF_driver_is_listed_once()
    {
        var drivers = _spooler.GetDrivers();

        Assert.Contains("Microsoft Print To PDF", drivers, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(drivers.Count, drivers.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Ports_include_the_built_in_ones()
    {
        var ports = _spooler.GetPorts();

        Assert.Contains("FILE:", ports, StringComparer.OrdinalIgnoreCase);
        Assert.All(ports, p => Assert.False(string.IsNullOrWhiteSpace(p)));
    }

    [Fact]
    public void Printers_have_names_ports_and_drivers_and_their_jobs_can_be_listed()
    {
        var printers = _spooler.GetPrinters();

        Assert.NotEmpty(printers);
        Assert.All(printers, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.False(string.IsNullOrWhiteSpace(p.PortName));
            Assert.False(string.IsNullOrWhiteSpace(p.DriverName));
        });
        foreach (var printer in printers)
        {
            var jobs = _spooler.GetJobs(printer.Name); // read-only
            Assert.All(jobs, j => Assert.True(j.Id > 0));
        }
    }

    [Fact]
    public void The_Microsoft_PDF_printer_reports_its_driver_and_datatype()
    {
        var printer = _spooler.GetPrinters().FirstOrDefault(p => p.DriverName.Equals("Microsoft Print To PDF", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(printer);

        Assert.False(printer.WorkOffline);
        Assert.False(string.IsNullOrEmpty(printer.PrintProcessor));
        Assert.False(string.IsNullOrEmpty(printer.Datatype));
    }

    [Fact]
    public void Listing_jobs_of_an_unknown_printer_is_a_SpoolerException_with_the_win32_error()
    {
        var ex = Assert.Throws<SpoolerException>(() => _spooler.GetJobs("No such printer " + Guid.NewGuid().ToString("N")));

        Assert.Equal(1801, ex.Win32Error); // ERROR_INVALID_PRINTER_NAME
        Assert.Contains("Win32 error 1801", ex.Message);
        Assert.StartsWith("OpenPrinter('No such printer", ex.Message);
    }
}

public sealed class PrinterProvisionerTests
{
    private readonly WinFakeSpooler _spooler = new();
    private readonly MemoryRegistryStore _registry = new();
    private readonly MemoryLog _log = new();
    private ErpConfig _config = new(GlobalSettings.Defaults(), []);

    private PrinterProvisioner Create(bool admin = true) => new(_spooler, _registry, () => _config, _log, () => admin);

    private static PrinterProfile Profile(string name, string? pipe = null, string? printer = null, bool enabled = true) =>
        PrinterProfile.Create(name, new Dictionary<string, object>
        {
            ["PipeName"] = pipe ?? "ErpPrinter-" + name,
            ["PrinterName"] = printer ?? name,
            ["Enabled"] = enabled,
        });

    private void Configure(params PrinterProfile[] profiles) => _config = new ErpConfig(GlobalSettings.Defaults(), profiles);

    private string[] Managed() => (string[])_registry.GetValues(ConfigStore.RegistryRoot)["ManagedPrinters"];

    [Fact]
    public void Creates_the_port_and_the_queue_for_an_enabled_profile()
    {
        Configure(Profile("Send to Odoo", "ErpPrinter-Odoo"));

        Create().Sync();

        Assert.Contains(@"\\.\pipe\ErpPrinter-Odoo", _spooler.Ports);
        var printer = Assert.Single(_spooler.Printers);
        Assert.Equal("Send to Odoo", printer.Name);
        Assert.Equal("Microsoft Print To PDF", printer.DriverName);
        Assert.Equal(@"\\.\pipe\ErpPrinter-Odoo", printer.PortName);
        Assert.Equal("Managed by ERP Printer (profile 'Send to Odoo')", printer.Comment);
        Assert.Equal([@"Send to Odoo|\\.\pipe\ErpPrinter-Odoo"], Managed());
        Assert.Equal(RegistryValueType.MultiString, _registry.Kinds[$@"{ConfigStore.RegistryRoot}|ManagedPrinters"]);
        Assert.Contains(_log.Entries, e => e.Message == @"Created printer 'Send to Odoo' on \\.\pipe\ErpPrinter-Odoo.");
    }

    [Fact]
    public void Disabled_profiles_get_no_printer()
    {
        Configure(Profile("Off", enabled: false));

        Create().Sync();

        Assert.Empty(_spooler.Printers);
        Assert.Empty(Managed());
    }

    [Fact]
    public void A_second_sync_changes_nothing()
    {
        Configure(Profile("A"));
        var provisioner = Create();
        provisioner.Sync();
        _spooler.Calls.Clear();

        provisioner.Sync();

        Assert.DoesNotContain(_spooler.Calls, c => c.StartsWith("AddPrinter") || c.StartsWith("UpdatePrinter") || c.StartsWith("RemovePrinter"));
    }

    [Fact]
    public void Repairs_a_queue_with_the_wrong_port_or_driver()
    {
        Configure(Profile("A", "Pipe-A"));
        _spooler.Printers.Add(new PrinterInfo("A", "Some Other Driver", "LPT1:", "", PrinterStatusFlags.None, false));

        Create().Sync();

        var printer = Assert.Single(_spooler.Printers);
        Assert.Equal("Microsoft Print To PDF", printer.DriverName);
        Assert.Equal(@"\\.\pipe\Pipe-A", printer.PortName);
        Assert.Contains(_log.Entries, e => e.Message == @"Updated printer 'A' to use \\.\pipe\Pipe-A.");
    }

    [Fact]
    public void Printer_and_port_names_are_compared_case_insensitively()
    {
        Configure(Profile("A", "Pipe-A", "My Printer"));
        _spooler.Ports.Add(@"\\.\pipe\pipe-a");
        _spooler.Printers.Add(new PrinterInfo("MY PRINTER", "microsoft print to pdf", @"\\.\pipe\pipe-a", "", PrinterStatusFlags.None, false));

        Create().Sync();

        Assert.DoesNotContain(_spooler.Calls, c => c.StartsWith("AddPrinter") || c.StartsWith("UpdatePrinter"));
    }

    [Fact]
    public void Removes_only_the_printers_it_created_when_a_profile_is_removed_or_disabled()
    {
        Configure(Profile("Keep"), Profile("Gone"));
        _spooler.Printers.Add(new PrinterInfo("Somebody else's printer", "Other", "LPT1:", "", PrinterStatusFlags.None, false));
        var provisioner = Create();
        provisioner.Sync();
        Assert.Equal(3, _spooler.Printers.Count);

        Configure(Profile("Keep"), Profile("Gone", enabled: false));
        provisioner.Sync();

        Assert.Equal(["Keep", "Somebody else's printer"], _spooler.Printers.Select(p => p.Name).Order());
        Assert.DoesNotContain(@"\\.\pipe\ErpPrinter-Gone", _spooler.Ports);
        Assert.Contains(@"\\.\pipe\ErpPrinter-Keep", _spooler.Ports);
        Assert.Equal([@"Keep|\\.\pipe\ErpPrinter-Keep"], Managed());
        Assert.Contains(_log.Entries, e => e.Message == "Removed printer 'Gone'.");
    }

    [Fact]
    public void A_printer_that_moved_to_a_new_pipe_is_kept_and_the_old_port_is_removed()
    {
        Configure(Profile("A", "Old"));
        var provisioner = Create();
        provisioner.Sync();

        Configure(Profile("A", "New"));
        provisioner.Sync();

        var printer = Assert.Single(_spooler.Printers);
        Assert.Equal(@"\\.\pipe\New", printer.PortName);
        Assert.DoesNotContain(_spooler.Calls, c => c == "RemovePrinter:A");
        Assert.DoesNotContain(@"\\.\pipe\Old", _spooler.Ports);
        Assert.Equal([@"A|\\.\pipe\New"], Managed());
    }

    [Fact]
    public void A_renamed_printer_replaces_the_old_queue()
    {
        Configure(Profile("A", "Pipe", "Old name"));
        var provisioner = Create();
        provisioner.Sync();

        Configure(Profile("A", "Pipe", "New name"));
        provisioner.Sync();

        Assert.Equal(["New name"], _spooler.Printers.Select(p => p.Name));
        Assert.Equal([@"New name|\\.\pipe\Pipe"], Managed());
        Assert.Contains(@"\\.\pipe\Pipe", _spooler.Ports); // the new queue uses the same port, so it stays
    }

    [Fact]
    public void One_failing_printer_does_not_stop_the_others_and_all_failures_are_thrown_together()
    {
        Configure(Profile("Good"), Profile("Bad1"), Profile("Bad2"));
        _spooler.Fault = call => call.StartsWith("AddPrinter:Bad") ? new SpoolerException($"{call} failed: access denied (Win32 error 5).", 5) : null;

        var ex = Assert.Throws<PrinterProvisioningException>(() => Create().Sync());

        Assert.Equal(["Good"], _spooler.Printers.Select(p => p.Name));
        Assert.Equal(2, ex.Errors.Count);
        Assert.StartsWith("Bad1: AddPrinter:Bad1 failed", ex.Errors[0]);
        Assert.StartsWith("Bad2: AddPrinter:Bad2 failed", ex.Errors[1]);
        Assert.StartsWith("Some printers could not be provisioned:\nBad1:", ex.Message);
        Assert.Equal(2, ex.Failures.Count);
        Assert.Equal([@"Good|\\.\pipe\ErpPrinter-Good"], Managed()); // saved before throwing
    }

    [Fact]
    public void A_queue_that_cannot_be_removed_stays_in_the_managed_list_so_the_next_sync_retries()
    {
        Configure(Profile("A"));
        var provisioner = Create();
        provisioner.Sync();
        Configure();
        _spooler.Fault = call => call == "RemovePrinter:A" ? new SpoolerException("RemovePrinter failed (Win32 error 5).", 5) : null;

        Assert.Throws<PrinterProvisioningException>(provisioner.Sync);
        Assert.Equal([@"A|\\.\pipe\ErpPrinter-A"], Managed());

        _spooler.Fault = null;
        provisioner.Sync();
        Assert.Empty(_spooler.Printers);
        Assert.Empty(Managed());
    }

    [Fact]
    public void Needs_administrator_rights()
    {
        Configure(Profile("A"));

        Assert.Throws<UnauthorizedAccessException>(() => Create(admin: false).Sync());
        Assert.Empty(_spooler.Printers);
    }

    [Fact]
    public void Managed_printers_are_read_from_the_pipe_separated_lines()
    {
        _registry.SetValue(ConfigStore.RegistryRoot, "ManagedPrinters", new[] { @"A|\\.\pipe\x", "garbage without separator", "", @"B|\\.\pipe\y" }, RegistryValueType.MultiString);

        var managed = Create().GetManagedPrinters();

        Assert.Equal([new ManagedPrinter("A", @"\\.\pipe\x"), new ManagedPrinter("B", @"\\.\pipe\y")], managed);
        Assert.Empty(new PrinterProvisioner(_spooler, new MemoryRegistryStore(), () => _config).GetManagedPrinters());
    }
}

public sealed class PdfDriverInstallerTests
{
    private sealed class FakeRunner : IProcessRunner
    {
        public List<(string File, string Arguments)> Runs { get; } = [];
        public int ExitCode { get; set; }
        public Action? OnRun { get; set; }

        public ProcessResult Run(string fileName, string arguments, TimeSpan timeout)
        {
            Runs.Add((fileName, arguments));
            OnRun?.Invoke();
            return new ProcessResult(ExitCode, "");
        }
    }

    [Fact]
    public void Does_nothing_when_the_driver_exists()
    {
        var runner = new FakeRunner();
        var installs = 0;

        new PdfDriverInstaller(new WinFakeSpooler(), runner: runner, installFromStore: _ => { installs++; return 0; }).EnsureInstalled();

        Assert.Equal(0, installs);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public void Installs_from_the_driver_store_first()
    {
        var spooler = new WinFakeSpooler();
        spooler.Drivers.Clear();
        var runner = new FakeRunner();

        new PdfDriverInstaller(spooler, runner: runner, installFromStore: name =>
        {
            Assert.Equal("Microsoft Print To PDF", name);
            spooler.Drivers.Add(name);
            return 0;
        }).EnsureInstalled();

        Assert.Empty(runner.Runs);
    }

    [Fact]
    public void Falls_back_to_enabling_the_Windows_feature_with_DISM()
    {
        var spooler = new WinFakeSpooler();
        spooler.Drivers.Clear();
        var log = new MemoryLog();
        var runner = new FakeRunner { ExitCode = 3010, OnRun = () => spooler.Drivers.Add("Microsoft Print To PDF") };

        new PdfDriverInstaller(spooler, log, runner, installFromStore: _ => unchecked((int)0x80070002)).EnsureInstalled();

        var run = Assert.Single(runner.Runs);
        Assert.EndsWith("dism.exe", run.File);
        Assert.Equal("/Online /Enable-Feature /FeatureName:Printing-PrintToPDFServices-Features /All /NoRestart", run.Arguments);
        Assert.Contains(log.Entries, e => e.Level == ErpLogLevel.Warning && e.Message.Contains("0x80070002"));
    }

    [Fact]
    public void Throws_when_the_driver_is_still_missing()
    {
        var spooler = new WinFakeSpooler();
        spooler.Drivers.Clear();
        var installer = new PdfDriverInstaller(spooler, runner: new FakeRunner { ExitCode = 50 }, installFromStore: _ => -1);

        var ex = Assert.Throws<SpoolerException>(installer.EnsureInstalled);

        Assert.Equal("The 'Microsoft Print To PDF' driver is not available on this machine.", ex.Message);
    }
}

public sealed class DataRootInitializerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ErpPrinterTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            RestoreAccess(new DirectoryInfo(_root));
            Directory.Delete(_root, true);
        }
        catch (Exception)
        {
            // Best effort: a temp folder.
        }
    }

    private static void RestoreAccess(DirectoryInfo directory, bool recurse = true)
    {
        if (!directory.Exists) return;
        var security = new DirectorySecurity();
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
        if (!recurse) return; // the inheritable entry reaches the children by itself
        foreach (var child in directory.EnumerateDirectories()) RestoreAccess(child);
    }

    private static List<string> Sids(DirectoryInfo directory)
    {
        var security = directory.GetAccessControl(AccessControlSections.Access);
        return security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow).Select(r => r.IdentityReference.Value).Distinct().Order().ToList();
    }

    [Fact]
    public void Creates_the_folders_and_locks_them_to_system_and_administrators()
    {
        DataRootInitializer.Initialize(_root);

        // The folder is locked now, so an unelevated test user can only inspect the ACLs (which also proves the folders exist).
        var root = new DirectoryInfo(_root);
        Assert.True(root.GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected);
        Assert.Equal(["S-1-5-18", "S-1-5-32-544"], Sids(root));
        foreach (var sub in new[] { "spool", "logs", "updates" })
            Assert.Equal(["S-1-5-18", "S-1-5-32-544"], Sids(new DirectoryInfo(Path.Combine(_root, sub))));
    }

    [Fact]
    public void Existing_folders_inside_are_covered_too_and_running_it_twice_is_fine()
    {
        Directory.CreateDirectory(Path.Combine(_root, "spool", "Send_to_Odoo", "outbox"));

        DataRootInitializer.Initialize(_root);
        RestoreAccess(new DirectoryInfo(_root), recurse: false); // an unelevated user cannot use the locked folder; give access back and run it again
        DataRootInitializer.Initialize(_root);

        Assert.Equal(["S-1-5-18", "S-1-5-32-544"], Sids(new DirectoryInfo(Path.Combine(_root, "spool", "Send_to_Odoo", "outbox"))));
    }

    [Fact]
    public void The_security_grants_full_control_with_inheritance_to_exactly_two_accounts()
    {
        var security = DataRootInitializer.CreateSecurity();

        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.Equal(2, rules.Count);
        Assert.All(rules, r =>
        {
            Assert.Equal(FileSystemRights.FullControl, r.FileSystemRights);
            Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, r.InheritanceFlags);
        });
    }
}

public sealed class WindowsAdministratorTests
{
    [Fact]
    public void IsAdministrator_agrees_with_Assert()
    {
        if (WindowsAdministrator.IsAdministrator()) WindowsAdministrator.Assert();
        else Assert.Throws<UnauthorizedAccessException>(WindowsAdministrator.Assert);
    }
}

/// <summary>
/// Changes the real spooler. Opt in with ERP_PRINTER_ADMIN_TESTS=1 in an elevated shell. Uses a made-up printer and pipe name,
/// never touches existing printers, and removes what it created.
/// </summary>
public sealed class WinspoolAdminTests
{
    [WinAdminFact]
    public void Provisioning_creates_repairs_and_removes_a_queue_on_the_real_spooler()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var printerName = "ERP Printer Test " + suffix;
        var pipe = "ErpPrinterTest-" + suffix;
        var port = @"\\.\pipe\" + pipe;
        var spooler = new WinspoolSpooler();
        var registry = new MemoryRegistryStore();
        var profile = PrinterProfile.Create("Test " + suffix, new Dictionary<string, object> { ["PipeName"] = pipe, ["PrinterName"] = printerName });
        var config = new ErpConfig(GlobalSettings.Defaults(), [profile]);
        var provisioner = new PrinterProvisioner(spooler, registry, () => config);

        try
        {
            new PdfDriverInstaller(spooler).EnsureInstalled();
            provisioner.Sync();

            Assert.Contains(port, spooler.GetPorts());
            var printer = Assert.Single(spooler.GetPrinters(), p => p.Name == printerName);
            Assert.Equal(port, printer.PortName);
            Assert.Equal("Microsoft Print To PDF", printer.DriverName);
            Assert.Empty(spooler.GetJobs(printerName));

            // Repair: point the queue somewhere else, then let Sync fix it.
            spooler.UpdatePrinter(printerName, "Microsoft Print To PDF", "FILE:", "changed");
            provisioner.Sync();
            Assert.Equal(port, spooler.GetPrinters().Single(p => p.Name == printerName).PortName);

            config = new ErpConfig(GlobalSettings.Defaults(), []);
            provisioner.Sync();

            Assert.DoesNotContain(spooler.GetPrinters(), p => p.Name == printerName);
            Assert.DoesNotContain(port, spooler.GetPorts());
        }
        finally
        {
            try { spooler.RemovePrinter(printerName); } catch (SpoolerException) { /* already gone */ }
            try { spooler.RemovePort(port); } catch (SpoolerException) { /* already gone */ }
        }
    }
}
