# Troubleshooting

A page travels through six hops. When "the test print worked but nothing arrived", the job is stuck at one of them, and the quickest way to find which is to ask the machine.

```
application --> Windows spooler --> printer port \\.\pipe\<name> --> listener --> outbox --> uploader --> ERP
   (1)               (2)                     (3)                       (4)         (5)         (6)
```

"Test connection" only proves hop 6 from the account you are logged in as. The service runs as SYSTEM, and a test page sent from an application is "successful" as soon as the spooler accepts it (hop 2).

## Start here

Run these in an elevated PowerShell on the affected computer (or use **Print test page** and **Diagnostics** on the Status tab, which do the same):

```powershell
& 'C:\Program Files\ERP Printer\erpprinter.exe' diagnose
& 'C:\Program Files\ERP Printer\erpprinter.exe' testprint 'Send to Odoo'
```

-   **Diagnose** checks every hop without printing anything and saves a report to `<data folder>\logs\diagnostics-<time>.log`. The line "Start with" names the first failing check and what to do about it. Send the whole report to whoever supports the machine.
-   **TestPrint** prints a real page through the real queue and watches it hop by hop: the job in the print queue, what the spooler logged about it, the listener log, the outbox, and delivery. It ends with the hop where the page stopped. It takes up to a minute and exits with code 2 if the page did not arrive. Run it elevated; otherwise it cannot see the outbox.

## When one application fails but the test page works

Applications hand the spooler different data: the test page goes through GDI, while a browser's own print preview (Edge, Chrome) submits an XPS document. If Edge's job ends in **Error** but the test page is delivered, run **Diagnose** right after the failed print, before deleting the job. For every job stuck in the queue it reads the spooler's event timeline and the spool file's format, and says at which stage the job stopped:

| Timeline | What it means |
| --- | --- |
| *spooled*, then nothing | The queue is paused, offline, or blocked by an earlier job |
| *spooled -> printing started*, no print processor event | The document failed while the driver converted it to PDF. The pipe was never opened, so ERP Printer's logs stay silent. This is a document or Windows problem, not delivery |
| *print processor finished* with a non-zero code | The print processor refused the document; the code is shown |
| *rendering*, but not *written to port* | The spooler could not write to the pipe: check the Listener and the pipe |
| *written to port* | The spooler is done; look for "The spooler connected to pipe" in the listener log |

To find out whether the document or the queue is at fault, print the same page to the built-in **Microsoft Print to PDF** printer. In Edge, **Ctrl+Shift+P** opens the system print dialog, which uses the GDI path that the test page uses. A failed job also puts the whole printer into the Error state and holds up every job behind it, so delete it (`Get-PrintJob -PrinterName <printer> | Remove-PrintJob`) once you have the report.

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

-   **Service logs:** `%ProgramData%\ErpPrinter\logs\listener-*.log`, `uploader-*.log`. The listener logs its runtime (account, .NET version), each pipe once it is open, every spooler connection, and an hourly heartbeat with the number of jobs received. A healthy idle listener has a `Listener alive` line every hour. Crashes are logged as `The listener crashed` with the cause; the stack trace is at `Verbose` level. Set **Log level** to `Verbose` while investigating (Settings tab or the `LogLevel` value).
-   **`cli-*.log` and `install-*.log`** in the same folder: written by `erpprinter.exe`, so a failed install, sync or update (including one run by the MSI) leaves a trace.
-   **Services:** `ErpPrinter` (`services.msc`). It should be *Running* and start *Automatic*. The listener, uploader and updater inside it are restarted individually after a failure (30 s pause, logged as `stopped unexpectedly`); if the whole process dies, Windows restarts the service after 60 s.
-   **Event Viewer:** *Application* (source `ErpPrinter`), and *Applications and Services Logs > Microsoft > Windows > PrintService > Admin* and *Operational*. Operational event 307 says which port the spooler used and how many bytes it sent; if the log is disabled, `wevtutil sl Microsoft-Windows-PrintService/Operational /e:true` turns it on. If the log folder cannot be written, a warning with event ID 2001 appears in the Application log.

## Frequent causes

-   **Service not running, or restarting every few minutes.** `Diagnose` shows the service state, whether a process exists, and how many times `Listener started` appears in 24 hours. Five or more starts a day means it is crashing; the lines next to the starts say why.
-   **The pipe is not visible.** `Diagnose` lists `\\.\pipe\` (it never connects to the pipe, which would take the listener's only instance) and shows whether the listener's pipe exists.
-   **Printer on the wrong port or driver.** **Repair printers** (or `erpprinter sync`) resets both.
-   **Windows protected print mode** (Windows 11) or a hardened print policy removes local ports and classic drivers; `Diagnose` checks for the policy.
-   **Application control** (AppLocker, WDAC, Smart App Control) blocking `ErpPrinterService.exe`: the service then fails to start and the System log says so. Allow the executables in `C:\Program Files\ERP Printer`.
-   **A PowerShell-era installation left behind.** If the old scheduled tasks `\ERP Printer\Listener` and `Uploader` still run, they compete with the service for the pipe and the outbox. `Diagnose` flags them; the MSI removes them on upgrade.
-   **Connection works in the GUI but not from the service.** The GUI runs as you; the uploader runs as SYSTEM. Compare proxy settings (`Diagnose` flags a per-user proxy with an empty `ProxyUrl`), VPN-only routes, and certificate trust for a private CA.
-   **Antivirus quarantining the PDF** before the uploader reads it. Exclude `%ProgramData%\ErpPrinter`.
