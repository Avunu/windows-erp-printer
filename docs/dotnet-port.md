# The C# port

ERP Printer is being ported from PowerShell to C# (.NET 10). The PowerShell product in `src/` keeps shipping, unchanged, until the port reaches parity; the port reuses its registry layout, data folder, outbox files and log format so that an installation upgrades **in place**. This page covers the packaging side. For the code architecture and conventions see [dotnet/ARCHITECTURE.md](../dotnet/ARCHITECTURE.md).

## Status

| Piece | State |
| --- | --- |
| Solution, shared foundation (`ErpPrinter.Core`), tests | in progress (`dotnet/`) |
| Service host, CLI, configuration GUI, Windows plumbing, backends | in progress; the executables are stubs until they land |
| Publish layout (`build/Publish-Dotnet.ps1`) | done |
| MSI (`installer/ErpPrinter.Service.wxs`) | builds and validates; **not yet installed anywhere** (see [Verification](#verification-state)) |
| CI (`.github/workflows/dotnet.yml`) | done |
| Release assets | **not wired**: releases still ship only the PowerShell MSI (see [Releases](#releases)) |

## Layout

```
dotnet/
  ErpPrinter.slnx             solution
  Directory.Build.props       shared settings and THE VERSION (marked for release-please)
  Publish.props               publish settings of the three executables (self-contained, ReadyToRun, no trimming)
  nuget.config                pins nuget.org
  src/ErpPrinter.Core         portable logic (no Windows APIs)
  src/ErpPrinter.Windows      registry, DPAPI, spooler, pipes, event log, services
  src/ErpPrinter.Service      ErpPrinterService.exe  - the "ErpPrinter" Windows service
  src/ErpPrinter.Cli          erpprinter.exe         - command line (install, uninstall, status, ...)
  src/ErpPrinter.Config       ErpPrinterConfig.exe   - configuration GUI (requires administrator)
  tests/                      xUnit: Core.Tests (any OS), Windows.Tests (Windows)
installer/ErpPrinter.Service.wxs   WiX v5 package of the C# product
installer/ErpPrinter.wxs           WiX v5 package of the PowerShell product (unchanged, still released)
build/Publish-Dotnet.ps1           publish the three executables into one folder
build/New-PolicyTemplateFromCli.ps1, build/New-SettingsReferenceFromCli.ps1   future generators, see below
.config/dotnet-tools.json          repo-local tools: WiX 5.0.2
```

## Build, test, publish, package

Prerequisites: the .NET 10 SDK (and Windows for the Windows projects, the publish and the MSI). WiX comes from the repo-local tool manifest, so nothing is installed globally; a different WiX on `PATH` (say a global 7.x) is never used.

```powershell
./build/build.ps1 -Task DotnetBuild, DotnetTest                      # build the solution, run the tests
./build/build.ps1 -Task DotnetPublish                                # out/dotnet/payload
./build/build.ps1 -Task DotnetPublish, DotnetMsi, DotnetManifest `
  -Version 1.2.3 -UpdateManifestUrl https://github.com/<owner>/<repo>/releases/latest/download/latest.json
```

| Task | Result |
| --- | --- |
| `DotnetBuild` | `dotnet build dotnet/ErpPrinter.slnx -c Release` (off Windows: only Core and its tests) |
| `DotnetTest` | xUnit, TRX files in `out/dotnet/test-results`. `-TestScope Core\|Windows\|All` |
| `DotnetPublish` | `build/Publish-Dotnet.ps1`: the three executables, self-contained for `win-x64`, into **one** folder `out/dotnet/payload`, plus `Policies\`, `README.md`, `LICENSE` |
| `DotnetSign` | Authenticode-signs the product's own `erpprinter.exe`, `ErpPrinter*.exe` and `ErpPrinter*.dll` (`-CertificatePath`, same secrets as the PowerShell product); runtime files are already signed |
| `DotnetMsi` | `out/dotnet/ErpPrinter-Service-<version>-x64.msi` (signed too if a certificate is given) |
| `DotnetManifest` | `latest.json` and `.sha256` next to that MSI |
| `DotnetGenerate` | Future switch, see [Policy templates](#policy-templates-and-the-settings-reference) |

The version comes from `dotnet/Directory.Build.props` (release-please bumps it together with the PowerShell module version) unless `-Version` is given. `-UpdateManifestUrl` and `-Commit` are stamped into `ErpPrinter.Core` as assembly metadata (`BuildInfo`). The solution has warnings as errors.

Design choices: **ReadyToRun** (faster service start, no JIT warm-up), **self-contained** (no .NET prerequisite on customer machines), **no trimming or Native AOT** (the code uses reflection-heavy APIs), **no single-file** (three executables share one copy of the runtime, 110 MB uncompressed, about 40 MB in the MSI, and an upgrade just swaps plain files). Debug symbols are embedded in Release builds, so stack traces in logs carry line numbers.

### Published layout (what the code may rely on)

```
C:\Program Files\ERP Printer\
  ErpPrinterService.exe        service host (also <name>.dll, .deps.json, .runtimeconfig.json, appsettings.json)
  erpprinter.exe               CLI
  ErpPrinterConfig.exe         GUI, manifest requires administrator
  *.dll, hostfxr/hostpolicy…   the shared self-contained runtime and the app assemblies
  Policies\ErpPrinter.admx, Policies\en-US\ErpPrinter.adml
  README.md, LICENSE
```

The install folder is the same as the PowerShell product's. The Start menu shortcut is `ProgramMenu\ERP Printer\ERP Printer Configuration.lnk`, targeting `ErpPrinterConfig.exe`.

## The MSI

`installer/ErpPrinter.Service.wxs`, WiX v5 (`WixToolset.Util.wixext` for the quiet-exec/shell-exec custom actions and service recovery).

**Service.** `ServiceInstall`: name `ErpPrinter`, display name "ERP Printer", `LocalSystem`, own process, automatic start (not delayed). Recovery (util `ServiceConfig`): restart after 60 s on the first, second and subsequent failures, counter reset after 1 day. `ServiceControl` stops the service on install/upgrade/uninstall (waiting for it), deletes it on uninstall, and starts it after install (without waiting; a slow or refused start does not fail the installation). There is deliberately **no dependency on the Print Spooler**: stopping the spooler is a routine troubleshooting step and would stop a dependent service permanently, because recovery actions only fire on crashes. The service must wait for the spooler itself.

**Public properties** (same as the PowerShell MSI; `msiexec /i ... /qn NAME=value`):

| Property | Meaning |
| --- | --- |
| `PROFILE_NAME` | printer/profile name, default `Send to ERP` |
| `BACKEND` | `OdooJson2`, `OdooJsonRpc`, `ERPNext` or `Webhook` |
| `SERVER_URL` | creates or updates the profile when set |
| `DATABASE`, `USERNAME` | Odoo database and (JSON-RPC) login |
| `API_KEY`, `API_SECRET` | secrets; **hidden** in MSI logs (`Hidden="yes"`, and the command line is a hidden property with `HideTarget`) |
| `AUTO_UPDATE` | `1` or `0` |
| `UPDATE_MANIFEST_URL` | `latest.json` location, HTTPS or UNC |
| `LAUNCHCONFIG` | `0` to not open the configuration window after an interactive install (default `1`) |
| `PURGE` | `1` on uninstall to also delete configuration, secrets and spooled documents |

**Custom actions** (both deferred, `Impersonate="no"`, so SYSTEM):

| Action | Command line | When |
| --- | --- | --- |
| `CA_Provision` | `"[INSTALLFOLDER]erpprinter.exe" install "ProfileName=…" "Backend=…" "ServerUrl=…" "Database=…" "Username=…" "ApiKey=…" "ApiSecret=…" "AutoUpdate=…" "UpdateManifestUrl=…"` (empty properties arrive as `Name=`) | install, upgrade and repair, after `InstallServices` and before `StartServices`; failure fails the installation |
| `CA_Deprovision` | `"[INSTALLFOLDER]erpprinter.exe" uninstall "Purge=[PURGE]"` | real uninstall only (`REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE`), after `StopServices`, before files are removed; result ignored |

What `erpprinter.exe install` must do (idempotent): write `InstallDir` and `InstalledVersion` under `HKLM\SOFTWARE\ErpPrinter` (Intune detection rules use them), create the data folder and secrets key, apply the `AutoUpdate`/`UpdateManifestUrl` globals and seed the profile when `ServerUrl` is given, enable the PrintService operational log, create the Print to PDF driver, printers and ports, register the event source, **remove the legacy PowerShell scheduled tasks and Start menu shortcut** (see below). It must not start or stop the service (the MSI does, after this action). It must not fail because a printer could not be provisioned (log it; the GUI and `status` show it). `erpprinter.exe uninstall` removes printers and ports and, with `Purge=1`, the data folder, registry key and event source; the MSI owns the service (the CLI must tolerate it being stopped or already gone). When the MSI is launched interactively with `LAUNCHCONFIG=1`, the Start menu shortcut is opened after `InstallFinalize`.

**Requirements**: Windows 10 / Server 2016 or later, 64-bit (launch condition).

### Upgrade story

The package uses the **same UpgradeCode** as the PowerShell MSI (`B93D3E79-AE9E-4D91-A3D8-47FE35CC1026`) and `MajorUpgrade` scheduled after `InstallInitialize`. Installing it over a PowerShell installation therefore *is* an in-place upgrade: one "ERP Printer" in Apps & features, a self-update through `latest.json` works with no new mechanism, and Intune/GPO keep targeting one product.

Why that is safe: every PowerShell MSI ever published (since 0.1.0) skips its deprovision action while it is being replaced (`REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE`) and makes provisioning idempotent, so removing it as part of the upgrade deletes only its files and shortcut. Printers, ports, `HKLM\SOFTWARE\ErpPrinter` (profiles and DPAPI secrets), `%ProgramData%\ErpPrinter` and the outbox all survive, because the port reads the same layout. The new `CA_Provision` then runs `erpprinter install`, which switches the machine over:

1. It removes the scheduled tasks `\ERP Printer\Listener` and `\Uploader` (stopping them first, so the old listener releases the printer pipes) and the folder. **The `Updater` task needs care**: when the upgrade was started by the PowerShell self-updater, that task's process is the one waiting for `msiexec`. Delete its definition **without stopping it** (stopping a task ends its process tree), or the update ends mid-way. The old process then exits on its own.
2. It removes the legacy Start menu shortcut **only if its target is `powershell.exe`**. The legacy shortcut and the new one have the same path and name, and the new one has just been created by the installer.
3. `StartServices` then starts `ErpPrinter`, which opens the same named pipes (`\\.\pipe\ErpPrinter-<profile>`) and picks up the outbox.

Jobs printed while the service is stopped for the swap wait in the spooler queue and in the outbox; nothing is lost. If the installation fails and Windows Installer rolls back, the old product's files return, but the removed scheduled tasks do not: re-run the PowerShell MSI or `ErpPrinter.ps1 -Command Install` to recreate them. That is why `CA_Provision` runs as late as possible, immediately before `StartServices`.

Other cases: C# to C# upgrades use the same path (deprovision skipped, provision idempotent, service stopped and started). Going **back** to PowerShell: uninstall the C# product (`PURGE` unset keeps configuration), then install a PowerShell MSI; or install a PowerShell MSI with a *higher* version number, which upgrades over it the same way. Downgrades by version number are refused by Windows Installer, as before.

**Version caveat.** The C# line is versioned in lockstep with the PowerShell line (release-please bumps both). The PowerShell updater only installs a manifest version that is *newer* than the installed one, so the release that first points `latest.json` at the C# MSI must carry a higher version than the last PowerShell release the machine has.

### Policy templates and the settings reference

`policies/` and `docs/settings.md` are still generated from the PowerShell schema (`./build/build.ps1 -Task Generate`). The C# `Schema` class is meant to become the source of truth. When the CLI implements `erpprinter schema --admx <dir>` (writes `ErpPrinter.admx` and `en-US\ErpPrinter.adml`) and `erpprinter schema --settings-md <file>`, run:

```powershell
./build/build.ps1 -Task DotnetPublish, DotnetGenerate
./build/New-PolicyTemplateFromCli.ps1 -Check        # parity test: the CLI's output must equal the committed files
./build/New-SettingsReferenceFromCli.ps1 -Check
```

Until then both scripts fail with an explanatory message; nothing in the default tasks calls them.

## CI

`.github/workflows/dotnet.yml` runs when `dotnet/`, the Service wxs, `build/`, `policies/` or the tool manifest change:

| Job | Runner | What it does |
| --- | --- | --- |
| Core tests | the self-hosted NixOS runner (`ubuntu-latest` for fork pull requests), like `commits.yml` | restore, build and test `ErpPrinter.Core.Tests` with the SDK from nixpkgs |
| Build, Windows tests and MSI | `windows-latest` | `DotnetBuild`, `DotnetTest -TestScope Windows`, `DotnetPublish`, `DotnetMsi`, `DotnetManifest`; uploads `msi-dotnet`, `payload-dotnet` and the TRX files |

The MSI is versioned `0.0.<run number>` there, like the PowerShell build in `ci.yml`.

## Releases

release-please already bumps `dotnet/Directory.Build.props` (`<Version>` line, marked `<!-- x-release-please-version -->`; the generic updater only needs the marker comment and a semantic version on the same line, which is valid XML). `release-assets.yml` is intentionally **unchanged**: it builds and attaches only the PowerShell MSI, and `latest.json` keeps pointing at it. Once the C# product is ready to be offered, the change there is small: set up .NET 10, add `DotnetPublish, DotnetSign, DotnetMsi, DotnetManifest` after the PowerShell `Msi`/`Manifest` tasks and upload `out/dotnet/*.msi` as an extra asset; at cutover, publish the C# `latest.json` as the release's `latest.json`.

## Migration plan

1. **Parity (now).** The port is built and tested beside the PowerShell product. Releases ship only the PowerShell MSI. The Service MSI is built in CI as an artifact for testing on disposable machines (install, print, deliver, upgrade over a PowerShell install, uninstall).
2. **Pilot.** Attach the Service MSI to releases as an extra asset (not referenced by `latest.json`). Pilot machines install it by hand or with a pilot `latest.json` (see [Hosting updates yourself](deployment.md#hosting-updates-yourself)); the PowerShell product on other machines is unaffected. Extend `tests/E2E` to cover the C# MSI.
3. **Cutover.** Release a version that points `latest.json` at the C# MSI. The PowerShell updaters upgrade machines in place (see the upgrade story). The generators switch to `erpprinter schema` (`DotnetGenerate`), and the C# MSI takes the name `ErpPrinter-<version>-x64.msi`.
4. **Retire PowerShell.** After a few releases, delete `src/`, `installer/ErpPrinter.wxs`, the PowerShell tasks and tests; keep the module only if a supported scripting surface (`Set-ErpPrinterSecret`, `New-ErpPrinterProfile`) is still wanted; `erpprinter` replaces it.

## Verification state

Built and checked on a developer machine without installing anything: the publish layout, the MSI build with the repo-local WiX 5.0.2, ICE validation (`wix msi validate`; the only warnings are the two the PowerShell MSI also has), and a read-only inspection of the MSI tables (sequence order, Upgrade table, ServiceInstall/ServiceControl/service recovery, custom action command lines, hidden properties). Not yet exercised: an actual install, upgrade over a PowerShell installation, rollback and uninstall; that needs a disposable VM, ideally with a PowerShell product installed (the Spooler and Print to PDF must be present).
