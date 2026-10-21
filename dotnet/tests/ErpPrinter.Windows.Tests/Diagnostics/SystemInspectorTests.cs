using System.IO.Pipes;
using System.Security.Principal;
using ErpPrinter.Diagnostics;
using ErpPrinter.Windows.Diagnostics;

namespace ErpPrinter.Windows.Tests.Diagnostics;

public class SystemInspectorTests
{
    private readonly SystemInspector _inspector = new();

    [Fact]
    public void Reports_the_elevation_of_this_session()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var expected = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

        Assert.Equal(expected, _inspector.IsElevated);
    }

    [Fact]
    public void Describes_the_machine_and_the_user()
    {
        Assert.Equal(Environment.MachineName, _inspector.ComputerName);
        Assert.Contains('\\', _inspector.CurrentUser);
        Assert.Contains(", version ", _inspector.OperatingSystem);
    }

    [Fact]
    public void Reports_free_disk_space_of_the_volume()
    {
        var space = _inspector.GetFreeSpace(Path.GetTempPath());

        Assert.True(space.FreeBytes > 0);
        Assert.Equal(Path.GetPathRoot(Path.GetTempPath()), space.Volume, ignoreCase: true);
    }

    [Fact]
    public async Task Probes_whether_a_folder_is_writable()
    {
        var ok = await _inspector.ProbeWritableAsync(Path.GetTempPath());
        Assert.True(ok.Writable);
        Assert.Null(ok.Error);

        var missing = await _inspector.ProbeWritableAsync(Path.Combine(Path.GetTempPath(), "erp-nothing-" + Guid.NewGuid().ToString("N")));
        Assert.False(missing.Writable);
        Assert.NotEmpty(missing.Error!);
    }

    [Fact]
    public async Task Leaves_no_probe_file_behind()
    {
        var dir = Path.Combine(Path.GetTempPath(), "erp-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await _inspector.ProbeWritableAsync(dir);
            Assert.Empty(Directory.GetFileSystemEntries(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Sees_a_pipe_that_a_test_hosts_without_connecting_to_it()
    {
        var name = "ErpPrinter-DiagTest-" + Guid.NewGuid().ToString("N");
        Assert.False(_inspector.PipeExists(name));

        await using (var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        {
            Assert.True(_inspector.PipeExists(name));
            Assert.True(_inspector.PipeExists(name.ToUpperInvariant()), "pipe names are case-insensitive");

            // The listener's only instance must still be free: a client can still connect after the inspection.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var waiting = server.WaitForConnectionAsync(cts.Token);
            await using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
            await client.ConnectAsync(cts.Token);
            await waiting;
        }

        Assert.False(_inspector.PipeExists(name));
    }

    [Fact]
    public void Finds_the_print_spooler_service_and_not_a_made_up_one()
    {
        var spooler = _inspector.GetService("Spooler");
        Assert.NotNull(spooler);
        Assert.Equal("Spooler", spooler.Name);
        Assert.Contains(spooler.State, new[] { "Running", "Stopped", "StartPending", "StopPending", "Paused", "PausePending", "ContinuePending" });
        Assert.Contains(spooler.StartType, new[] { "Automatic", "Manual", "Disabled", "Boot", "System" });

        Assert.Null(_inspector.GetService("ErpPrinter-no-such-service-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void Lists_legacy_tasks_and_processes_without_throwing()
    {
        Assert.NotNull(_inspector.GetLegacyTasks());
        _ = _inspector.GetErpProcesses();
        _ = _inspector.GetProtectedPrintMode();
        _ = _inspector.GetUserProxy();
    }

    [Fact]
    public void Parses_schtasks_csv_output_for_the_legacy_folder_only()
    {
        const string csv = """
            "PC1","\Microsoft\Windows\Defrag\ScheduledDefrag","N/A","Ready","Interactive/Background","30.11.1999 00:00:00","267011"
            "PC1","\ERP Printer\Listener","N/A","Running","SYSTEM","20.10.2026 08:00:00","267009"
            "PC1","\ERP Printer\Updater","20.10.2026 12:00:00","Ready","SYSTEM","N/A","x"
            garbage line
            """;

        var tasks = SystemInspector.ParseSchtasksCsv(csv);

        Assert.Equal(["Listener", "Updater"], tasks.Select(t => t.Name));
        Assert.Equal("Running", tasks[0].State);
        Assert.Equal(267009, tasks[0].LastResult);
        Assert.True(tasks[0].IsRunning);
        Assert.Null(tasks[1].LastResult);
        Assert.Null(tasks[1].LastRun);
    }
}
