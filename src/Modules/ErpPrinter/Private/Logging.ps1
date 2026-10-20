$script:ErpLogLevels = @{ Error = 1; Warning = 2; Information = 3; Verbose = 4 }
$script:ErpLogSettings = $null
$script:ErpLogFileFailureReported = $false

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
        } catch {
            Write-Verbose "Could not write log file: $_"
            # A log that silently stops growing is the worst failure mode, so say so once in the event log.
            Write-ErpLogFileFailure -Message "Could not write to the log folder '$($script:ErpLogSettings.LogDir)': $($_.Exception.Message)"
        }
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

function Write-ErpLogFileFailure {
    <# Reports (once per process) that the log file cannot be written. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Message)
    if ($script:ErpLogFileFailureReported -or -not $script:ErpIsWindows) { return }
    $script:ErpLogFileFailureReported = $true
    try {
        if ([Diagnostics.EventLog]::SourceExists($script:ErpEventSource)) {
            [Diagnostics.EventLog]::WriteEntry($script:ErpEventSource, "[$script:ErpLogComponent] $Message", 'Warning', 2001)
        }
    } catch { Write-Verbose "Could not write event log: $_" }
}

function Write-ErpExceptionLog {
    <#
    Logs an error at Error level with its type and root cause, and the script stack trace at
    Verbose level, so a failure that used to vanish leaves enough to find the failing line.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Message,
        [Parameter(Mandatory)] $ErrorRecord,
        [int] $EventId = 0
    )
    $ex = if ($ErrorRecord -is [System.Management.Automation.ErrorRecord]) { $ErrorRecord.Exception } else { $ErrorRecord }
    $detail = "$($ex.Message)"
    $root = $ex.GetBaseException()
    if ($root -and $root -ne $ex) { $detail += " (caused by $($root.GetType().Name): $($root.Message))" }
    Write-ErpLog -Level Error -Message "${Message}: [$($ex.GetType().Name)] $detail" -EventId $EventId
    if ($ErrorRecord -is [System.Management.Automation.ErrorRecord]) {
        $stack = "$($ErrorRecord.ScriptStackTrace)".Trim()
        if ($stack) { Write-ErpLog -Level Verbose -Message ('Stack trace: ' + ($stack -replace '\r?\n', ' | ')) }
    }
}

function Get-ErpRecentLogEntries {
    <#
    Parses the log lines written in the last -Hours hours from every component's log files,
    oldest first. -Level keeps only those levels; -Component only that component's files.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)] [string] $LogDir,
        [int] $Hours = 24,
        [string[]] $Level = @('Error', 'Warning', 'Information', 'Verbose'),
        [string] $Component
    )
    if (-not (Test-Path -LiteralPath $LogDir -ErrorAction SilentlyContinue)) { return }
    $since = (Get-Date).AddHours(-$Hours)
    $pattern = '^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) \[(?<level>\w+)\s*\] \[(?<component>[^\]]*)\] (?<message>.*)$'
    $files = Get-ChildItem -LiteralPath $LogDir -Filter '*.log' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $since -and (-not $Component -or $_.Name -like "$Component-*") }
    $entries = foreach ($file in $files) {
        try { $lines = [IO.File]::ReadAllLines($file.FullName, [Text.Encoding]::UTF8) } catch { continue }
        foreach ($line in $lines) {
            if ($line -notmatch $pattern) { continue }
            $time = [DateTime]::ParseExact($Matches.time, 'yyyy-MM-dd HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture)
            if ($time -lt $since -or $Level -notcontains $Matches.level) { continue }
            [pscustomobject]@{ Time = $time; Level = $Matches.level; Component = $Matches.component; Message = $Matches.message }
        }
    }
    @($entries | Sort-Object Time)
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
