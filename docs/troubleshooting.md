# Troubleshooting

A page travels through six hops. When "the test print worked but nothing arrived", the job is stuck at one of them, and the quickest way to find which is to ask the machine.

```
application --> Windows spooler --> printer port \\.\pipe\<name> --> Listener --> outbox --> Uploader --> ERP
   (1)               (2)                     (3)                       (4)         (5)         (6)
```

"Test connection" only proves hop 6 from the account you are logged in as. The Listener and Uploader run as SYSTEM, and a test page sent from an application is "successful" as soon as the spooler accepts it (hop 2).

## Start here

Run these in an elevated PowerShell on the affected computer (or use **Print test page** and **Diagnostics** on the Status tab, which do the same):

```powershell
& 'C:\Program Files\ERP Printer\ErpPrinter.ps1' -Command Diagnose
& 'C:\Program Files\ERP Printer\ErpPrinter.ps1' -Command TestPrint -ProfileName 'Send to Odoo'
```

-   **Diagnose** checks every hop without printing anything and saves a report to `<data folder>\logs\diagnostics-<time>.log`. The line "Start with" names the first failing check and what to do about it. Send the whole report to whoever supports the machine.
-   **TestPrint** prints a real page through the real queue and watches it hop by hop: the job in the print queue, what the spooler logged about it, the listener log, the outbox, and delivery. It ends with the hop where the page stopped. It takes up to a minute and exits with code 2 if the page did not arrive. Run it elevated; otherwise it cannot see the outbox.

Both are also available in PowerShell as `Invoke-ErpPrinterDiagnostics` and `Invoke-ErpPrinterTestPrint`.

## What the test print result means

| Stage reached | What it means | Usual causes |
| --- | --- | --- |
| *Submitted* | The spooler never listed the job | Print Spooler service stopped; printer removed |
| *InQueue* | The job sits in the queue | The spooler cannot open the pipe: Listener not running or crashed, pipe name differs, printer set to "Use Printer Offline" |
| *Spooled* | The spooler says it printed, the listener saw nothing | Printer on the wrong port (run **Repair printers**); two Listener processes; antivirus or Windows protected print mode interfering |
| *Received* | The listener has the page; the ERP does not | The error is shown: URL, credentials, model or field names, proxy (the service runs as SYSTEM and ignores per-user proxy settings; set `ProxyUrl`) |
| *Delivered* | Everything works | |

## Where to look

All of these are written by the service even when nothing is wrong, so "no lines" is itself a finding.

-   **Service logs:** `%ProgramData%\ErpPrinter\logs\listener-*.log`, `uploader-*.log`. The Listener logs its runtime (account, PowerShell version, language mode), each pipe once it is open, every spooler connection, and an hourly heartbeat with the number of jobs received. A healthy idle listener has a `Listener alive` line every hour. Crashes are logged as `The listener crashed` with the cause; the stack trace is at `Verbose` level. Set **Log level** to `Verbose` while investigating (Settings tab or the `LogLevel` value).
-   **`cli-*.log`** in the same folder: written by the scheduled-task entry point itself, so it still records failures that happen before the module loads (a blocked module, a bad registry value, AppLocker or Constrained Language Mode) and every start of the Listener, Uploader and Updater. If the data folder is not writable it falls back to `%TEMP%` (for SYSTEM, `C:\Windows\Temp`).
-   **Task Scheduler:** `\ERP Printer\Listener` and `Uploader`. A *Last Run Result* other than `0x41301` (running) on a task that should be running means it exited; `Diagnose` translates the code.
-   **Event Viewer:** *Application* (source `ErpPrinter`), and *Applications and Services Logs > Microsoft > Windows > PrintService > Admin* and *Operational*. Operational event 307 says which port the spooler used and how many bytes it sent; if the log is disabled, `wevtutil sl Microsoft-Windows-PrintService/Operational /e:true` turns it on. If the log folder cannot be written, a warning with event ID 2001 appears in the Application log.

## Frequent causes

-   **Listener not running, or restarting every few minutes.** `Diagnose` shows the task state, last result, and how many times `Listener started` appears in 24 hours. Five or more starts a day means it is crashing; the lines next to the starts say why.
-   **The pipe is not visible.** `Diagnose` lists `\\.\pipe\` (it never connects to the pipe, which would take the listener's only instance) and shows whether the listener's pipe exists.
-   **Printer on the wrong port or driver.** **Repair printers** (or `Sync-ErpPrinterQueue`) resets both.
-   **Windows protected print mode** (Windows 11) or a hardened print policy removes local ports and classic drivers; `Diagnose` checks for the policy.
-   **Constrained Language Mode** (AppLocker or WDAC script rules): the listener needs .NET calls that this mode blocks. `Diagnose` reports the mode; allow-list the install folder.
-   **Connection works in the GUI but not from the service.** The GUI runs as you; the uploader runs as SYSTEM. Compare proxy settings (`Diagnose` flags a per-user proxy with an empty `ProxyUrl`), VPN-only routes, and certificate trust for a private CA.
-   **Antivirus quarantining the PDF** before the uploader reads it. Exclude `%ProgramData%\ErpPrinter`.
