# Windows ERP Printer

Virtual printers for Windows that deliver whatever you print straight into your ERP: Odoo (any model you can create records in: attachments, Documents, OCA EDI exchange records or your own), ERPNext / Frappe, or any HTTP endpoint through a webhook.

Users pick a printer such as **Send to Odoo** in any application's print dialog. The job becomes a PDF, is queued locally, and is uploaded in the background with the Windows user and document title attached. If the ERP is unreachable, documents wait in the outbox and are retried; nothing is lost.

It is a thin layer over what Windows already has: the inbox **Microsoft Print to PDF** driver and a named-pipe printer port. A native .NET Windows service does the rest. There is no printer driver to install or sign, and nothing to install first: the .NET runtime ships inside the installer.

```
 Word / browser / PDF reader
          |  print to "Send to Odoo"
          v
 Spooler --> Microsoft Print To PDF driver --> port \\.\pipe\ErpPrinter-Send_to_Odoo
                                                         |
                   ERP Printer service (SYSTEM)  <-------+  one pipe connection = one complete PDF
                     listener: <id>.pdf + <id>.json (user, title, job)
                          v
          %ProgramData%\ErpPrinter\spool\<profile>\outbox
                          |  wakes immediately, retries with backoff
                          v
                     uploader --> Odoo JSON-2 | Odoo JSON-RPC | ERPNext | Webhook
```

## Features

-   **Several printers per machine.** Each profile is its own Windows printer with its own backend, destination and credentials, e.g. "Send to Odoo - Invoices" and "Send to ERPNext".
-   **Backends:**
    -   `OdooJson2`: Odoo 19+ JSON-2 API.
    -   `OdooJsonRpc`: Odoo 14-18 `/jsonrpc`.
    -   `ERPNext`: Frappe `upload_file`, optionally attached to a document.
    -   `Webhook`: multipart or JSON POST to any URL, such as n8n, Power Automate or Paperless-ngx.
-   **Reliable delivery.** A local outbox with atomic writes, exponential backoff, permanent-vs-transient error classification, a failed folder with one-click retry, and optional archiving of sent files.
-   **Diagnosable.** `erpprinter diagnose` checks every link from the printer to the ERP, `erpprinter testprint` follows a real page hop by hop, and both explain where a job stopped (see [troubleshooting](docs/troubleshooting.md)).
-   **User attribution.** The Windows user and document title travel with every document. Odoo profiles can map Windows users to Odoo users (login, email, or AD mail/UPN) and write the match to a field of your choice, such as `owner_id`.
-   **Registry-based configuration** with a Group Policy override layer, generated ADMX templates, and DPAPI-encrypted secrets in an ACL'd key.
-   **Configuration window** (Windows Forms, launched from the Start menu). It is generated from the settings schema, respects policy-locked values, tests connections, prints test pages and shows queue status.
-   **MSI installer** (WiX v5) that supports silent install and pre-seeding a printer profile from MSI properties, for Intune, GPO, SCCM or RMM.
-   **Auto-updater** that checks a `latest.json` manifest (GitHub Releases by default, or your own HTTPS or UNC location), verifies SHA-256 and optionally Authenticode, then runs a silent major upgrade.

## Requirements

-   Windows 10/11 or Windows Server 2016+ (Desktop Experience), 64-bit, with the Print Spooler running.
-   The Microsoft Print to PDF feature. The installer enables it if it is missing.
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
| LAUNCHCONFIG | 0 to not open the configuration window after an interactive install |
| PURGE | On uninstall: 1 also deletes configuration, secrets and spooled documents |

Uninstall with `msiexec /x ErpPrinter-1.0.0-x64.msi /qn`. Printers and the service are removed. Configuration and any undelivered documents stay unless you pass `PURGE=1`.

**Coming from the PowerShell version?** The MSI upgrades it in place and keeps printers, configuration, secrets and the outbox; see [upgrading](docs/upgrading.md).

For Intune, Group Policy, and destination-specific setup (Odoo models and default values, ERPNext tokens, webhooks), see [docs/deployment.md](docs/deployment.md) and [docs/backends.md](docs/backends.md).

## Configure

Use whichever fits; they all write the same registry values ([full reference](docs/settings.md)).

-   **Configuration window:** Start menu, then **ERP Printer Configuration** (elevates itself).
    
-   **Command line** (elevated; `erpprinter.exe` is in `C:\Program Files\ERP Printer`, which the installer does not add to `PATH`):
    
    ```powershell
    $erp = 'C:\Program Files\ERP Printer\erpprinter.exe'
    & $erp profile add 'Send to ERPNext' Backend=ERPNext ServerUrl=https://erp.example.com ERPNextFolder=Home/Scans
    & $erp secret set 'Send to ERPNext' ApiKey          # prompts without echo; add --stdin to read it from a pipe
    & $erp secret set 'Send to ERPNext' ApiSecret
    & $erp sync                                         # creates/removes Windows printers now (the service also does it within a minute)
    & $erp test 'Send to ERPNext'
    ```
    
    Other commands: `profile list|show|remove`, `set [--profile <name>] Name=Value`, `set --clear Name`.
    
