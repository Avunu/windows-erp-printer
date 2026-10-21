using System.Diagnostics;
using ErpPrinter.Logging;
using ErpPrinter.Outbox;
using ErpPrinter.Policy;
using ErpPrinter.Settings;
using ErpPrinter.Updates;
using ErpPrinter.Windows.Hosting;
using ErpPrinter.Windows.Listening;
using ErpPrinter.Windows.Setup;

namespace ErpPrinter.Cli;

internal static class Program
{
    private const string Usage = """
        erpprinter <command> [options]

        Commands:
          status                       Service, printer queues and outbox counts per profile.
          test <profile>               Test the connection of a profile (--profile <name> also works).
          diagnose [--profile <name>] [--skip-connection]
                                       Check every link from the printer to the ERP and save a report. Run elevated.
          testprint <profile> [--timeout <seconds>]
                                       Print a test page and follow it to the ERP.
          retry [--profile <name>] [--id <id>]
                                       Move failed documents back to the outbox.
          upload --once [--force]      Run one delivery pass now (the service normally does this).
          update [--check-only] [--force]
                                       Check for and install updates.
          install Name=Value ...       Provision printers, folders and settings (the MSI runs this).
          uninstall [Purge=1]          Remove printers and ports (the MSI runs this).
          sync                         Recreate printer queues from the current profiles.
          config                       Open the configuration window.
          schema --admx <dir> | --settings-md <file>
                                       Write the policy templates or the settings reference from the schema.
          version                      Show the version.

        Whatever goes wrong is also written to <data folder>\logs\cli-yyyyMMdd.log.
        """;

