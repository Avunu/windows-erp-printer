# Deploying at scale

The MSI is per-machine and needs no reboot. Everything it configures lives in the registry, so any tool that can run an MSI and write registry values can manage it.

## Intune (Win32 app or line-of-business MSI)

**Line-of-business app:** upload the MSI and set the command-line arguments, e.g.:

```
PROFILE_NAME="Send to Odoo" BACKEND=OdooJson2 SERVER_URL=https://acme.odoo.com DATABASE=acme API_KEY=<key>
```

**Win32 app (.intunewin):** gives more control:

| Field | Value |
| --- | --- |
| Install command | `msiexec /i ErpPrinter-1.0.0-x64.msi /qn PROFILE_NAME="Send to Odoo" BACKEND=OdooJson2 SERVER_URL=https://acme.odoo.com API_KEY=<key>` |
| Uninstall command | `msiexec /x {product code} /qn` |
| Install behavior | System |
| Detection | MSI product code, or registry `HKLM\SOFTWARE\ErpPrinter` value `InstalledVersion` (removed on uninstall) |

Because the app updates itself, set the detection rule to "exists" rather than an exact version. Otherwise, set `AUTO_UPDATE=0` and deploy new versions through Intune.

Instead of putting the key in the install command, you can deploy it separately with a remediation script that runs as SYSTEM:

```powershell
Import-Module 'C:\Program Files\ERP Printer\Modules\ErpPrinter\ErpPrinter.psd1'
Set-ErpPrinterSecret -ProfileName 'Send to Odoo' -Name ApiKey -Value '<key>'
```

Global settings can be pushed with the imported ADMX (Intune: *Devices > Configuration > Import ADMX*) or with OMA-URI/registry settings under `HKLM\SOFTWARE\Policies\ErpPrinter`.

## Group Policy

1. **Software installation:** assign the MSI to computers. Group Policy software installation cannot pass properties, so configure profiles with the steps below.
2. **Administrative templates:** copy `ErpPrinter.admx` and `en-US\ErpPrinter.adml` (from the release's `-admx.zip`, or `C:\Program Files\ERP Printer\Policies`) into the central store, `\\<domain>\SYSVOL\<domain>\Policies\PolicyDefinitions`. The settings appear under *Computer Configuration > Administrative Templates > ERP Printer*.
3. **Printer profiles:** use *Computer Configuration > Preferences > Windows Settings > Registry* to create values under `HKLM\SOFTWARE\Policies\ErpPrinter\Printers\<profile name>`, for example `Backend` (REG_SZ) = `OdooJson2` and `ServerUrl` (REG_SZ) = `https://acme.odoo.com`. See the [settings reference](settings.md) for every value. A profile defined only under `Policies` appears read-only in the GUI and cannot be deleted there.

   The Listener re-reads profiles every 30 seconds. When they change, it creates, repairs or removes the matching Windows printers itself, so no further action is needed after a policy refresh. To force it immediately, run `ErpPrinter.ps1 -Command Sync`.

4. **Execution policy:** releases are unsigned. If you enforce the PowerShell execution policy by GPO, use `RemoteSigned` or looser on these machines; `AllSigned` blocks the unsigned scripts.
5. **Secrets are never read from `Policies`,** because anything in a GPO is readable by every domain user. Deliver keys with a startup script or remediation that calls `Set-ErpPrinterSecret`, or with the MSI property on a per-machine install.

## Scripted or RMM deployment

```powershell
$msi = 'ErpPrinter-1.0.0-x64.msi'
Start-Process msiexec.exe -Wait -ArgumentList "/i `"$msi`" /qn AUTO_UPDATE=1"
Import-Module 'C:\Program Files\ERP Printer\Modules\ErpPrinter\ErpPrinter.psd1'
New-ErpPrinterProfile -Name 'Send to Odoo' -Force -Settings @{
    Backend = 'OdooJson2'; ServerUrl = 'https://acme.odoo.com'; Database = 'acme'
    OdooFolderId = 12; UserMapping = $true; UserEmailDomain = 'acme.com'
    ApiKey = $env:ODOO_PRINT_KEY
}
New-ErpPrinterProfile -Name 'Send to ERPNext' -Force -Settings @{
    Backend = 'ERPNext'; ServerUrl = 'https://erp.acme.com'; ApiKey = $env:ERPNEXT_KEY; ApiSecret = $env:ERPNEXT_SECRET
}
Sync-ErpPrinterQueue
Restart-ErpPrinterService
```

## Hosting updates yourself

To stage updates (for example pilot ring first), mirror the release assets to an internal location and point machines at it:

1. Copy `ErpPrinter-X.Y.Z-x64.msi` and `latest.json` to e.g. `\\fs01\deploy\erp-printer\pilot\`. The manifest's `url` may be relative to the manifest, e.g. `"url": "ErpPrinter-X.Y.Z-x64.msi"`.
2. Set the `UpdateManifestUrl` policy to `\\fs01\deploy\erp-printer\pilot\latest.json`, or to an HTTPS URL. The share must be readable by the computer accounts, because the updater runs as SYSTEM.
3. Promote a version by copying it to the production folder.

`latest.json` looks like:

```json
{ "version": "1.4.0", "url": "ErpPrinter-1.4.0-x64.msi", "sha256": "<hex>", "notes": "https://..." }
```

## Terminal servers (RDS / AVD)

One installation serves every session. The job owner comes from the spooler, so each document carries the right user. Make the ERP printers available to the users (they are local printers on the host), and consider `UserMapping` for Odoo so documents are owned by the right person.

## Pilot checklist

- Print from Word, a browser and a PDF reader. Check that each arrives complete, with the right user and title.
- Stop the ERP (or block it at the firewall), print, then restore it. The documents should arrive after the backoff.
- Print from two RDS sessions at once.
- Try your newest Windows build and any hardened print policy (Windows protected print mode).
