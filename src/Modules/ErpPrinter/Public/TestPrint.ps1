# How long a job may sit in an error state before the test gives up waiting (a variable so tests can shorten it).
$script:ErpTestPrintStuckSeconds = 10

function Invoke-ErpPrinterTestPrint {
    <#
    .SYNOPSIS
        Prints a test page through the real printer queue and reports where it stops.
    .DESCRIPTION
        Sending a page to the spooler only proves the spooler accepted it. This command keeps
        watching: the job in the print queue, the spooler's own log of what it did with the
        job, the listener's log, the outbox, and finally delivery to the ERP. It then says which
        hop failed and what to do about it.

        Use it on a machine where "test print succeeded" but nothing reaches the ERP.
    .PARAMETER ProfileName
        The printer profile to test.
    .PARAMETER TimeoutSeconds
        How long to wait for the page to be delivered. Failures that are certain earlier (a job
        stuck in an error state, a delivery error) end the wait sooner.
    .PARAMETER OnProgress
        Script block called with a one-line status message as things happen.
    .EXAMPLE
        Invoke-ErpPrinterTestPrint -ProfileName 'Send to Odoo' -OnProgress { param($m) Write-Host $m }
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory, Position = 0)] [string] $ProfileName,
        [ValidateRange(5, 900)] [int] $TimeoutSeconds = 60,
        [scriptblock] $OnProgress
    )
    $config = Get-ErpPrinterConfig
    $printerProfile = $config.Profiles | Where-Object { $_.Name -eq $ProfileName } | Select-Object -First 1
    if (-not $printerProfile) { throw "No printer profile named '$ProfileName'." }
    if (-not $printerProfile.Enabled) { throw "Profile '$ProfileName' is disabled; enable it first." }

    $start = Get-Date
    $steps = New-Object System.Collections.Generic.List[object]
    $note = {
        param([string] $Status, [string] $Message)
        $steps.Add([pscustomobject]@{ Seconds = [Math]::Round(((Get-Date) - $start).TotalSeconds, 1); Status = $Status; Message = $Message })
        if ($OnProgress) { try { & $OnProgress $Message } catch { Write-Verbose "Progress callback failed: $_" } }
    }

    $printerName = [string]$printerProfile.PrinterName
    $expectedPort = Get-ErpPortName $printerProfile.PipeName
    $paths = Get-ErpProfilePaths -DataRoot $config.Global.DataRoot -ProfileName $printerProfile.Name
    $token = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $documentName = "ERP Printer test page [$token]"
    $evidence = @{ Submitted = $false; ExpectedPort = $expectedPort }

    # --- Before printing: what should already be true -------------------------------------
    $printer = Get-Printer -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq $printerName } | Select-Object -First 1
    if (-not $printer) {
        & $note 'Fail' "Windows printer '$printerName' does not exist."
    } elseif ($printer.PortName -ne $expectedPort) {
        & $note 'Fail' "Printer '$printerName' is on port '$($printer.PortName)', not '$expectedPort'. The page will not reach the listener."
    } else {
        & $note 'Pass' "Printer '$printerName' is on port '$expectedPort' with driver '$($printer.DriverName)'."
    }
    $listenerState = Get-ErpTaskState 'Listener'
    & $note $(if ($listenerState -eq 'Running') { 'Pass' } else { 'Fail' }) "Listener task: $listenerState."
    $uploaderState = Get-ErpTaskState 'Uploader'
    & $note $(if ($uploaderState -eq 'Running') { 'Pass' } else { 'Warn' }) "Uploader task: $uploaderState."
    $evidence.PipeExists = Test-ErpPipeExists -PipeName $printerProfile.PipeName
    if ($evidence.PipeExists) { & $note 'Pass' "The listener is hosting $expectedPort." }
    elseif ($evidence.PipeExists -eq $false) { & $note 'Fail' "Nothing is hosting $expectedPort; the spooler has nowhere to send the page." }

    if (-not (Test-ErpFolderReadable $config.Global.DataRoot)) {
        & $note 'Warn' "The data folder '$($config.Global.DataRoot)' cannot be read from this session, so the outbox cannot be watched. Run elevated."
    }
    $known = @{}
    foreach ($folder in $paths.Outbox, $paths.Failed, $paths.Sent) {
        foreach ($f in Get-ChildItem -LiteralPath $folder -Filter '*.json' -ErrorAction SilentlyContinue) { $known[$f.BaseName] = $true }
    }

    # --- Print ----------------------------------------------------------------------------
    if ($printer) {
        try {
            Send-ErpTestPage -PrinterName $printerName -DocumentName $documentName -ProfileName $printerProfile.Name -Token $token
            $evidence.Submitted = $true
            & $note 'Pass' "Sent '$documentName' to the spooler for '$printerName'."
        } catch {
            & $note 'Fail' "The spooler refused the test page: $($_.Exception.Message)"
        }
    }

    # --- Watch it travel ------------------------------------------------------------------
    $deadline = $start.AddSeconds($TimeoutSeconds)
    $seenJob = $false
    $jobGone = $false
    $lastStatus = ''
    $badSince = $null
    $receivedId = $null
    $lastProgress = Get-Date
    while ($evidence.Submitted -and (Get-Date) -lt $deadline) {
        $job = @(Get-PrintJob -PrinterName $printerName -ErrorAction SilentlyContinue) | Where-Object { $_.DocumentName -like "*$token*" } | Select-Object -First 1
        if ($job) {
            $status = [string]$job.JobStatus
            if (-not $seenJob) {
                $seenJob = $true
                & $note 'Info' "Job $($job.Id) is in the print queue (status '$status')."
            } elseif ($status -ne $lastStatus) {
                & $note 'Info' "Job $($job.Id) status changed from '$lastStatus' to '$status'."
            }
            $lastStatus = $status
            if (Test-ErpBadJobStatus $status) { if (-not $badSince) { $badSince = Get-Date } } else { $badSince = $null }
        } elseif ($seenJob -and -not $jobGone) {
            $jobGone = $true
            & $note 'Info' 'The job left the print queue: the spooler is done with it.'
        }

        $found = Find-ErpTestPrintItem -Paths $paths -Known $known -Token $token
        if ($found) {
            if (-not $receivedId) {
                $receivedId = $found.Id
                $evidence.Received = $true
                & $note 'Pass' "The listener received it and queued document $($found.Id) ($($found.Folder))."
            }
            if ($found.Folder -eq 'sent') { $evidence.Delivered = $true; & $note 'Pass' "Delivered to the ERP (remote id $($found.Meta.remoteId))."; break }
            if ($found.Folder -eq 'failed') {
                $evidence.Failed = $true
                $evidence.FailedError = [string]$found.Meta.lastError
                & $note 'Fail' "The ERP rejected it: $($found.Meta.lastError)"
                break
            }
            if ($found.Meta.lastError) {
                $evidence.LastDeliveryError = [string]$found.Meta.lastError
                & $note 'Fail' "Delivery attempt $($found.Meta.attempts) failed: $($found.Meta.lastError)"
                break
            }
        } elseif ($receivedId) {
            # Gone from every folder: delivered, and KeepSentDays is 0 so nothing was archived.
            $evidence.Delivered = $true
            & $note 'Pass' 'Delivered to the ERP (the document was removed from the outbox).'
            break
        }
        if ($badSince -and ((Get-Date) - $badSince).TotalSeconds -ge $script:ErpTestPrintStuckSeconds) {
            & $note 'Fail' "Job stayed in state '$lastStatus' for $script:ErpTestPrintStuckSeconds seconds; not waiting any longer."
            break
        }
        if (((Get-Date) - $lastProgress).TotalSeconds -ge 3) {
            $lastProgress = Get-Date
            if ($OnProgress) { try { & $OnProgress ("Waiting for the page to arrive... {0:N0}s" -f ((Get-Date) - $start).TotalSeconds) } catch { Write-Verbose "Progress callback failed: $_" } }
        }
        Start-Sleep -Milliseconds 500
    }

    # --- Collect what the spooler and the listener say about it --------------------------
    if ($evidence.Submitted) {
        $evidence.JobSeen = $seenJob
        $evidence.JobGone = $jobGone
        $evidence.LastJobStatus = $lastStatus
        $evidence.JobStuck = ($seenJob -and -not $jobGone -and -not $evidence.Received -and -not $evidence.Delivered)
        $evidence.TimedOut = -not ($evidence.Delivered -or $evidence.Failed -or $evidence.LastDeliveryError)

        $logInfo = Get-WinEvent -ListLog 'Microsoft-Windows-PrintService/Operational' -ErrorAction SilentlyContinue
        if ($logInfo -and -not $logInfo.IsEnabled) {
            & $note 'Warn' 'The PrintService Operational log is disabled, so the spooler cannot report what it did with the job (wevtutil sl Microsoft-Windows-PrintService/Operational /e:true).'
        }
        try {
            $events = @(Get-ErpPrintServiceEvents -Log Operational -Since $start.AddSeconds(-2) -Max 60 |
                    Where-Object { $_.Message -like "*$token*" -or $_.Message -like "*$printerName*" -or $_.Message -like "*$expectedPort*" -or $_.Level -le 3 })
            foreach ($ev in $events | Sort-Object TimeCreated) {
                $isMine = $ev.Message -like "*$token*"
                & $note $(if ($ev.Level -le 3) { 'Warn' } else { 'Info' }) ("Spooler log: " + (Format-ErpEventLine $ev))
                if ($isMine -and $ev.Id -eq 307) {
                    $evidence.SpoolerPrinted = $true
                    if ($ev.Message -like "*$expectedPort*") { $evidence.SpoolerPort = $expectedPort }
                    elseif ($ev.Message -match 'via port (?<port>.+?)\.\s') { $evidence.SpoolerPort = $Matches.port }
                }
            }
            $evidence.ErrorEvents = @($events | Where-Object { $_.Level -le 3 }).Count
        } catch { & $note 'Warn' "Could not read the spooler log: $($_.Exception.Message)" }
        try {
            foreach ($ev in @(Get-ErpPrintServiceEvents -Log Admin -Since $start.AddSeconds(-2) -Level 1, 2, 3 -Max 10)) {
                & $note 'Warn' ("Spooler Admin log: " + (Format-ErpEventLine $ev))
            }
        } catch { & $note 'Warn' "Could not read the spooler Admin log: $($_.Exception.Message)" }

        $listenerLines = @(Get-ErpRecentLogEntries -LogDir (Join-Path $config.Global.DataRoot 'logs') -Hours 1 -Component 'listener' | Where-Object { $_.Time -ge $start.AddSeconds(-2) })
        $evidence.ListenerConnected = [bool]@($listenerLines | Where-Object { $_.Message -like 'The spooler connected*' }).Count
        foreach ($line in $listenerLines) {
            & $note $(if ($line.Level -in 'Error', 'Warning') { 'Warn' } else { 'Info' }) ("Listener log: " + (Limit-ErpString $line.Message 300))
        }
        $listenerFailure = $listenerLines | Where-Object { $_.Level -in 'Error', 'Warning' } | Select-Object -First 1
        if ($listenerFailure -and -not $evidence.Received) { $evidence.ListenerError = $listenerFailure.Message }
        if (-not $listenerLines.Count) { & $note 'Info' 'The listener log has no entries since the page was printed.' }
        if ($evidence.Received -and -not $evidence.Delivered) {
            & $note $(if ((Get-ErpTaskState 'Uploader') -eq 'Running') { 'Info' } else { 'Fail' }) "Uploader task: $(Get-ErpTaskState 'Uploader')."
        }
    }

    $verdict = Get-ErpTestPrintVerdict -Evidence $evidence
    $report = Format-ErpTestPrintReport -Verdict $verdict -Steps $steps -ProfileName $printerProfile.Name
    [pscustomobject]@{
        Success  = [bool]$verdict.Success
        Stage    = $verdict.Stage
        Summary  = $verdict.Summary
        Hint     = $verdict.Hint
        Token    = $token
        Steps    = $steps.ToArray()
        Evidence = $evidence
        Report   = $report
    }
}

