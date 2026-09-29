# ERP Printer

Virtual printers for Windows that deliver whatever you print straight into your ERP: Odoo (Documents or any record's attachments), ERPNext / Frappe, or any HTTP endpoint through a webhook.

Users pick a printer such as **Send to Odoo** in any application's print dialog. The job becomes a PDF, is queued locally, and is uploaded in the background with the Windows user and document title attached. If the ERP is unreachable, documents wait in the outbox and are retried; nothing is lost.

It is a thin layer over what Windows already has: the inbox **Microsoft Print to PDF** driver, a named-pipe printer port, and **Windows PowerShell 5.1**. There is no driver to install or sign, and no runtime to ship.

```
 Word / browser / PDF reader
          |  print to "Send to Odoo"
          v
 Spooler --> Microsoft Print To PDF driver --> port \\.\pipe\ErpPrinter-Send_to_Odoo
                                                         |
                    Listener task (SYSTEM) <-------------+  one pipe connection = one complete PDF
                          |  <id>.pdf + <id>.json (user, title, job)
                          v
          %ProgramData%\ErpPrinter\spool\<profile>\outbox
                          |  wakes immediately, retries with backoff
                          v
                    Uploader task (SYSTEM) --> Odoo JSON-2 | Odoo JSON-RPC | ERPNext | Webhook
```

## Features

-   **Several printers per machine.** Each profile is its own Windows printer with its own backend, destination and credentials, e.g. "Send to Odoo - Invoices" and "Send to ERPNext".
-   **Backends:**
    -   `OdooJson2`: Odoo 19+ JSON-2 API.
    -   `OdooJsonRpc`: Odoo 14-18 `/jsonrpc`.
    -   `ERPNext`: Frappe `upload_file`, optionally attached to a document.
    -   `Webhook`: multipart or JSON POST to any URL, such as n8n, Power Automate or Paperless-ngx.
-   **Reliable delivery.** A local outbox with atomic writes, exponential backoff, permanent-vs-transient error classification, a failed folder with one-click retry, and optional archiving of sent files.
-   **User attribution.** The Windows user and document title travel with every document. Odoo profiles can map Windows users to Odoo users (login, email, or AD mail/UPN) and set `owner_id`.
-   **Registry-based configuration** with a Group Policy override layer, generated ADMX templates, and DPAPI-encrypted secrets in an ACL'd key.
-   **Configuration GUI** (Windows Forms, launched from the Start menu). It is generated from the settings schema, respects policy-locked values, tests connections, prints test pages and shows queue status.
-   **MSI installer** (WiX v5) that supports silent install and pre-seeding a printer profile from MSI properties, for Intune, GPO, SCCM or RMM.
-   **Auto-updater** that checks a `latest.json` manifest (GitHub Releases by default, or your own HTTPS or UNC location), verifies SHA-256 and optionally Authenticode, then runs a silent major upgrade.
-   **CI** with PSScriptAnalyzer (including Windows PowerShell 5.1 compatibility rules), Pester on Windows PowerShell 5.1 and PowerShell 7, an MSI build, an end-to-end test that installs, prints and uninstalls on a Windows runner, and tag-triggered releases.

## Requirements

-   Windows 10/11 or Windows Server 2016+ (Desktop Experience) with the Print Spooler running.
-   The Microsoft Print to PDF feature. The installer enables it if it is missing.
-   Windows PowerShell 5.1, which is built in.
-   Outbound HTTPS from the machine (as SYSTEM) to the ERP. Set `ProxyUrl` if you need a proxy.

## Install

Download `ErpPrinter-<version>-x64.msi` from the [releases](../../releases) page.

**Interactive:** run the MSI. Releases are not code-signed, so Windows SmartScreen may say it "protected your PC" and the elevation prompt shows _Unknown publisher_. Choose **More info > Run anyway**, then **Yes**. Silent installs through Intune, Group Policy or `msiexec /qn` show no prompts. The configuration window opens when the install finishes. Click **Add...**, choose a backend, enter the URL and API key, then **Test connection** and **Save & apply**.

**Silent, with a printer pre-configured:**

```powershell
msiexec /i ErpPrinter-1.0.0-x64.msi /qn `
  PROFILE_NAME="Send to Odoo" BACKEND=OdooJson2 SERVER_URL=https://acme.odoo.com `
  DATABASE=acme API_KEY=0123abcd... AUTO_UPDATE=1
```

| Property | Meaning |
| --- | --- |
| PROFILE_NAME | Printer/profile name (default Send to ERP) |
| BACKEND | OdooJson2, OdooJsonRpc, ERPNext or Webhook |
| SERVER_URL | ERP base URL, or the full webhook URL. A profile is created only when this is set |
| DATABASE, USERNAME | Odoo database; login for OdooJsonRpc |
| API_KEY, API_SECRET | Stored DPAPI-encrypted; hidden from MSI logs |
| AUTO_UPDATE | 1 or 0 |
| UPDATE_MANIFEST_URL | Alternative latest.json location (HTTPS or UNC) |
| LAUNCHCONFIG | 0 to not open the GUI after an interactive install |
| PURGE | On uninstall: 1 also deletes configuration, secrets and spooled documents |

Uninstall with `msiexec /x ErpPrinter-1.0.0-x64.msi /qn`. Printers and tasks are removed. Configuration and any undelivered documents stay unless you pass `PURGE=1`.

For Intune, Group Policy, and destination-specific setup (Odoo API keys, ERPNext tokens, Documents folders, webhooks), see [docs/deployment.md](docs/deployment.md) and [docs/backends.md](docs/backends.md).

## Configure

Use whichever fits; they all write the same registry values ([full reference](docs/settings.md)).

-   **GUI:** Start menu, then **ERP Printer Configuration** (elevates itself).
    
-   **PowerShell:**
    
    ```powershell
    Import-Module 'C:\Program Files\ERP Printer\Modules\ErpPrinter\ErpPrinter.psd1'
    New-ErpPrinterProfile -Name 'Send to ERPNext' -Settings @{
        Backend = 'ERPNext'; ServerUrl = 'https://erp.example.com'; ERPNextFolder = 'Home/Scans'
    }
    Set-ErpPrinterSecret -ProfileName 'Send to ERPNext' -Name ApiKey    -Value (Read-Host -AsSecureString)
    Set-ErpPrinterSecret -ProfileName 'Send to ERPNext' -Name ApiSecret -Value (Read-Host -AsSecureString)
    Sync-ErpPrinterQueue          # creates/removes Windows printers to match the profiles
    Test-ErpPrinterConnection 'Send to ERPNext'
    ```
    
-   **Registry / Group Policy:** global settings under `HKLM\SOFTWARE\ErpPrinter`, profiles under `...\Printers\<name>`. The same layout under `HKLM\SOFTWARE\Policies\ErpPrinter` overrides the machine values and locks them in the GUI. ADMX/ADML templates for the global settings ship with each release, and profiles can be pushed with Group Policy Preferences or Intune (see [deployment](docs/deployment.md#group-policy)).
    

The file name sent to the ERP comes from `FileNameTemplate` (default `{title}`), which can use `{title} {user} {username} {domain} {computer} {date} {time} {datetime} {id} {jobid} {profile}`.

## Operate

```powershell
& 'C:\Program Files\ERP Printer\ErpPrinter.ps1' -Command Status     # tasks, printers, pending/failed counts, last error
& 'C:\Program Files\ERP Printer\ErpPrinter.ps1' -Command Test -ProfileName 'Send to Odoo'
& 'C:\Program Files\ERP Printer\ErpPrinter.ps1' -Command Retry      # move failed documents back to the outbox
& 'C:\Program Files\ERP Printer\ErpPrinter.ps1' -Command Upload -Once -Force
& 'C:\Program Files\ERP Printer\ErpPrinter.ps1' -Command Update -CheckOnly
```

-   **Scheduled tasks** (under `\ERP Printer\`, running as SYSTEM): `Listener` and `Uploader` start at boot, with a 5-minute watchdog trigger that restarts them if they exit. `Updater` runs daily.
    
-   **Logs:** `%ProgramData%\ErpPrinter\logs\<component>-yyyyMMdd.log`, plus the Application event log, source `ErpPrinter`:
    
    | Event ID | Meaning |
    | --- | --- |
    | 1001 | Job queued |
    | 1002 | Document delivered |
    | 1010 / 1011 | Update started / installed |
    | 2002 | Delivery attempt failed; will retry |
    | 3002 | Document moved to the failed folder |
    
    Per-job spooler auditing is also switched on (`Microsoft-Windows-PrintService/Operational`, event 307).
    
-   **Failed documents** stay in `spool\<profile>\failed` with the error in the JSON sidecar. Retry them from the GUI's Status tab or with `-Command Retry`. Permanent errors, such as an HTTP 400 or an Odoo `ValidationError`, fail immediately. Network errors, 5xx and 401/403 keep retrying (forever by default; see `MaxAttempts`).
    

## Updates

Each release publishes `latest.json` (version, MSI URL, SHA-256). The daily `Updater` task reads `UpdateManifestUrl`, which defaults to the latest GitHub release of the repository the MSI was built from. When a newer version is available, the task downloads the MSI, verifies the hash, and (if `RequireSignedUpdates` or `UpdateSignerThumbprint` is set) its Authenticode signature, then runs `msiexec /qn`. Upgrades keep printers, configuration and the outbox.

To control rollout, host your own `latest.json` on an internal web server or file share and point `UpdateManifestUrl` at it by policy. Set `AutoUpdate` to 0 to turn updates off.

## Security notes

-   The data folder and the secrets key are restricted to SYSTEM and Administrators (by SID, so this works on localized Windows). Print jobs can contain sensitive documents.
-   API keys are DPAPI-encrypted with machine scope. That protects them at rest and in backups of the registry hive; the key ACL keeps them from other local users. Use a dedicated, tightly scoped ERP integration user and rotate its key.
-   The pipe allows authenticated users to write, because the spooler may open the port while impersonating the printing user. It is inbound-only, and each connection is treated as untrusted PDF data.
-   Releases are unsigned. Updates are trusted because the manifest is fetched over HTTPS from your release location, and the MSI must match the SHA-256 it lists. Anyone who can publish releases to the repository (or write to your self-hosted manifest location) can push code to every client, so protect that access. If you sign releases later (see [development](docs/development.md#code-signing)), set `RequireSignedUpdates` or pin `UpdateSignerThumbprint` to also require the signature.
-   The scheduled tasks and the Start menu shortcut run the scripts with `-ExecutionPolicy Bypass`, so unsigned scripts work under the default policies. The exception is an `AllSigned` execution policy enforced by Group Policy, which overrides `Bypass`; such machines need signed releases or an exemption. An enforced `RemoteSigned` policy is fine, because files installed by the MSI are local, not downloaded.

## Limitations and roadmap

-   No per-user notification yet (for example "Sent to Odoo" or "Queued, Odoo offline"). This needs a small per-user tray component, since the SYSTEM tasks cannot show UI.
-   Profiles are dynamic registry keys, which ADMX cannot model; ADMX covers the global settings.
-   Test with Windows 11's _Windows protected print mode_ and hardened spooler configurations before a wide rollout. Print to PDF is an inbox driver and should be allowed, but custom local ports are the part to verify.
-   Very large scans are held in memory once for base64 encoding (Odoo backends). `MaxDocumentSizeMB` caps this. ERPNext and multipart webhooks stream from disk.

## License

[MIT](LICENSE)

## Development

See [docs/development.md](docs/development.md). In short:

```powershell
./build/bootstrap.ps1                       # Pester 5 + PSScriptAnalyzer
./build/build.ps1 -Task Lint, Test          # works on Linux/macOS with PowerShell 7 too
./build/build.ps1 -Task Stage, Msi, Manifest -Version 1.0.0   # Windows + WiX 5
```

Releases are cut by pushing a `vX.Y.Z` tag.
