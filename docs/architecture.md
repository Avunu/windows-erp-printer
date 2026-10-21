# Architecture and conventions

ERP Printer is a .NET 10 solution: a Windows service that receives print jobs from a named-pipe printer port, queues them in a local outbox and delivers them to an ERP. Building, packaging and releases are in [development.md](development.md); this page covers the structure and the conventions every change must follow.

## Why a native service

The product used to be written in PowerShell and run as scheduled tasks. Two field failures were invisible failures (jobs that stopped inside the spooler, a service that died without a trace), and its weakest dependency was the PowerShell host itself (execution policy, Constrained Language Mode, AppLocker, the scheduled-task watchdog). A native service gives a real Windows service with recovery actions, no host dependency, direct access to winspool (job status flags) and typed, testable code. Installations of the PowerShell version are upgraded in place, see [upgrading.md](upgrading.md).

## Projects

| Project | Target | Contents |
| --- | --- | --- |
| `ErpPrinter.Core` | net10.0 | Portable logic with no Windows APIs: settings schema and resolution, secrets, outbox, HTTP and the four backends, update manifest logic, diagnostics decisions, the policy-template generators. Builds and tests on any OS. |
| `ErpPrinter.Windows` | net10.0-windows | Registry, DPAPI, winspool P/Invoke (`ISpooler`), printer/port provisioning, the named-pipe listener, log files and event log, service control, diagnostics checks, test print, updater, install/uninstall, and the composition root (`ErpPrinterContext`). |
| `ErpPrinter.Service` | net10.0-windows | The `ErpPrinter` Windows service: listener + uploader + updater in one process. |
| `ErpPrinter.Cli` | net10.0-windows | `erpprinter.exe`: status, test, diagnose, testprint, retry, upload, update, install, uninstall, sync, profile, set, secret, config, schema. |
| `ErpPrinter.Config` | net10.0-windows | WinForms configuration window (schema-driven). |
| `tests/*` | | xUnit. `Core.Tests` run anywhere; `Windows.Tests` and `Config.Tests` need Windows and never touch the real spooler or HKLM unless a test says so. |

Dependency direction: `Core` <- `Windows` <- `{Service, Cli, Config}`. Core never references Windows types; anything it needs from Windows is an interface in Core (`IRegistryStore`, `ISecretProtector`, `ISpooler`, `IErpLog`, `IBackendProvider`, `IJobSink`, `IOutboxSignal`). `ErpPrinterContext` builds the object graph once, so the service, the command line and the configuration window run on identical wiring.

## Conventions

- Registry layout: `HKLM\SOFTWARE\ErpPrinter` (+ `\Printers\<name>`, `\Secrets\<name>`), policy under `HKLM\SOFTWARE\Policies\ErpPrinter`. Effective value = policy, else machine, else schema default. Paths passed to `IRegistryStore` are relative to HKLM. This layout is shared with every earlier release; do not change it.
- Data folder `DataRoot` (default `%ProgramData%\ErpPrinter`): `spool\<safe profile name>\{outbox,failed,sent}`, `delivered.json`, `logs`, `updates`.
- Outbox sidecars (`<id>.json`, camelCase, ISO 8601 timestamps) are written after the PDF, each through a `.tmp` rename; `<id>.json` existing means the PDF is complete.
- Log lines: `yyyy-MM-dd HH:mm:ss.fff [Level      ] [component] message` in `<DataRoot>\logs\<component>-yyyyMMdd.log` (level padded to 11 characters). Components: `listener`, `uploader`, `updater`, `cli`, `install`, `service`. Event log source `ErpPrinter` (Application); ids 1001 queued, 1002 delivered, 1010/1011 update started/installed, 2000/2001/2002 warning/log-write failure/retry, 3000/3002 error/failed document.
- The Windows service is named `ErpPrinter` (display name "ERP Printer"). Kernel objects: event `Global\ErpPrinterOutbox`, mutex `Global\ErpPrinterUploader`.
- Printers use the `Microsoft Print To PDF` driver on a local port named `\\.\pipe\<PipeName>`; the listener hosts the pipe, one job per connection.
- Time: take a `TimeProvider` (never `DateTime.Now` in logic), so retry/backoff tests are deterministic.
- Async all the way down for I/O; every long-running loop takes a `CancellationToken`.
- Warnings are errors. Nullable is on. No `#pragma warning disable` without a comment saying why.
- The secrets registry key is readable only by SYSTEM and Administrators, and an unreadable key looks exactly like a missing one. Outside an elevated session "no secret stored" proves nothing: never report it as a problem, and say so when showing it.
- Failures must leave a trace. A long-running part logs its own crash before it ends, and `erpprinter.exe` logs any failure to the `cli`/`install` log, because the MSI and the service have no console.

## Authoring notes

- Do not write source files through shell heredocs: some shell layers collapse `\\` to `\` (this once broke `\\.\pipe\`). Use an editor.
- A repo-local `nuget.config` pins nuget.org so a machine-level NuGet configuration with a wrong or missing source cannot break restore.
- Tests must not write to the real Application event log, HKLM, the real spooler or `%ProgramData%`.