    private static async Task<int> Main(string[] args)
    {
        var cli = CommandLine.Parse(args);
        if (cli.Command is "help" or "--help" or "-h" or "/?")
        {
            Console.WriteLine(Usage);
            return 0;
        }
        if (cli.Command == "version")
        {
            Console.WriteLine($"{BuildInfo.Version} ({BuildInfo.Commit})");
            return 0;
        }
        if (cli.Command == "schema") return Schema(cli);

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

        var component = cli.Command is "install" or "uninstall" ? "install" : "cli";
        using var context = new ErpPrinterContext(component);
        context.Log.Echo = (level, message) =>
        {
            // The command's own output is the console; log lines are only echoed for problems.
            if (level <= ErpLogLevel.Warning) Console.Error.WriteLine(message);
        };
        try
        {
            context.LoadConfig();
            if (cli.Command is not ("install" or "uninstall" or "version"))
                context.Log.Verbose($"Running '{cli.Command}' as {System.Security.Principal.WindowsIdentity.GetCurrent().Name}.");
            return cli.Command switch
            {
                "status" => Status(context),
                "test" => await TestAsync(context, cli, cancellation.Token),
                "diagnose" => await DiagnoseAsync(context, cli, cancellation.Token),
                "testprint" => await TestPrintAsync(context, cli, cancellation.Token),
                "retry" => Retry(context, cli),
                "upload" => await UploadAsync(context, cli, cancellation.Token),
                "update" => await UpdateAsync(context, cli, cancellation.Token),
                "install" => Install(context, cli),
                "uninstall" => Uninstall(context, cli),
                "sync" => Sync(context),
                "config" => Config(),
                _ => Unknown(cli.Command),
            };
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception ex)
        {
            // A failure inside a task nobody watches (the MSI, a scheduled run) must leave a trace.
            context.Log.Exception($"'{cli.Command}' failed", ex);
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.{Environment.NewLine}{Environment.NewLine}{Usage}");
        return 1;
    }

    private static PrinterProfile RequireProfile(ErpPrinterContext context, CommandLine cli)
    {
        var name = cli.Profile ?? throw new ArgumentException("A profile name is required: --profile <name>.");
        return context.Store.GetProfile(name) ?? throw new ArgumentException($"No printer profile named '{name}'.");
    }

    private static int Status(ErpPrinterContext context)
    {
        var status = context.Status.Get();
        Console.WriteLine($"Version  {status.Version}");
        Console.WriteLine($"Service  {status.Service}");
        Console.WriteLine($"DataRoot {status.DataRoot}");
        Console.WriteLine();
        var rows = status.Profiles.Select(p => new[]
        {
            p.Profile, p.Enabled ? "yes" : "no", p.Backend, p.Printer, p.PrinterOk ? "OK" : p.Enabled ? "Missing" : "Disabled",
            p.Pending.ToString(), p.Failed.ToString(), p.Sent.ToString(), p.LastSent?.ToString("g") ?? "", p.LastRemoteId ?? "",
        }).ToList();
        Table(["Profile", "Enabled", "Backend", "Printer", "Queue", "Pending", "Failed", "Sent", "Last sent", "Remote id"], rows);
        foreach (var p in status.Profiles.Where(p => !string.IsNullOrEmpty(p.LastError)))
            Console.WriteLine($"[{p.Profile}] last error: {p.LastError}");
        return 0;
    }

    private static async Task<int> TestAsync(ErpPrinterContext context, CommandLine cli, CancellationToken ct)
    {
        var profile = RequireProfile(context, cli);
        var result = await context.Connections.TestAsync(profile, context.LoadConfig().Global, null, ct);
        Console.WriteLine(result.Message);
        return result.Success ? 0 : 2;
    }

    private static async Task<int> DiagnoseAsync(ErpPrinterContext context, CommandLine cli, CancellationToken ct)
    {
        var config = context.LoadConfig();
        var runner = context.CreateDiagnostics(config);
        var report = await runner.RunAsync(config, new Diagnostics.DiagnosticsOptions(cli.Profile, cli.Has("skip-connection")), ct);
        Console.WriteLine(report.Report);
        if (report.ReportPath is not null) Console.WriteLine($"Report saved to {report.ReportPath}");
        return report.Failures > 0 ? 2 : 0;
    }

    private static async Task<int> TestPrintAsync(ErpPrinterContext context, CommandLine cli, CancellationToken ct)
    {
        var profile = RequireProfile(context, cli);
        var config = context.LoadConfig();
        var options = new Diagnostics.TestPrintOptions(cli.GetInt("timeout", 60)) { OnProgress = Console.WriteLine };
        var result = await context.CreateTestPrint(config).RunAsync(config, profile.Name, options, ct);
        Console.WriteLine(result.Report);
        return result.Success ? 0 : 2;
    }

    private static int Retry(ErpPrinterContext context, CommandLine cli)
    {
        using var signal = new NamedOutboxSignal();
        var count = context.CreateProcessor(signal).RetryFailed(context.LoadConfig(), cli.Get("profile"), cli.Get("id"));
        Console.WriteLine($"{count} document(s) re-queued.");
        return 0;
    }

    private static async Task<int> UploadAsync(ErpPrinterContext context, CommandLine cli, CancellationToken ct)
    {
        if (!cli.Has("once"))
        {
            Console.Error.WriteLine("The uploader runs inside the ErpPrinter service. Use 'upload --once [--force]' for a single pass.");
            return 1;
        }
        var results = await context.CreateProcessor().RunPassAsync(context.LoadConfig(), ignoreBackoff: cli.Has("force"), ct);
        Table(["Profile", "Sent", "Retrying", "Failed"], results.Select(r => new[] { r.Profile, r.Sent.ToString(), r.Retrying.ToString(), r.Failed.ToString() }).ToList());
        return 0;
    }

    private static async Task<int> UpdateAsync(ErpPrinterContext context, CommandLine cli, CancellationToken ct)
    {
        var runner = new UpdateRunner(context.Http, new MsiexecInstaller(), context.Log, context.Time);
        var result = await runner.RunAsync(context.LoadConfig().Global, cli.Has("check-only"), cli.Has("force"), ct);
        Console.WriteLine($"CurrentVersion   : {result.CurrentVersion}");
        Console.WriteLine($"AvailableVersion : {result.AvailableVersion}");
        Console.WriteLine($"UpdateAvailable  : {result.UpdateAvailable}");
        Console.WriteLine($"Installed        : {result.Installed}");
        Console.WriteLine($"Message          : {result.Message}");
        return 0;
    }

    private static int Install(ErpPrinterContext context, CommandLine cli)
    {
        string Pair(string name) => cli.Pairs.TryGetValue(name, out var value) ? value : "";
        var options = new InstallOptions(
            InstallDir: AppContext.BaseDirectory.TrimEnd('\\'),
            ProfileName: Pair("ProfileName") is { Length: > 0 } name ? name : "Send to ERP",
            Backend: Pair("Backend"), ServerUrl: Pair("ServerUrl"), Database: Pair("Database"), Username: Pair("Username"),
            ApiKey: Pair("ApiKey"), ApiSecret: Pair("ApiSecret"), AutoUpdate: Pair("AutoUpdate"), UpdateManifestUrl: Pair("UpdateManifestUrl"));
        new InstallCommands(context).Install(options);
        return 0;
    }

    private static int Uninstall(ErpPrinterContext context, CommandLine cli)
    {
        new InstallCommands(context).Uninstall(purge: cli.Pairs.TryGetValue("Purge", out var purge) && purge == "1");
        return 0;
    }

    private static int Sync(ErpPrinterContext context)
    {
        WindowsAdministrator.Assert();
        context.PdfDriver.EnsureInstalled();
        context.Provisioner.Sync();
        Console.WriteLine("Printers match the enabled profiles.");
        return 0;
    }

    private static int Config()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "ErpPrinterConfig.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("ErpPrinterConfig.exe is not next to erpprinter.exe.", exe);
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        return 0;
    }

    private static int Schema(CommandLine cli)
    {
        if (cli.Get("admx") is { } dir)
        {
            PolicyTemplateGenerator.WriteTo(dir);
            Console.WriteLine($"Wrote ADMX/ADML to {dir}");
            return 0;
        }
        if (cli.Get("settings-md") is { } file)
        {
            SettingsReferenceGenerator.WriteTo(file);
            Console.WriteLine($"Wrote {file}");
            return 0;
        }
        Console.Error.WriteLine("schema needs --admx <dir> or --settings-md <file>.");
        return 1;
    }

    private static void Table(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        var widths = headers.Select((h, i) => Math.Max(h.Length, rows.Select(r => r[i].Length).DefaultIfEmpty(0).Max())).ToArray();
        string Line(IReadOnlyList<string> cells) => string.Join("  ", cells.Select((c, i) => c.PadRight(widths[i]))).TrimEnd();
        Console.WriteLine(Line(headers));
        Console.WriteLine(Line(widths.Select(w => new string('-', w)).ToArray()));
        foreach (var row in rows) Console.WriteLine(Line(row));
    }
}
