function Start-ErpPrinterListener {
    <#
    .SYNOPSIS
        Hosts one named pipe per enabled profile and writes each print job to that profile's outbox.
    .DESCRIPTION
        Runs until stopped (normally as the SYSTEM "Listener" scheduled task). The spooler
        sends one job at a time per queue, so each pipe connection is exactly one complete
        PDF. Profiles are re-read every -ReloadSeconds. When they change (including at
        startup), Windows printers are reconciled with Sync-ErpPrinterQueue, so profiles
        pushed by Group Policy or scripts get their printers without further action.
    #>
    [CmdletBinding()]
    param([int] $ReloadSeconds = 30)
    $config = Get-ErpPrinterConfig
    Set-ErpLogContext -Component 'listener' -GlobalSettings $config.Global
    $signal = Get-ErpOutboxSignal
    $servers = @{}
    $stamp = $null
    $nextReload = [DateTime]::MinValue
    $nextHeartbeat = (Get-Date).AddHours(1)
    $jobsReceived = 0
    $lastJob = $null
    Write-ErpLog -Message "Listener started (version $(Get-ErpPrinterVersion), PID $PID)."
    Write-ErpLog -Message (Get-ErpRuntimeSummary -GlobalSettings $config.Global)

    try {
        while ($true) {
            if ((Get-Date) -ge $nextReload) {
                $config = Get-ErpPrinterConfig
                Set-ErpLogContext -Component 'listener' -GlobalSettings $config.Global
                $enabled = @($config.Profiles | Where-Object { $_.Enabled })
                $newStamp = ($enabled | ForEach-Object { "$($_.Name)|$($_.PipeName)|$($_.PrinterName)" }) -join ';'
                if ($newStamp -ne $stamp) {
                    try { Sync-ErpPrinterQueue -NoRestart -Confirm:$false }
                    catch { Write-ErpLog -Level Error -Message "Printer reconciliation failed: $($_.Exception.Message)" }
                    Close-ErpPipeServers $servers
                    foreach ($p in $enabled) {
                        $servers[$p.PipeName] = @{ Profile = $p; Pipe = $null; Wait = $null; Failures = 0; Announced = $false }
                    }
                    $stamp = $newStamp
                    Write-ErpLog -Message "Listening on $($servers.Count) pipe(s): $((@($servers.Keys) | Sort-Object) -join ', ')"
                    if (-not $servers.Count) {
                        Write-ErpLog -Level Warning -Message 'No enabled printer profiles, so nothing is listening. Create or enable a profile in the configuration tool.'
                    }
                } else {
                    # Refresh settings of unchanged pipes (e.g. a new FileNameTemplate).
                    foreach ($p in $enabled) { if ($servers.ContainsKey($p.PipeName)) { $servers[$p.PipeName].Profile = $p } }
                }
                $nextReload = (Get-Date).AddSeconds($ReloadSeconds)
            }

            if ((Get-Date) -ge $nextHeartbeat) {
                $last = if ($lastJob) { "last job at $($lastJob.ToString('yyyy-MM-dd HH:mm:ss'))" } else { 'no jobs since start' }
                Write-ErpLog -Message "Listener alive: $($servers.Count) pipe(s), $jobsReceived job(s) received since start, $last."
                $nextHeartbeat = (Get-Date).AddHours(1)
            }

            foreach ($entry in @($servers.Values)) {
                if ($entry.Wait) { continue }
                try {
                    $entry.Pipe = New-ErpPipeServer -PipeName $entry.Profile.PipeName
                    $entry.Wait = $entry.Pipe.WaitForConnectionAsync()
                    if ($entry.Failures -gt 0) {
                        Write-ErpLog -Message "Pipe '$($entry.Profile.PipeName)' recovered after $($entry.Failures) failure(s)."
                    }
                    $entry.Failures = 0
                    if (-not $entry.Announced) {
                        $entry.Announced = $true
                        $state = Test-ErpPipeExists -PipeName $entry.Profile.PipeName
                        $visible = if ($null -eq $state) { 'visibility not checked' } elseif ($state) { 'visible to the spooler' } else { 'NOT visible in \\.\pipe\' }
                        Write-ErpLog -Message "Waiting for print jobs for '$($entry.Profile.Name)' on $(Get-ErpPortName $entry.Profile.PipeName) (printer '$($entry.Profile.PrinterName)'; $visible)."
                    }
                } catch {
                    $entry.Failures++
                    if ($entry.Failures -eq 1 -or $entry.Failures % 60 -eq 0) {
                        Write-ErpExceptionLog -Message "Cannot open pipe '$($entry.Profile.PipeName)' (failure $($entry.Failures))" -ErrorRecord $_
                    }
                }
            }

            $active = @($servers.Values | Where-Object { $_.Wait })
            if (-not $active.Count) {
                Start-Sleep -Seconds 5
                continue
            }
            $tasks = [System.Threading.Tasks.Task[]]@($active | ForEach-Object { $_.Wait })
            $index = [System.Threading.Tasks.Task]::WaitAny($tasks, 5000)
            if ($index -lt 0) { continue }

            $entry = $active[$index]
            try {
                if ($entry.Wait.IsFaulted) { throw $entry.Wait.Exception.GetBaseException() }
                Write-ErpLog -Message "The spooler connected to pipe '$($entry.Profile.PipeName)'; receiving a job for '$($entry.Profile.Name)'."
                Receive-ErpPrintJob -Pipe $entry.Pipe -PrinterProfile $entry.Profile -GlobalSettings $config.Global
                $jobsReceived++
                $lastJob = Get-Date
                $null = $signal.Set()
            } catch {
                Write-ErpExceptionLog -Message "Failed to receive a job for '$($entry.Profile.Name)'" -ErrorRecord $_
            } finally {
                $entry.Pipe.Dispose()
                $entry.Pipe = $null
                $entry.Wait = $null
            }
        }
    } catch {
        # Without this a crash only shows up as "Listener stopped" with no reason, or not at all.
        Write-ErpExceptionLog -Message 'The listener crashed' -ErrorRecord $_
        throw
    } finally {
        Close-ErpPipeServers $servers
        Write-ErpLog -Message 'Listener stopped.'
    }
}

function Close-ErpPipeServers {
    param([hashtable] $Servers)
    foreach ($entry in @($Servers.Values)) {
        if ($entry.Pipe) { try { $entry.Pipe.Dispose() } catch { Write-Verbose "Pipe dispose: $_" } }
    }
    $Servers.Clear()
}

function Get-ErpOutboxSignal {
    <# Named event the listener sets after each job so the uploader wakes immediately. #>
    $created = $false
    New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::AutoReset, $script:ErpOutboxSignal, [ref]$created)
}