function Send-ErpTestPage {
    <# Prints a one-page document through the Windows printing API, as any application would. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $PrinterName,
        [Parameter(Mandatory)] [string] $DocumentName,
        [Parameter(Mandatory)] [string] $ProfileName,
        [Parameter(Mandatory)] [string] $Token
    )
    Add-Type -AssemblyName System.Drawing
    $script:ErpTestPageText = @(
        'ERP Printer test page', '',
        "Printer:  $PrinterName", "Profile:  $ProfileName", "Computer: $env:COMPUTERNAME",
        "User:     $env:USERDOMAIN\$env:USERNAME", "Time:     $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')", "Test id:  $Token", '',
        'If this page arrives in your ERP, printing works end to end.'
    ) -join [Environment]::NewLine
    $doc = New-Object Drawing.Printing.PrintDocument
    try {
        $doc.DocumentName = $DocumentName
        $doc.PrinterSettings.PrinterName = $PrinterName
        if (-not $doc.PrinterSettings.IsValid) { throw "Printer '$PrinterName' is not valid for printing from this session." }
        $doc.PrintController = New-Object Drawing.Printing.StandardPrintController
        $doc.add_PrintPage({
                $page = $args[1]
                $font = New-Object Drawing.Font('Segoe UI', 14)
                try { $page.Graphics.DrawString($script:ErpTestPageText, $font, [Drawing.Brushes]::Black, 72, 72) } finally { $font.Dispose() }
                $page.HasMorePages = $false
            })
        $doc.Print()
    } finally {
        $doc.Dispose()
    }
}

