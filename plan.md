# Windows ERP Printer

The idea of this project is to create a thin wrapper around existing Microsoft technologies (Print to PDF driver and Powershell) to allow the instantiation of a virtual printer which sends documents straight to Odoo (or another ERP like ERPNext).

## Why not a file port and FileSystemWatcher

A Local Port named `C:\OdooSpool\out.pdf` is a single fixed filename, so every job writes to the same file. That causes three problems:

-   **Partial files.** FileSystemWatcher fires on Created/Changed while the spooler is still writing, so you have to poll until the file is closed.
-   **Overwrites.** If your script hasn't moved the file before the next job starts, the new job overwrites it.
-   **Failed jobs.** If your script is holding the file open when the next job starts, that job fails.

You can work around all of this with retries, but it stays racy.

A named pipe fixes it structurally. You name the Local Port `\\.\pipe\OdooPrint`, and your script hosts a pipe server on that name. The spooler processes one job at a time per queue, so each pipe connection is exactly one complete PDF. There's no polling, no fixed filename, and no half-written files. If your listener isn't running, the job sits in the queue in an error state and retries instead of being lost.

## Architecture

1.  **Printer.** A "Send to Odoo" queue uses the `Microsoft Print To PDF` driver on port `\\.\pipe\OdooPrint`.
2.  **Listener.** A Windows PowerShell 5.1 script (built into Windows, so no install) runs as SYSTEM from a startup scheduled task. It accepts pipe connections, reads the PDF, and looks up the job's owner and document name from the spooler.
3.  **Local outbox.** The listener writes each PDF plus a small JSON sidecar (user, title, timestamp) to a spool folder, then immediately goes back to waiting. The print job always completes fast, even if Odoo is unreachable.
4.  **Uploader.** A loop in the same script (or a second task) pushes outbox items to Odoo, deletes them on success, and retries with backoff on failure.

## 1\. Provisioning (run as admin, deployable via Intune or GPO)

```powershell
$port    = '\\.\pipe\OdooPrint'
$printer = 'Send to Odoo'

if (-not (Get-PrinterPort -Name $port -ErrorAction SilentlyContinue)) {
    Add-PrinterPort -Name $port
}
Add-Printer -Name $printer -DriverName 'Microsoft Print To PDF' -PortName $port

New-Item -ItemType Directory -Force 'C:\ProgramData\OdooPrint\outbox' | Out-Null
# Lock the folder down to SYSTEM + Administrators
icacls 'C:\ProgramData\OdooPrint' /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F'

# Optional but useful: the PrintService operational log gives you an audit trail (event 307)
wevtutil sl Microsoft-Windows-PrintService/Operational /e:true
```

If `Add-PrinterPort` refuses the pipe name on some build, there's a fallback. Add a string value named `\\.\pipe\OdooPrint` (with empty data) under `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Ports`, then restart the Spooler service.

## 2\. The listener

```powershell
# OdooPrintListener.ps1 — run with Windows PowerShell 5.1 as SYSTEM
$printer = 'Send to Odoo'
$outbox  = 'C:\ProgramData\OdooPrint\outbox'

# Pipe ACL: the spooler may impersonate the printing user when it opens the port,
# so allow authenticated users to write, not just SYSTEM.
$sec = New-Object System.IO.Pipes.PipeSecurity
$sec.AddAccessRule((New-Object System.IO.Pipes.PipeAccessRule('NT AUTHORITY\SYSTEM','FullControl','Allow')))
$sec.AddAccessRule((New-Object System.IO.Pipes.PipeAccessRule('NT AUTHORITY\Authenticated Users','ReadWrite','Allow')))

while ($true) {
    $pipe = New-Object System.IO.Pipes.NamedPipeServerStream(
        'OdooPrint', 'In', 1, 'Byte', 'None', 65536, 65536, $sec)
    try {
        $pipe.WaitForConnection()

        # The job currently being sent to the port tells us who printed what
        $job = Get-PrintJob -PrinterName $printer |
               Where-Object { $_.JobStatus -match 'Printing' } |
               Select-Object -First 1

        $ms = New-Object System.IO.MemoryStream
        $pipe.CopyTo($ms)

        $id = [guid]::NewGuid().ToString()
        [IO.File]::WriteAllBytes("$outbox\$id.pdf", $ms.ToArray())
        @{
            user     = $job.UserName
            title    = $job.DocumentName
            computer = $env:COMPUTERNAME
            printed  = (Get-Date).ToString('o')
        } | ConvertTo-Json | Set-Content "$outbox\$id.json" -Encoding UTF8
    }
    catch { Write-EventLog -LogName Application -Source 'OdooPrint' -EventId 1 -EntryType Error -Message $_ }
    finally { $pipe.Dispose() }
}
```

A few notes on this script:

