function Start-ErpPrinterUploader {
    <#
    .SYNOPSIS
        Delivers outbox documents until stopped (normally as the SYSTEM "Uploader" scheduled task).
    .DESCRIPTION
        Wakes when the listener signals a new job, or every PollIntervalSeconds, and runs
        Invoke-ErpPrinterUpload. Only one uploader runs at a time per machine.
    #>
    [CmdletBinding()]
    param()
    $mutex = New-Object System.Threading.Mutex($false, 'Global\ErpPrinterUploader')
    if (-not $mutex.WaitOne(0)) {
        Write-Warning 'Another uploader is already running.'
        return
    }
    try {
        $signal = Get-ErpOutboxSignal
        $config = Get-ErpPrinterConfig
        Set-ErpLogContext -Component 'uploader' -GlobalSettings $config.Global
        Write-ErpLog -Message "Uploader started (version $(Get-ErpPrinterVersion), PID $PID)."
        $nextHousekeeping = [DateTime]::MinValue
        while ($true) {
            $config = Get-ErpPrinterConfig
            Set-ErpLogContext -Component 'uploader' -GlobalSettings $config.Global
            try {
                $null = Invoke-ErpOutboxPass -Config $config
                if ((Get-Date) -ge $nextHousekeeping) {
                    Invoke-ErpHousekeeping -Config $config
                    $nextHousekeeping = (Get-Date).AddHours(1)
                }
            } catch {
                Write-ErpLog -Level Error -Message "Upload pass failed: $($_.Exception.Message)"
            }
            $null = $signal.WaitOne([TimeSpan]::FromSeconds($config.Global.PollIntervalSeconds))
        }
    } finally {
        $mutex.ReleaseMutex()
        $mutex.Dispose()
    }
}

function Invoke-ErpPrinterUpload {
    <#
    .SYNOPSIS
        Runs a single delivery pass over all outboxes and returns per-profile counts.
    .PARAMETER Force
        Ignore retry backoff and try every waiting document now.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([switch] $Force)
    $config = Get-ErpPrinterConfig
    Set-ErpLogContext -Component 'uploader' -GlobalSettings $config.Global
    Invoke-ErpOutboxPass -Config $config -IgnoreBackoff:$Force
}

function Invoke-ErpOutboxPass {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Config, [switch] $IgnoreBackoff, [DateTime] $Now = (Get-Date))
    $globalSettings = $Config.Global
    foreach ($printerProfile in $Config.Profiles) {
        $paths = Get-ErpProfilePaths -DataRoot $globalSettings.DataRoot -ProfileName $printerProfile.Name
        if (-not (Test-Path -LiteralPath $paths.Outbox)) { continue }
        Repair-ErpOrphanedPdfs -Folder $paths.Outbox -ProfileName $printerProfile.Name
        $stats = [pscustomobject]@{ Profile = $printerProfile.Name; Sent = 0; Retrying = 0; Failed = 0 }
        foreach ($item in Get-ErpOutboxItems -Folder $paths.Outbox -DueOnly:(-not $IgnoreBackoff) -Now $Now) {
            switch (Send-ErpOutboxItem -Item $item -PrinterProfile $printerProfile -GlobalSettings $globalSettings -Paths $paths -Now $Now) {
                'Sent' { $stats.Sent++ }
                'Failed' { $stats.Failed++ }
                default { $stats.Retrying++ }
            }
        }
        $stats
    }
}

function Send-ErpOutboxItem {
    <# Tries one item. Returns 'Sent', 'Retry' or 'Failed' and moves/updates the files accordingly. #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [hashtable] $Item,
        [Parameter(Mandatory)] [System.Collections.IDictionary] $PrinterProfile,
        [Parameter(Mandatory)] [System.Collections.IDictionary] $GlobalSettings,
        [Parameter(Mandatory)] [hashtable] $Paths,
        [DateTime] $Now = (Get-Date)
    )
    $meta = $Item.Meta
    $meta.attempts = [int]$meta.attempts + 1
    $meta.lastAttempt = $Now.ToString('o')
    $label = "'$($meta.title)' ($($meta.id)) for profile '$($PrinterProfile.Name)'"
    try {
        if (-not (Test-Path -LiteralPath $Item.PdfPath)) { throw (New-ErpException 'The PDF file is missing.' -Permanent) }
        $maxBytes = [long]$GlobalSettings.MaxDocumentSizeMB * 1MB
        if ((Get-Item -LiteralPath $Item.PdfPath).Length -gt $maxBytes) {
            throw (New-ErpException "Document is larger than MaxDocumentSizeMB ($($GlobalSettings.MaxDocumentSizeMB) MB)." -Permanent)
        }
        if (-not $PrinterProfile.Enabled) { throw (New-ErpException 'The printer profile is disabled.') }

        $remoteId = Send-ErpDocument -PrinterProfile $PrinterProfile -GlobalSettings $GlobalSettings -Item $Item
        $meta.remoteId = $remoteId
        $meta.sentAt = $Now.ToString('o')
        $meta.lastError = $null
        $meta.nextAttempt = $null
        if ($GlobalSettings.KeepSentDays -gt 0) { Move-ErpOutboxItem -Item $Item -Destination $Paths.Sent }
        else { Remove-ErpOutboxItem -Item $Item }
        Write-ErpLog -Message "Delivered $label to $($PrinterProfile.Backend) (remote id: $remoteId)." -EventId 1002
        return 'Sent'
    } catch {
        $message = $_.Exception.Message
        $permanent = Test-ErpPermanentError $_
        $meta.lastError = $message
        $exhausted = $GlobalSettings.MaxAttempts -gt 0 -and $meta.attempts -ge $GlobalSettings.MaxAttempts
        if ($permanent -or $exhausted) {
            $meta.nextAttempt = $null
            Move-ErpOutboxItem -Item $Item -Destination $Paths.Failed
            $why = if ($permanent) { 'permanent error' } else { "gave up after $($meta.attempts) attempts" }
            Write-ErpLog -Level Error -Message "Moved $label to the failed folder ($why): $message" -EventId 3002
            return 'Failed'
        }
        $delay = Get-ErpRetryDelaySeconds -Attempt $meta.attempts -MaxMinutes $GlobalSettings.RetryMaxBackoffMinutes
        $meta.nextAttempt = $Now.AddSeconds($delay).ToString('o')
        Save-ErpOutboxMeta -Folder $Paths.Outbox -Meta $meta
        Write-ErpLog -Level Warning -Message "Attempt $($meta.attempts) for $label failed, retrying in $delay s: $message" -EventId 2002
        return 'Retry'
    }
}