function Find-ErpTestPrintItem {
    <#
    Looks for the test page's document in the outbox, failed and sent folders. Matches the
    unique token in the title; if the spooler job could not be looked up (so the title is
    empty) a single new untitled document is accepted.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [hashtable] $Paths,
        [Parameter(Mandatory)] [hashtable] $Known,
        [Parameter(Mandatory)] [string] $Token
    )
    $candidates = New-Object System.Collections.Generic.List[object]
    foreach ($pair in @(@('outbox', $Paths.Outbox), @('failed', $Paths.Failed), @('sent', $Paths.Sent))) {
        foreach ($file in Get-ChildItem -LiteralPath $pair[1] -Filter '*.json' -ErrorAction SilentlyContinue) {
            if ($Known.ContainsKey($file.BaseName)) { continue }
            try { $meta = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json } catch { continue }
            $meta = Complete-ErpOutboxMeta -Meta $meta -Id $file.BaseName
            $candidates.Add([pscustomobject]@{ Id = $file.BaseName; Folder = $pair[0]; Meta = $meta })
        }
    }
    $match = $candidates | Where-Object { "$($_.Meta.title)" -like "*$Token*" } | Select-Object -First 1
    if (-not $match) {
        $untitled = @($candidates | Where-Object { -not $_.Meta.title })
        if ($untitled.Count -eq 1) { $match = $untitled[0] }
    }
    $match
}

function Format-ErpTestPrintReport {
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)] $Verdict, [Parameter(Mandatory)] [object[]] $Steps, [Parameter(Mandatory)] [string] $ProfileName)
    $builder = New-Object System.Text.StringBuilder
    $null = $builder.AppendLine("ERP Printer test print - profile '$ProfileName'")
    $null = $builder.AppendLine(('{0:yyyy-MM-dd HH:mm:ss} on {1}' -f (Get-Date), $env:COMPUTERNAME))
    $null = $builder.AppendLine()
    $null = $builder.AppendLine("Result: $(if ($Verdict.Success) { 'SUCCESS' } else { 'FAILED' }) (reached stage: $($Verdict.Stage))")
    $null = $builder.AppendLine($Verdict.Summary)
    if ($Verdict.Hint) { $null = $builder.AppendLine("Next step: $($Verdict.Hint)") }
    $null = $builder.AppendLine()
    $null = $builder.AppendLine('What happened:')
    foreach ($step in $Steps) {
        $null = $builder.AppendLine(('  {0,5:N1}s [{1,-4}] {2}' -f $step.Seconds, $step.Status.ToUpper(), $step.Message))
    }
    $builder.ToString().TrimEnd() + [Environment]::NewLine
}
