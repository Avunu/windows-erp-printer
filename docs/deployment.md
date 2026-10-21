# Deploying at scale

The MSI is per-machine and needs no reboot. Everything it configures lives in the registry, so any tool that can run an MSI and write registry values can manage it. Machines that already run the PowerShell version are upgraded in place; see [upgrading](upgrading.md).

## Intune (Win32 app or line-of-business MSI)

**Line-of-business app:** upload the MSI and set the command-line arguments, e.g.:

```
PROFILE_NAME="Send to Odoo" BACKEND=OdooJson2 SERVER_URL=https://acme.odoo.com DATABASE=acme API_KEY=<key>
```

**Win32 app (.intunewin):** gives more control:

| Field | Value |
| --- | --- |
| Install command | msiexec /i ErpPrinter-1.0.0-x64.msi /qn PROFILE_NAME="Send to Odoo" BACKEND=OdooJson2 SERVER_URL=https://acme.odoo.com API_KEY=<key> |
| Uninstall command | msiexec /x {product code} /qn |
| Install behavior | System |
| Detection | MSI product code, or registry HKLM\SOFTWARE\ErpPrinter value InstalledVersion (removed on uninstall) |

Because the app updates itself, set the detection rule to "exists" rather than an exact version. Otherwise, set `AUTO_UPDATE=0` and deploy new versions through Intune.

Instead of putting the key in the install command, you can deploy it separately with a remediation script that runs as SYSTEM. The key is read from standard input, so it never appears on a command line:

```powershell
'<key>' | & 'C:\Program Files\ERP Printer\erpprinter.exe' secret set 'Send to Odoo' ApiKey --stdin
```

Global settings can be pushed with the imported ADMX (Intune: _Devices > Configuration > Import ADMX_) or with OMA-URI/registry settings under `HKLM\SOFTWARE\Policies\ErpPrinter`.

## Group Policy

1.  **Software installation:** assign the MSI to computers. Group Policy software installation cannot pass properties, so configure profiles with the steps below.
    
2.  **Administrative templates:** copy `ErpPrinter.admx` and `en-US\ErpPrinter.adml` (from the release's `-admx.zip`, or `C:\Program Files\ERP Printer\Policies`) into the central store, `\\<domain>\SYSVOL\<domain>\Policies\PolicyDefinitions`. The settings appear under _Computer Configuration > Administrative Templates > ERP Printer_.
    
3.  **Printer profiles:** use _Computer Configuration > Preferences > Windows Settings > Registry_ to create values under `HKLM\SOFTWARE\Policies\ErpPrinter\Printers\<profile name>`, for example `Backend` (REG\_SZ) = `OdooJson2` and `ServerUrl` (REG\_SZ) = `https://acme.odoo.com`. See the [settings reference](settings.md) for every value. A profile defined only under `Policies` appears read-only in the configuration window and cannot be deleted there.
    
    The service re-reads profiles every 30 seconds. When they change, it creates, repairs or removes the matching Windows printers itself, so no further action is needed after a policy refresh. To force it immediately, run `erpprinter sync`.
    
4.  **Application control:** if AppLocker or WDAC is enforced, allow the executables in `C:\Program Files\ERP Printer` (`ErpPrinterService.exe`, `erpprinter.exe`, `ErpPrinterConfig.exe`). Releases are unsigned unless a signing certificate is configured; signed releases can be allowed by publisher.
    
5.  **Secrets are never read from `Policies`,** because anything in a GPO is readable by every domain user. Deliver keys with a startup script or remediation that calls `erpprinter secret set`, or with the MSI property on a per-machine install.
    

## Scripted or RMM deployment

```powershell
$msi = 'ErpPrinter-1.0.0-x64.msi'
Start-Process msiexec.exe -Wait -ArgumentList "/i `"$msi`" /qn AUTO_UPDATE=1"
$erp = 'C:\Program Files\ERP Printer\erpprinter.exe'
& $erp profile add 'Send to Odoo' --force Backend=OdooJson2 ServerUrl=https://acme.odoo.com Database=acme `
    OdooModel=documents.document 'OdooDefaultValues={"folder_id": 12}' `
    UserMapping=true UserEmailDomain=acme.com ApiKey=$env:ODOO_PRINT_KEY
& $erp profile add 'Send to ERPNext' --force Backend=ERPNext ServerUrl=https://erp.acme.com `
    ApiKey=$env:ERPNEXT_KEY ApiSecret=$env:ERPNEXT_SECRET
& $erp sync
```

`profile add` stores `ApiKey` and `ApiSecret` encrypted. Prefer `secret set --stdin` when the value could be visible in a process list. The service picks up configuration changes within a minute; `sync` does the printer part immediately. Any other value can be changed later with `erpprinter set --profile <name> Name=Value`.

## Hosting updates yourself

To stage updates (for example pilot ring first), mirror the release assets to an internal location and point machines at it:

1.  Copy `ErpPrinter-X.Y.Z-x64.msi` and `latest.json` to e.g. `\\fs01\deploy\erp-printer\pilot\`. The manifest's `url` may be relative to the manifest, e.g. `"url": "ErpPrinter-X.Y.Z-x64.msi"`.
2.  Set the `UpdateManifestUrl` policy to `\\fs01\deploy\erp-printer\pilot\latest.json`, or to an HTTPS URL. The share must be readable by the computer accounts, because the updater runs as SYSTEM.
3.  Promote a version by copying it to the production folder.

`latest.json` looks like:

```json
{ "version": "1.4.0", "url": "ErpPrinter-1.4.0-x64.msi", "sha256": "<hex>", "notes": "https://..." }
```

## Terminal servers (RDS / AVD)

One installation serves every session. The job owner comes from the spooler, so each document carries the right user. Make the ERP printers available to the users (they are local printers on the host), and consider `UserMapping` for Odoo so documents are owned by the right person.

## Pilot checklist

-   Print from Word, a browser and a PDF reader. Check that each arrives complete, with the right user and title. Browsers submit XPS rather than GDI data, so include Edge or Chrome with the browser's own print preview, not only the system dialog.
-   Stop the ERP (or block it at the firewall), print, then restore it. The documents should arrive after the backoff.
-   Print from two RDS sessions at once.
-   Try your newest Windows build and any hardened print policy (Windows protected print mode).
-   Run `erpprinter testprint <profile>` and `erpprinter diagnose` on a pilot machine and keep the output as the baseline.