function Invoke-ErpHousekeeping {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)] $Config)
    Remove-ErpOldLogs -GlobalSettings $Config.Global
    $cutoff = (Get-Date).AddDays(-[Math]::Max($Config.Global.KeepSentDays, 0))
    foreach ($dir in Get-ChildItem -Path (Join-Path $Config.Global.DataRoot 'spool\*\sent') -Directory -ErrorAction SilentlyContinue) {
        Get-ChildItem -LiteralPath $dir.FullName -File | Where-Object { $_.LastWriteTime -lt $cutoff } |
            ForEach-Object { if ($PSCmdlet.ShouldProcess($_.FullName, 'Delete archived document')) { Remove-Item -LiteralPath $_.FullName -Force } }
    }
    $updates = Join-Path $Config.Global.DataRoot 'updates'
    Get-ChildItem -LiteralPath $updates -File -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-30) } |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

function Restore-ErpPrinterFailedDocument {
    <#
    .SYNOPSIS
        Moves failed documents back to the outbox with their attempt counter reset.
    .EXAMPLE
        Restore-ErpPrinterFailedDocument                       # every failed document, all profiles
        Restore-ErpPrinterFailedDocument -ProfileName 'Send to Odoo' -Id 3f2a...
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([int])]
    param([string] $ProfileName, [string] $Id)
    $config = Get-ErpPrinterConfig
    $count = 0
    foreach ($printerProfile in $config.Profiles | Where-Object { -not $ProfileName -or $_.Name -eq $ProfileName }) {
        $paths = Get-ErpProfilePaths -DataRoot $config.Global.DataRoot -ProfileName $printerProfile.Name
        foreach ($item in Get-ErpOutboxItems -Folder $paths.Failed) {
            if ($Id -and $item.Meta.id -ne $Id) { continue }
            if (-not $PSCmdlet.ShouldProcess($item.Meta.id, 'Retry failed document')) { continue }
            $item.Meta.attempts = 0
            $item.Meta.nextAttempt = $null
            Move-ErpOutboxItem -Item $item -Destination $paths.Outbox
            $count++
        }
    }
    try { $null = (Get-ErpOutboxSignal).Set() } catch { Write-Verbose "Could not signal uploader: $_" }
    $count
}

function Get-ErpPrinterStatus {
    <#
    .SYNOPSIS
        Summarises tasks, printer queues and outbox contents per profile.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param()
    $config = Get-ErpPrinterConfig
    $printers = @()
    if ($script:ErpIsWindows) { $printers = @(Get-Printer -ErrorAction SilentlyContinue) }
    $profiles = foreach ($printerProfile in $config.Profiles) {
        $paths = Get-ErpProfilePaths -DataRoot $config.Global.DataRoot -ProfileName $printerProfile.Name
        $queue = $printers | Where-Object { $_.Name -eq $printerProfile.PrinterName } | Select-Object -First 1
        $pending = @(Get-ErpOutboxItems -Folder $paths.Outbox)
        $failed = @(Get-ErpOutboxItems -Folder $paths.Failed)
        $lastError = @($pending + $failed | Where-Object { $_.Meta.lastError } | ForEach-Object { $_.Meta.lastError }) | Select-Object -Last 1
        [pscustomobject]@{
            Profile     = $printerProfile.Name
            Enabled     = $printerProfile.Enabled
            Backend     = $printerProfile.Backend
            Printer     = $printerProfile.PrinterName
            PrinterOk   = [bool]($queue -and $queue.PortName -eq (Get-ErpPortName $printerProfile.PipeName))
            Pending     = $pending.Count
            Failed      = $failed.Count
            Sent        = @(Get-ChildItem -LiteralPath $paths.Sent -Filter '*.json' -ErrorAction SilentlyContinue).Count
            LastError   = $lastError
            OutboxPath  = $paths.Root
        }
    }
    [pscustomobject]@{
        Version  = Get-ErpPrinterVersion
        Listener = if ($script:ErpIsWindows) { Get-ErpTaskState 'Listener' } else { 'n/a' }
        Uploader = if ($script:ErpIsWindows) { Get-ErpTaskState 'Uploader' } else { 'n/a' }
        Updater  = if ($script:ErpIsWindows) { Get-ErpTaskState 'Updater' } else { 'n/a' }
        DataRoot = $config.Global.DataRoot
        Profiles = @($profiles)
    }
}
