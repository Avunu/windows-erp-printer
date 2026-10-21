# C# port: architecture and conventions

This solution is a port of the PowerShell product in `../src` to C# (.NET 10). The PowerShell product keeps shipping until
the port reaches parity; the port reuses its registry layout, data folder, outbox files and log format, so an installation
can be upgraded in place.

## Why

Both field failures so far were invisible failures (jobs that stopped inside the spooler, a service that died without a
trace), and the product's weakest dependency is the PowerShell host itself (execution policy, Constrained Language Mode,
AppLocker, the scheduled-task watchdog). A native service gives: a real Windows service with recovery actions, no host
dependency, direct access to winspool (job status flags, change notifications) and typed, testable code.

## Projects

| Project | Target | Contents |
| --- | --- | --- |
| `ErpPrinter.Core` | net10.0 | Portable logic with no Windows APIs: settings schema and resolution, secrets, outbox, HTTP and the four backends, update manifest logic, diagnostics decisions. Builds and tests on any OS. |
| `ErpPrinter.Windows` | net10.0-windows | Registry, DPAPI, winspool P/Invoke (`ISpooler`), printer/port provisioning, the named-pipe listener, log files and event log, scheduled-task/service control, diagnostics checks, test print, updater. |
| `ErpPrinter.Service` | net10.0-windows | The `ErpPrinter` Windows service: listener + uploader + updater in one process. |
| `ErpPrinter.Cli` | net10.0-windows | `erpprinter.exe`: status, test, diagnose, testprint, retry, upload, update, install, uninstall, sync, config, schema. |
| `ErpPrinter.Config` | net10.0-windows | WinForms configuration GUI (schema-driven). |
| `tests/*` | | xUnit. `Core.Tests` run anywhere; `Windows.Tests` need Windows and never touch the real spooler or HKLM unless a test says so. |

Dependency direction: `Core` <- `Windows` <- `{Service, Cli, Config}`. Core never references Windows types; anything it needs from
Windows is an interface in Core (`IRegistryStore`, `ISecretProtector`, `ISpooler`, `IErpLog`, `IBackendProvider`).

## Conventions (keep behaviour identical to the PowerShell product unless a change is called out)

- Registry layout: `HKLM\SOFTWARE\ErpPrinter` (+ `\Printers\<name>`, `\Secrets\<name>`), policy under `HKLM\SOFTWARE\Policies\ErpPrinter`.
  Effective value = policy, else machine, else schema default. Paths passed to `IRegistryStore` are relative to HKLM.
- Data folder `DataRoot` (default `%ProgramData%\ErpPrinter`): `spool\<safe profile name>\{outbox,failed,sent}`, `delivered.json`, `logs`, `updates`.
- Outbox sidecars (`<id>.json`, camelCase, ISO 8601 timestamps) are written after the PDF, each through a `.tmp` rename; `<id>.json` existing means the PDF is complete.
- Log lines: `yyyy-MM-dd HH:mm:ss.fff [Level      ] [component] message` in `<DataRoot>\logs\<component>-yyyyMMdd.log` (level padded to 11 characters).
  Components: `listener`, `uploader`, `updater`, `cli`, `install`, `service`. Event log source `ErpPrinter` (Application); ids 1001 queued,
  1002 delivered, 1010/1011 update started/installed, 2000/2001/2002 warning/log-write failure/retry, 3000/3002 error/failed document.
- The Windows service is named `ErpPrinter` (display name "ERP Printer"). The legacy scheduled tasks `\ERP Printer\{Listener,Uploader,Updater}` are
  removed on install/upgrade. Kernel objects keep the PowerShell names: event `Global\ErpPrinterOutbox`, mutex `Global\ErpPrinterUploader`.
- Printers use the `Microsoft Print To PDF` driver on a local port named `\\.\pipe\<PipeName>`; the listener hosts the pipe, one job per connection.
- Time: take a `TimeProvider` (never `DateTime.Now` in logic), so retry/backoff tests are deterministic.
- Async all the way down for I/O; every long-running loop takes a `CancellationToken`.
- Warnings are errors. Nullable is on. No `#pragma warning disable` without a comment saying why.

## Authoring notes for this environment

- **Do not write C# (or any file containing backslashes) through bash heredocs**: the shell layer collapses `\\` to `\` (this broke
  `\\.\pipe\` once). Use the editor/Write tools for source files.
- `dotnet` needs the machine PATH refreshed in a PowerShell tool: `$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')`.
- A repo-local `nuget.config` pins nuget.org because the user-level one is misconfigured. Do not edit the user-level config.
- Tests must not write to the real Application event log, HKLM, the real spooler or `%ProgramData%`.
