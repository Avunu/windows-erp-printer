# Upgrading from the PowerShell version

Releases up to and including 0.4.0 were written in PowerShell and ran as scheduled tasks. Later releases are a native .NET Windows service. The MSI upgrades the old product **in place**: printers, configuration, encrypted secrets and the outbox survive, and nothing needs to be reconfigured.

## What you do

Nothing special. Install the new MSI over the old one (or let the old product's self-updater do it: it reads `latest.json`, like any update). Check afterwards:

```powershell
& 'C:\Program Files\ERP Printer\erpprinter.exe' status      # Service: Running, printers OK
& 'C:\Program Files\ERP Printer\erpprinter.exe' diagnose    # no failures, no "legacy scheduled tasks" warning
```

Then print a test page (`erpprinter testprint <profile>`). Pilot on one machine before a wide rollout; see the [pilot checklist](deployment.md#pilot-checklist).

## What changes on the machine

| | PowerShell version | Now |
| --- | --- | --- |
| Background work | scheduled tasks `\ERP Printer\Listener`, `Uploader`, `Updater` (SYSTEM) | one Windows service `ErpPrinter` (SYSTEM) with a restart-on-failure recovery action |
| Command line | `ErpPrinter.ps1 -Command ...` and the `ErpPrinter` PowerShell module | `erpprinter.exe` (`status`, `diagnose`, `testprint`, `profile`, `set`, `secret`, ...) |
| Configuration window | `ErpPrinterConfig.ps1` | `ErpPrinterConfig.exe` (same Start menu entry) |
| Requirements | Windows PowerShell 5.1 | none: the .NET runtime ships inside the MSI |
| Registry, data folder, outbox, logs, event source | `HKLM\SOFTWARE\ErpPrinter`, `%ProgramData%\ErpPrinter`, `ErpPrinter` | **unchanged** |

Scripts that used the PowerShell module need small changes:

| Before | Now |
| --- | --- |
| `New-ErpPrinterProfile -Name N -Settings @{ ... }` | `erpprinter profile add N Name=Value ...` |
| `Set-ErpPrinterSetting -Name X -Value Y [-ProfileName N]` | `erpprinter set [--profile N] X=Y` |
| `Set-ErpPrinterSecret -ProfileName N -Name ApiKey -Value ...` | `erpprinter secret set N ApiKey` (hidden prompt, or `--stdin`) |
| `Remove-ErpPrinterProfile`, `Get-ErpPrinterProfile` | `erpprinter profile remove\|list\|show` |
| `Sync-ErpPrinterQueue` | `erpprinter sync` |
| `Test-ErpPrinterConnection N` | `erpprinter test N` |
| `Get-ErpPrinterStatus` | `erpprinter status` |
| `Invoke-ErpPrinterDiagnostics`, `Invoke-ErpPrinterTestPrint` | `erpprinter diagnose`, `erpprinter testprint N` |
| `Restore-ErpPrinterFailedDocument` | `erpprinter retry` |
| `Restart-ErpPrinterService` | `Restart-Service ErpPrinter` |

## How the in-place upgrade works

The MSI keeps the **UpgradeCode** of the PowerShell releases (`B93D3E79-AE9E-4D91-A3D8-47FE35CC1026`, never change it) and uses a major upgrade scheduled after `InstallInitialize`. Windows Installer therefore removes the old product and installs the new one as a single upgrade: one "ERP Printer" in Apps & features, and Intune or Group Policy keep targeting one product.

This is safe because every PowerShell MSI ever published skips its uninstall step while it is being replaced (`NOT UPGRADINGPRODUCTCODE`), so removing it deletes only its files and shortcut. The new product's provisioning step (`erpprinter install`, run as SYSTEM just before the service starts) then switches the machine over:

1.  It removes the scheduled tasks `\ERP Printer\Listener` and `Uploader`, stopping them first so the old listener releases the printer pipes, and the task folder.
2.  The `Updater` task is only *deleted*, never stopped. When the PowerShell self-updater started the upgrade, that task's process is the one waiting for `msiexec`; stopping it would end the update half way. It exits by itself.
3.  It removes the old Start menu shortcut, but only if it still points at `powershell.exe` (the new shortcut has the same path and name).
4.  The service starts, opens the same pipes (`\\.\pipe\ErpPrinter-<profile>`) and picks up the outbox.

Jobs printed while the service is stopped for the swap wait in the spooler queue and in the outbox; nothing is lost. If the installation fails and Windows Installer rolls back, the old files return but the removed scheduled tasks do not; provisioning therefore runs as late as possible, immediately before the service starts.

## Rolling it out through the old updater

The PowerShell updater installs a manifest version only if it is **newer** than the installed one. The first release whose `latest.json` points at the new MSI must therefore have a higher version than the last PowerShell release on the machines. Releases are versioned by release-please, so any release after 0.4.0 qualifies.

Publishing a release attaches `latest.json` to it, and every machine with `AutoUpdate` on picks that up within a day. So **merge the release pull request only after the MSI has been installed and printed through on a clean machine and on one that runs the PowerShell version**. To roll out in rings, point a pilot group's `UpdateManifestUrl` at a manifest you host yourself first (see [Hosting updates yourself](deployment.md#hosting-updates-yourself)).

## Going back

Uninstall the new product (without `PURGE`, so configuration stays) and install a PowerShell MSI, or install a PowerShell MSI with a *higher* version number, which upgrades over it the same way. Windows Installer refuses downgrades by version number.