-   **Registry / Group Policy:** global settings under `HKLM\SOFTWARE\ErpPrinter`, profiles under `...\Printers\<name>`. The same layout under `HKLM\SOFTWARE\Policies\ErpPrinter` overrides the machine values and locks them in the configuration window. ADMX/ADML templates for the global settings ship with each release, and profiles can be pushed with Group Policy Preferences or Intune (see [deployment](docs/deployment.md#group-policy)).
    

The file name sent to the ERP comes from `FileNameTemplate` (default `{title}`), which can use `{title} {user} {username} {domain} {computer} {date} {time} {datetime} {id} {jobid} {profile}`.

## Operate

```powershell
$erp = 'C:\Program Files\ERP Printer\erpprinter.exe'
& $erp status                            # service, printers, pending/failed/sent counts, last error
& $erp test 'Send to Odoo'               # connection test of a profile
& $erp diagnose                          # check every hop from printer to ERP and save a report (run elevated)
& $erp testprint 'Send to Odoo'          # print a page and report where it stops
& $erp retry                             # move failed documents back to the outbox
& $erp upload --once --force             # one delivery pass now
& $erp update --check-only
```

-   **The service** is named `ErpPrinter` ("ERP Printer" in `services.msc`), runs as SYSTEM and starts automatically. One process hosts the listener (the print pipes), the uploader and the updater, each restarted on its own if it fails; if the process itself dies, Windows restarts the service after 60 seconds.
    
-   **Logs:** `%ProgramData%\ErpPrinter\logs\<component>-yyyyMMdd.log` (`listener`, `uploader`, `updater`, `cli`, `install`), plus the Application event log, source `ErpPrinter`. If a print does not arrive, start with [troubleshooting](docs/troubleshooting.md):
    
    | Event ID | Meaning |
    | --- | --- |
    | 1001 | Job queued |
    | 1002 | Document delivered |
    | 1010 / 1011 | Update started / installed |
    | 2002 | Delivery attempt failed; will retry |
    | 3002 | Document moved to the failed folder |
    
    Per-job spooler auditing is also switched on (`Microsoft-Windows-PrintService/Operational`, event 307).
    
-   **Failed documents** stay in `spool\<profile>\failed` with the error in the JSON sidecar. Retry them from the configuration window's Status tab or with `erpprinter retry`. Permanent errors, such as an HTTP 400 or an Odoo `ValidationError`, fail immediately. Network errors, 5xx and 401/403 keep retrying (forever by default; see `MaxAttempts`).
    

## Updates

Each release publishes `latest.json` (version, MSI URL, SHA-256). The service's updater reads `UpdateManifestUrl`, which defaults to the latest GitHub release of the repository the MSI was built from. It checks once a day at 03:00 plus a random delay of up to three hours, and soon after start-up if a check was missed. When a newer version is available, it downloads the MSI, verifies the hash, and (if `RequireSignedUpdates` or `UpdateSignerThumbprint` is set) its Authenticode signature, then runs `msiexec /qn`. Upgrades keep printers, configuration and the outbox.

To control rollout, host your own `latest.json` on an internal web server or file share and point `UpdateManifestUrl` at it by policy. Set `AutoUpdate` to 0 to turn updates off.

## Security notes

-   The data folder and the secrets key are restricted to SYSTEM and Administrators (by SID, so this works on localized Windows). Print jobs can contain sensitive documents.
-   API keys are DPAPI-encrypted with machine scope. That protects them at rest and in backups of the registry hive; the key ACL keeps them from other local users. Use a dedicated, tightly scoped ERP integration user and rotate its key. `erpprinter secret set` reads the key from a hidden prompt or standard input so it never appears on a command line.
-   The pipe allows authenticated users to write to it and open it with delete access, because the spooler opens the port while impersonating the printing user and asks for both. Users cannot create pipe instances or change the pipe's permissions. It is inbound-only, and each connection is treated as untrusted PDF data.
-   Releases are unsigned unless a signing certificate is configured. Updates are trusted because the manifest is fetched over HTTPS from your release location, and the MSI must match the SHA-256 it lists. Anyone who can publish releases to the repository (or write to your self-hosted manifest location) can push code to every client, so protect that access. If you sign releases (see [development](docs/development.md#code-signing)), set `RequireSignedUpdates` or pin `UpdateSignerThumbprint` to also require the signature.
-   Application-control policies (AppLocker, WDAC, Smart App Control) must allow the executables in `C:\Program Files\ERP Printer`. Nothing runs through a script host, so script-execution policies do not apply.

## Limitations and roadmap

-   No per-user notification yet (for example "Sent to Odoo" or "Queued, Odoo offline"). This needs a small per-user tray component, since the SYSTEM service cannot show UI.
-   Profiles are dynamic registry keys, which ADMX cannot model; ADMX covers the global settings.
-   Test with Windows 11's _Windows protected print mode_ and hardened spooler configurations before a wide rollout. Print to PDF is an inbox driver and should be allowed, but custom local ports are the part to verify (`erpprinter diagnose` reports the policy).
-   `MaxDocumentSizeMB` caps the size of a document; larger ones are moved to the failed folder instead of being uploaded.

## License

[MIT](LICENSE)

## Development

See [docs/development.md](docs/development.md) and [docs/architecture.md](docs/architecture.md). In short, with the .NET 10 SDK:

```powershell
./build/build.ps1 -Task Build,Test                  # off Windows: the portable Core project and its tests
./build/build.ps1 -Task Publish,Msi,Manifest -Version 1.0.0   # Windows; WiX comes from the repo-local tool manifest
```

Commits follow [Conventional Commits](https://www.conventionalcommits.org/); release-please turns them into version bumps, [CHANGELOG.md](CHANGELOG.md) and GitHub releases. See [development.md](docs/development.md#releases).