-   Use Windows PowerShell 5.1, not PowerShell 7. The `NamedPipeServerStream` constructor that takes a `PipeSecurity` only exists in .NET Framework; in .NET Core you'd use `NamedPipeServerStreamAcl.Create` instead.
-   Register the event source once during provisioning with `New-EventLog -LogName Application -Source OdooPrint`, or the `Write-EventLog` call will fail.
-   To run it as a "service," create a scheduled task that runs at startup as SYSTEM, repeats on failure, and has no execution time limit. PowerShell can't be a true Windows service without a wrapper. If you later want a real service with proper recovery options, the same logic ports almost line-for-line to a small C# worker service installed with `sc.exe create`.

## 3\. Sending to Odoo: API, not email

Email works (Odoo Documents folders can have mail aliases), but it adds SMTP credentials, delivery delays, and weak attribution. The external API is more direct:

-   **Odoo 19+:** use the JSON-2 API with an API key: `POST https://<instance>/json/2/documents.document/create`, with the header `Authorization: bearer <key>` (plus `X-Odoo-Database` if the server hosts several databases).
-   **Odoo 17/18:** use `/jsonrpc` with `execute_kw` against the same model. Odoo has announced that the older XML-RPC/JSON-RPC endpoints are being phased out in favor of JSON-2, so plan to move to JSON-2 when you upgrade.

Uploader sketch (JSON-2):

```powershell
$base   = 'https://yourco.odoo.com'
$apiKey = Get-Content 'C:\ProgramData\OdooPrint\key.txt'   # ACL'd to SYSTEM only
$folder = 42                                                # target Documents folder id

Get-ChildItem "$outbox\*.json" | ForEach-Object {
    $meta = Get-Content $_.FullName -Raw | ConvertFrom-Json
    $pdf  = [IO.Path]::ChangeExtension($_.FullName, '.pdf')
    $body = @{
        vals_list = @(@{
            name      = ($meta.title -replace '[\\/:*?"<>|]', '_') + '.pdf'
            datas     = [Convert]::ToBase64String([IO.File]::ReadAllBytes($pdf))
            folder_id = $folder
            # owner_id = <res.users id resolved from $meta.user>
        })
    } | ConvertTo-Json -Depth 5
    try {
        Invoke-RestMethod "$base/json/2/documents.document/create" -Method Post `
            -Headers @{ Authorization = "bearer $apiKey" } `
            -ContentType 'application/json' -Body $body
        Remove-Item $pdf, $_.FullName
    } catch { <# leave in outbox; retry next pass #> }
}
```

Field names on `documents.document` have shifted between versions (folders became documents themselves in 18, for example). Check the model on your version under Settings → Technical → Models before relying on `folder_id` or `owner_id`.

**If you're on Community edition** (no Documents app), you can create an `ir.attachment` on a record the user chooses later, or post a message with the attachment onto a "Scanned/Printed Inbox" record so it lands in the chatter.

## 4\. Attributing documents to the right Odoo user

I'd use one restricted integration user with Documents access only, and map Windows users to Odoo users on the server side. The job's `UserName`, or the user's email/UPN from AD, can be looked up against `res.users` (login or email) to set `owner_id`, or to pick the user's personal folder. Cache that mapping locally.

Per-user API keys look cleaner but are awkward in practice. The listener runs as SYSTEM, so it can't read secrets stored in each user's DPAPI or Credential Manager without extra plumbing.

Either way, the API key sits on every endpoint. Scope that Odoo user tightly and plan for key rotation.

## Pitfalls to test early

-   **Impersonation and ACLs.** This is the most likely thing to break on first try. Depending on build and settings, the spooler opens the port either as SYSTEM or impersonating the printing user. The pipe ACL above allows both. With a file port, users would need write access to the folder.
-   **Print hardening.** Windows 11 24H2's "Windows protected print mode" and future spooler hardening could affect custom local ports. Microsoft Print to PDF is an inbox driver, so it should survive, but pilot on your newest build and any hardened machines.
-   **RDS/terminal servers.** One queue serves many users. The job-owner lookup handles this, which is another advantage over the fixed-file approach.
-   **Large documents.** Base64 inflates the file by about a third and PowerShell holds it all in memory. That's fine for typical business docs, but cap the size or stream for huge scans.
-   **Job lookup race.** `Get-PrintJob` filtering on "Printing" status is reliable with one queue processing one job at a time. As a belt-and-braces check, correlate against PrintService event 307 afterward.

## Rollout order

1.  Build the printer and listener on one test machine, writing only to the outbox with no upload. Confirm that a print from Word, a browser, and a PDF reader each produce a valid, complete PDF with the correct user and title.
2.  Add the uploader against a staging Odoo database.
3.  Test failure cases: Odoo unreachable, listener stopped mid-job, two users printing at once on an RDS host.
4.  Package provisioning plus the scheduled task into one deployment script and pilot with a few users.

A small tray notification ("Sent to Odoo ✓" or "Queued, Odoo offline") is a nice later addition. It needs a per-user component, since the SYSTEM task can't show UI.