function Receive-ErpPrintJob {
    <# Streams one job from a connected pipe to <id>.pdf and writes the <id>.json sidecar. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Pipe,
        [Parameter(Mandatory)] [System.Collections.IDictionary] $PrinterProfile,
        [Parameter(Mandatory)] [System.Collections.IDictionary] $GlobalSettings
    )
    # Look the job up while the spooler still shows it as printing.
    $job = Get-ErpActivePrintJob -PrinterName $PrinterProfile.PrinterName
    if (-not $job) {
        Write-ErpLog -Level Warning -Message "A job arrived on '$($PrinterProfile.PrinterName)' but the spooler lists none, so the user and title are unknown."
    }
    $paths = Get-ErpProfilePaths -DataRoot $GlobalSettings.DataRoot -ProfileName $PrinterProfile.Name
    Initialize-ErpProfilePaths $paths
    $id = [guid]::NewGuid().ToString('N')
    $pdf = Join-Path $paths.Outbox "$id.pdf"
    $tmp = "$pdf.tmp"

    try {
        $file = [IO.File]::Create($tmp)
        try { $Pipe.CopyTo($file) } finally { $file.Dispose() }
        $size = (Get-Item -LiteralPath $tmp).Length
        if ($size -eq 0) {
            Remove-Item -LiteralPath $tmp -Force
            Write-ErpLog -Level Warning -Message "Discarded an empty job on '$($PrinterProfile.PrinterName)': the spooler connected to the pipe but sent no data."
            return
        }
        $header = New-Object byte[] 5
        $stream = [IO.File]::OpenRead($tmp)
        try { $null = $stream.Read($header, 0, 5) } finally { $stream.Dispose() }
        if ([Text.Encoding]::ASCII.GetString($header) -ne '%PDF-') {
            $hex = ($header | ForEach-Object { $_.ToString('X2') }) -join ' '
            Write-ErpLog -Level Warning -Message "Job on '$($PrinterProfile.PrinterName)' does not look like a PDF (starts with $hex, $size bytes); is the queue using the '$script:ErpPdfDriver' driver?"
        }
        Move-Item -LiteralPath $tmp -Destination $pdf
    } catch {
        # Do not leave a half-written file behind for the orphan recovery to adopt.
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        throw
    }

    $metaArgs = @{ Id = $id; ProfileName = $PrinterProfile.Name; SizeBytes = $size }
    if ($job) {
        $metaArgs.User = [string]$job.UserName
        $metaArgs.Title = [string]$job.DocumentName
        $metaArgs.JobId = [string]$job.Id
        $metaArgs.Pages = [int]$job.TotalPages
        if ($job.SubmittedTime) { $metaArgs.Printed = $job.SubmittedTime }
    }
    $meta = New-ErpOutboxMeta @metaArgs
    Save-ErpOutboxMeta -Folder $paths.Outbox -Meta $meta
    Write-ErpLog -Message "Queued '$($meta.title)' from $($meta.user) ($([Math]::Round($size / 1KB)) KB) on '$($PrinterProfile.PrinterName)' as $id." -EventId 1001
}
