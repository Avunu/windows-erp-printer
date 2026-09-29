$script:ErpLogLevels = @{ Error = 1; Warning = 2; Information = 3; Verbose = 4 }
$script:ErpLogSettings = $null

function Set-ErpLogContext {
    <# Called by long-running entry points so log lines are tagged and levels come from config. #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)] [string] $Component, [System.Collections.IDictionary] $GlobalSettings)
    if (-not $PSCmdlet.ShouldProcess($Component, 'Set log context')) { return }
    $script:ErpLogComponent = $Component
    if ($GlobalSettings) {
        $script:ErpLogSettings = @{
            Level  = $GlobalSettings.LogLevel
            LogDir = Join-Path $GlobalSettings.DataRoot 'logs'
        }
    }
}

function Write-ErpLog {
    [CmdletBinding()]
    param(
        [ValidateSet('Error', 'Warning', 'Information', 'Verbose')] [string] $Level = 'Information',
        [Parameter(Mandatory)] [string] $Message,
        [int] $EventId = 0
    )
    $configured = 'Information'
    if ($script:ErpLogSettings -and $script:ErpLogSettings.Level) { $configured = $script:ErpLogSettings.Level }
    if ($script:ErpLogLevels[$Level] -gt $script:ErpLogLevels[$configured]) {
        Write-Verbose $Message
        return
    }

    $line = '{0:yyyy-MM-dd HH:mm:ss.fff} [{1,-11}] [{2}] {3}' -f (Get-Date), $Level, $script:ErpLogComponent, $Message
    switch ($Level) {
        'Error'   { Write-Warning $Message }
        'Warning' { Write-Warning $Message }
        default   { Write-Verbose $Message }
    }

    if ($script:ErpLogSettings -and $script:ErpLogSettings.LogDir) {
        try {
            if (-not (Test-Path $script:ErpLogSettings.LogDir)) { New-Item -ItemType Directory -Path $script:ErpLogSettings.LogDir -Force | Out-Null }
            $file = Join-Path $script:ErpLogSettings.LogDir ('{0}-{1:yyyyMMdd}.log' -f $script:ErpLogComponent, (Get-Date))
            [IO.File]::AppendAllText($file, $line + [Environment]::NewLine, [Text.Encoding]::UTF8)
        } catch { Write-Verbose "Could not write log file: $_" }
    }

    # Event log gets warnings, errors, and information-level delivery events.
    if ($script:ErpIsWindows -and ($Level -ne 'Verbose')) {
        try {
            if ($EventId -eq 0) { $EventId = @{ Error = 3000; Warning = 2000; Information = 1000 }[$Level] }
            $entryType = @{ Error = 'Error'; Warning = 'Warning'; Information = 'Information' }[$Level]
            if ([Diagnostics.EventLog]::SourceExists($script:ErpEventSource)) {
                [Diagnostics.EventLog]::WriteEntry($script:ErpEventSource, "[$script:ErpLogComponent] $Message", $entryType, $EventId)
            }
        } catch { Write-Verbose "Could not write event log: $_" }
    }
}

function Remove-ErpOldLogs {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)] [System.Collections.IDictionary] $GlobalSettings)
    $dir = Join-Path $GlobalSettings.DataRoot 'logs'
    if (-not (Test-Path $dir)) { return }
    $cutoff = (Get-Date).AddDays(-$GlobalSettings.LogRetentionDays)
    Get-ChildItem -Path $dir -Filter '*.log' | Where-Object { $_.LastWriteTime -lt $cutoff } |
        ForEach-Object { if ($PSCmdlet.ShouldProcess($_.FullName, 'Delete old log')) { Remove-Item $_.FullName -Force } }
}
