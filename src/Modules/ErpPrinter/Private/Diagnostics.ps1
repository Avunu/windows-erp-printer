# Building blocks for Invoke-ErpPrinterDiagnostics and Invoke-ErpPrinterTestPrint.
#
# Every check returns result objects (Category, Name, Status, Detail, Hint) and is run through
# Invoke-ErpDiagnosticStep, so one check that cannot run never hides the others. Status is
# Pass, Info, Warn or Fail. Hints say what to do next, not just what is wrong.

$script:ErpDiagnosticStatusOrder = @{ Fail = 0; Warn = 1; Info = 2; Pass = 3 }

function New-ErpDiagnosticResult {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)] [string] $Category,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [ValidateSet('Pass', 'Info', 'Warn', 'Fail')] [string] $Status,
        [string] $Detail = '',
        [string] $Hint = ''
    )
    [pscustomobject][ordered]@{ Category = $Category; Name = $Name; Status = $Status; Detail = $Detail; Hint = $Hint }
}

function Invoke-ErpDiagnosticStep {
    <# Runs one check; if the check itself throws, that becomes a warning instead of aborting the run. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Category, [Parameter(Mandatory)] [string] $Name, [Parameter(Mandatory)] [scriptblock] $Script)
    try {
        @(& $Script)
    } catch {
        New-ErpDiagnosticResult -Category $Category -Name $Name -Status Warn -Detail "The check itself failed: $($_.Exception.Message)"
    }
}

function ConvertTo-ErpTaskResultText {
    <# Translates a scheduled task's LastTaskResult into something a person can act on. #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)] [int64] $Code)
    $unsigned = if ($Code -lt 0) { $Code + 4294967296 } else { $Code }
    $hex = '0x{0:X8}' -f $unsigned
    $text = switch ($unsigned) {
        0 { 'completed successfully' }
        1 { 'the script exited with an error (see the cli-*.log files in the log folder)' }
        267008 { 'ready to run' }
        267009 { 'currently running' }
        267010 { 'disabled' }
        267011 { 'has not run yet' }
        267014 { 'was stopped' }
        2147942402 { 'file not found (the install folder or ErpPrinter.ps1 is missing)' }
        2147942405 { 'access denied' }
        2147943645 { 'the task could not start (check the account and logon rights)' }
        3221225786 { 'terminated by Ctrl+C or a console close' }
        default { 'unrecognized result' }
    }
    "$hex ($text)"
}

function Get-ErpTaskProcesses {
    <# Finds running ERP Printer script processes for a command (Listen, Upload). Returns $null when the command lines are unreadable. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Command)
    $processes = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'powershell.exe' OR Name = 'pwsh.exe'" -ErrorAction Stop)
    if ($processes.Count -and -not @($processes | Where-Object { $_.CommandLine }).Count) { return $null }
    @($processes | Where-Object { $_.CommandLine -match "ErpPrinter\.ps1.*-Command\s+$Command\b" })
}

function Get-ErpPrintServiceEvents {
    <# Reads events from a PrintService log. Returns nothing (instead of throwing) when none match. #>
    [CmdletBinding()]
    param(
        [ValidateSet('Operational', 'Admin')] [string] $Log = 'Operational',
        [Parameter(Mandatory)] [DateTime] $Since,
        [int[]] $Level,
        [int[]] $Id,
        [int] $Max = 50
    )
    $filter = @{ LogName = "Microsoft-Windows-PrintService/$Log"; StartTime = $Since }
    if ($Level) { $filter.Level = $Level }
    if ($Id) { $filter.Id = $Id }
    try {
        @(Get-WinEvent -FilterHashtable $filter -MaxEvents $Max -ErrorAction Stop)
    } catch {
        if ($_.FullyQualifiedErrorId -like 'NoMatchingEventsFound*' -or $_.Exception.Message -like '*No events were found*') { return @() }
        throw
    }
}

function Format-ErpEventLine {
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)] $LogEvent, [int] $MaxLength = 260)
    $text = ("$($LogEvent.Message)" -replace '\s+', ' ').Trim()
    if ($text.Length -gt $MaxLength) { $text = $text.Substring(0, $MaxLength) + '...' }
    '{0:yyyy-MM-dd HH:mm:ss} [{1}] id {2}: {3}' -f $LogEvent.TimeCreated, $LogEvent.LevelDisplayName, $LogEvent.Id, $text
}

function Test-ErpFolderReadable {
    <# False only when the folder exists but this session may not list it (the data folder is limited to SYSTEM and Administrators). #>
    [CmdletBinding()]
    [OutputType([bool])]
    param([Parameter(Mandatory)] [string] $Path)
    try { $null = Get-ChildItem -LiteralPath $Path -ErrorAction Stop; $true }
    catch [System.UnauthorizedAccessException] { $false }
    catch { $true }
}

function Get-ErpPrinterStateInfo {
    <#
    Whether the spooler will process jobs for a printer at all: paused, offline, "Use Printer
    Offline", or outside its availability hours all leave jobs waiting with status Normal.
    Returns Problem (text, or $null when the printer can print), Hint and a one-line Summary.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([Parameter(Mandatory)] $Printer)
    $statusText = [string]$Printer.PrinterStatus
    $cim = Get-CimInstance -ClassName Win32_Printer -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq $Printer.Name } | Select-Object -First 1
    $problem = $null
    $hint = ''
    $availability = ''
    if ($cim -and $cim.WorkOffline) {
        $problem = 'The printer is set to "Use Printer Offline", so the spooler holds every job and never sends it.'
        $hint = 'Open the printer queue, Printer menu, and untick "Use Printer Offline".'
    } elseif ($statusText -and $statusText -notin 'Normal', 'Idle', 'Printing', 'Processing', 'WarmingUp') {
        $problem = "The spooler reports the printer as $statusText."
        $hint = 'Open the printer queue and clear that state (Printer menu: untick "Pause Printing" or "Use Printer Offline"); jobs are not sent while it lasts.'
    } elseif ($cim -and $cim.StartTime -and $cim.UntilTime -and $cim.StartTime -ne $cim.UntilTime) {
        $from = ([DateTime]$cim.StartTime).TimeOfDay
        $until = ([DateTime]$cim.UntilTime).TimeOfDay
        $now = (Get-Date).TimeOfDay
        $inside = if ($from -le $until) { $now -ge $from -and $now -lt $until } else { $now -ge $from -or $now -lt $until }
        $availability = " (available {0:hh\:mm}-{1:hh\:mm})" -f $from, $until
        if (-not $inside) {
            $problem = "The printer is only available between {0:hh\:mm} and {1:hh\:mm} and it is outside those hours, so the spooler holds jobs." -f $from, $until
            $hint = 'Printer properties, Advanced tab: choose "Always available".'
        }
    }
    [pscustomobject]@{
        Problem = $problem
        Hint    = $hint
        Summary = "$(if ($statusText) { $statusText } else { 'Normal' })$availability"
    }
}

function Format-ErpPrintJobList {
    <# One-line-per-job description of queue contents. #>
    [CmdletBinding()]
    [OutputType([string])]
    param([object[]] $Jobs, [int] $Max = 5)
    ($Jobs | Select-Object -First $Max | ForEach-Object {
            "job $($_.Id) '$($_.DocumentName)' ($($_.JobStatus), submitted $($_.SubmittedTime))"
        }) -join '; '
}

function Test-ErpBadJobStatus {
    <# True for spooler job states that mean the job will not reach the port by itself. #>
    [CmdletBinding()]
    [OutputType([bool])]
    param([string] $Status)
    [bool]($Status -match 'Error|Offline|Paused|Blocked|UserIntervention|PaperOut|Deleting|Restarted')
}

#region System, storage, logging --------------------------------------------------------

function Get-ErpSystemChecks {
    [CmdletBinding()]
    param()
    $category = 'System'
    $osName = try { (Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop).Caption } catch { 'Windows' }
    New-ErpDiagnosticResult $category 'ERP Printer' Info "Version $(Get-ErpPrinterVersion), build $($script:ErpBuildInfo.Commit); computer $env:COMPUTERNAME"
    New-ErpDiagnosticResult $category 'Operating system' Info "$osName, version $([Environment]::OSVersion.Version)"

    $identity = try { [Security.Principal.WindowsIdentity]::GetCurrent().Name } catch { 'unknown' }
    New-ErpDiagnosticResult $category 'PowerShell' Info "$($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition)), running as $identity"
    if (-not (Test-ErpAdministrator)) {
        New-ErpDiagnosticResult $category 'Elevation' Warn 'This session is not elevated, so process, task and storage checks are limited.' 'Run again from an elevated PowerShell (or the configuration tool as Administrator).'
    }
    $mode = [string]$ExecutionContext.SessionState.LanguageMode
    if ($mode -ne 'FullLanguage') {
        New-ErpDiagnosticResult $category 'PowerShell language mode' Fail "Language mode is $mode." 'Constrained Language Mode (AppLocker or WDAC script rules) blocks the .NET calls the listener and uploader need. Allow-list the ERP Printer install folder.'
    } else {
        New-ErpDiagnosticResult $category 'PowerShell language mode' Pass 'FullLanguage'
    }
}

function Get-ErpStorageChecks {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Config)
    $category = 'Storage and logs'
    $dataRoot = [string]$Config.Global.DataRoot
    if (-not (Test-Path -LiteralPath $dataRoot)) {
        New-ErpDiagnosticResult $category 'Data folder' Fail "'$dataRoot' does not exist." 'Run the installer repair or Install-ErpPrinter; the data folder is created at install time.'
        return
    }
    $probe = Join-Path $dataRoot ('.write-test-' + [guid]::NewGuid().ToString('N'))
    try {
        [IO.File]::WriteAllText($probe, 'x')
        Remove-Item -LiteralPath $probe -Force
        New-ErpDiagnosticResult $category 'Data folder' Pass "'$dataRoot' is writable by this session."
    } catch {
        $status = if (Test-ErpAdministrator) { 'Fail' } else { 'Warn' }
        New-ErpDiagnosticResult $category 'Data folder' $status "'$dataRoot' is not writable by this session: $($_.Exception.Message)" 'The folder is limited to SYSTEM and Administrators. If this session is elevated, check the folder ACL (icacls) and antivirus exclusions.'
    }
    try {
        $drive = New-Object IO.DriveInfo ([IO.Path]::GetPathRoot($dataRoot))
        $freeMb = [Math]::Round($drive.AvailableFreeSpace / 1MB)
        $status = if ($freeMb -lt 200) { 'Fail' } elseif ($freeMb -lt 1024) { 'Warn' } else { 'Pass' }
        New-ErpDiagnosticResult $category 'Free disk space' $status "$freeMb MB free on $($drive.Name)" $(if ($status -ne 'Pass') { 'Print jobs are spooled to disk before upload; free some space.' })
    } catch { New-ErpDiagnosticResult $category 'Free disk space' Info "Not checked ($($_.Exception.Message))" }

    $level = [string]$Config.Global.LogLevel
    if ($level -in 'Error', 'Warning') {
        New-ErpDiagnosticResult $category 'Log level' Warn "LogLevel is $level, which hides the 'queued' and 'delivered' messages." 'Set LogLevel to Information (or Verbose while troubleshooting) in the Settings tab.'
    } else {
        New-ErpDiagnosticResult $category 'Log level' Pass $level
    }

    $logDir = Join-Path $dataRoot 'logs'
    $files = @(Get-ChildItem -LiteralPath $logDir -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
    if (-not (Test-ErpFolderReadable $dataRoot)) {
        New-ErpDiagnosticResult $category 'Log files' Warn "'$logDir' cannot be read from this session." 'Run elevated to read the logs.'
    } elseif (-not $files.Count) {
        New-ErpDiagnosticResult $category 'Log files' Warn "No log files in '$logDir'." 'Neither the listener nor the uploader has ever written a line here. Check that the scheduled tasks run (see Background tasks) and that SYSTEM can write to this folder.'
    } else {
        $latest = ($files | Select-Object -First 4 | ForEach-Object { '{0} ({1:yyyy-MM-dd HH:mm:ss}, {2} KB)' -f $_.Name, $_.LastWriteTime, [Math]::Max(1, [Math]::Round($_.Length / 1KB)) }) -join '; '
        New-ErpDiagnosticResult $category 'Log files' Info "In '$logDir': $latest"
    }
}

#endregion
#region Background tasks ----------------------------------------------------------------

function Get-ErpTaskChecks {
    [CmdletBinding()]
    param()
    $category = 'Background tasks'
    foreach ($definition in $script:ErpTaskDefinitions | Where-Object { $_.Name -in 'Listener', 'Uploader' }) {
        $name = $definition.Name
        $task = Get-ScheduledTask -TaskPath $script:ErpTaskPath -TaskName $name -ErrorAction SilentlyContinue
        if (-not $task) {
            New-ErpDiagnosticResult $category "$name task" Fail 'The scheduled task is not installed.' 'Run the installer repair, or: Install-ErpPrinter -InstallDir <folder>.'
            continue
        }
        $info = Get-ScheduledTaskInfo -InputObject $task -ErrorAction SilentlyContinue
        $result = if ($info) { ConvertTo-ErpTaskResultText -Code ([int64]$info.LastTaskResult) } else { 'unknown' }
        $lastRun = if ($info -and $info.LastRunTime -and $info.LastRunTime.Year -gt 1999) { $info.LastRunTime.ToString('yyyy-MM-dd HH:mm:ss') } else { 'never' }
        $detail = "State $($task.State); last run $lastRun; last result $result."
        $action = $task.Actions | Select-Object -First 1
        $script = if ($action -and $action.Arguments -match '-File\s+"([^"]+)"') { $Matches[1] } else { $null }
        if ($script -and -not (Test-Path -LiteralPath $script)) {
            New-ErpDiagnosticResult $category "$name task" Fail "$detail It points at '$script', which does not exist." 'The install folder moved or was removed. Repair the installation.'
        } elseif ([string]$task.State -eq 'Disabled') {
            New-ErpDiagnosticResult $category "$name task" Fail "$detail" "Enable it: Enable-ScheduledTask -TaskPath '$script:ErpTaskPath' -TaskName $name"
        } elseif ([string]$task.State -ne 'Running') {
            New-ErpDiagnosticResult $category "$name task" Fail "$detail It should always be running." "Start it with Restart-ErpPrinterService. If it stops again, the newest cli-*.log and $($name.ToLower())-*.log files in the log folder say why."
        } else {
            New-ErpDiagnosticResult $category "$name task" Pass $detail
        }

        $command = $definition.Command
        $processes = Get-ErpTaskProcesses -Command $command
        if ($null -eq $processes) {
            New-ErpDiagnosticResult $category "$name process" Info 'Process command lines are not readable from this session (run elevated to check).'
        } elseif ($processes.Count -eq 0) {
            $status = if ([string]$task.State -eq 'Running') { 'Warn' } else { 'Fail' }
            New-ErpDiagnosticResult $category "$name process" $status "No powershell.exe is running ErpPrinter.ps1 -Command $command." 'Restart-ErpPrinterService; check the log folder for a crash message.'
        } elseif ($processes.Count -gt 1) {
            $ids = ($processes | ForEach-Object { $_.ProcessId }) -join ', '
            New-ErpDiagnosticResult $category "$name process" Warn "$($processes.Count) processes are running it (PIDs $ids)." 'Extra copies compete for the same pipe or outbox. Restart-ErpPrinterService stops them all.'
        } else {
            New-ErpDiagnosticResult $category "$name process" Pass "Running as PID $($processes[0].ProcessId), started $($processes[0].CreationDate.ToString('yyyy-MM-dd HH:mm:ss'))."
        }
    }
}

function Get-ErpLogHistoryChecks {
    <# What the service logs say about the last day: restarts, crashes, jobs, errors. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Config)
    $category = 'Service history (24 h)'
    $logDir = Join-Path $Config.Global.DataRoot 'logs'
    if (-not (Test-ErpFolderReadable $Config.Global.DataRoot)) {
        New-ErpDiagnosticResult $category 'Service logs' Warn 'The logs cannot be read from this session, so what the service did in the last day is unknown.' 'Run elevated.'
        return
    }
    $entries = @(Get-ErpRecentLogEntries -LogDir $logDir -Hours 24)
    $listener = @($entries | Where-Object { $_.Component -eq 'listener' })
    $uploader = @($entries | Where-Object { $_.Component -eq 'uploader' })

    $starts = @($listener | Where-Object { $_.Message -like 'Listener started*' })
    if (-not $starts.Count) {
        $why = if ($Config.Global.LogLevel -in 'Error', 'Warning') { 'LogLevel hides Information messages.' } else { 'The listener has not started, or cannot write its log.' }
        New-ErpDiagnosticResult $category 'Listener start-up' Warn "No 'Listener started' line in the last 24 hours. $why" 'See Background tasks above, and the Application event log (source ErpPrinter).'
    } else {
        $lastStart = $starts[-1]
        $opened = @($listener | Where-Object { $_.Time -ge $lastStart.Time -and $_.Message -like 'Waiting for print jobs*' })
        if ($starts.Count -ge 5) {
            New-ErpDiagnosticResult $category 'Listener restarts' Warn "The listener started $($starts.Count) times in 24 hours (latest $($lastStart.Time.ToString('HH:mm:ss')))." 'It is probably crashing; look for a crash or error line next to the start lines in the listener log.'
        }
        if (-not $opened.Count) {
            New-ErpDiagnosticResult $category 'Listener pipes' Fail "The listener started at $($lastStart.Time.ToString('HH:mm:ss')) but never reported a pipe ready." 'Either no profile is enabled or opening the pipe fails; the errors below say which.'
        } else {
            New-ErpDiagnosticResult $category 'Listener pipes' Pass "$($opened.Count) pipe(s) opened after the last start: $(($opened | ForEach-Object { $_.Message -replace '^Waiting for print jobs for ', '' } | Select-Object -First 3) -join ' | ')"
        }
    }

    $connections = @($listener | Where-Object { $_.Message -like 'The spooler connected*' })
    $queued = @($listener | Where-Object { $_.Message -like 'Queued *' })
    $delivered = @($uploader | Where-Object { $_.Message -like 'Delivered *' })
    if ($connections.Count -eq 0) {
        New-ErpDiagnosticResult $category 'Spooler connections' Info 'The spooler has not connected to a pipe in the last 24 hours.' 'If you printed in that time, the job never left the spooler: run Invoke-ErpPrinterTestPrint to see where it stops.'
    } else {
        New-ErpDiagnosticResult $category 'Spooler connections' Info "$($connections.Count) connection(s), $($queued.Count) job(s) queued, $($delivered.Count) delivered; latest connection $($connections[-1].Time.ToString('yyyy-MM-dd HH:mm:ss'))."
    }

    $problems = @($entries | Where-Object { $_.Level -in 'Error', 'Warning' })
    if ($problems.Count) {
        $recent = ($problems | Select-Object -Last 6 | ForEach-Object { '{0:HH:mm:ss} {1} [{2}] {3}' -f $_.Time, $_.Level, $_.Component, (Limit-ErpString $_.Message 300) }) -join [Environment]::NewLine
        $hasError = [bool]@($problems | Where-Object Level -eq 'Error').Count
        New-ErpDiagnosticResult $category 'Logged problems' $(if ($hasError) { 'Fail' } else { 'Warn' }) "$($problems.Count) warning/error line(s); the latest:$([Environment]::NewLine)$recent"
    } else {
        New-ErpDiagnosticResult $category 'Logged problems' Pass 'No warnings or errors in the last 24 hours.'
    }
}

#endregion
#region Windows printing ----------------------------------------------------------------

function Get-ErpSpoolerChecks {
    [CmdletBinding()]
    param()
    $category = 'Windows printing'
    $service = Get-Service -Name Spooler -ErrorAction SilentlyContinue
    if (-not $service) {
        New-ErpDiagnosticResult $category 'Print Spooler service' Fail 'The Print Spooler service does not exist.'
    } elseif ($service.Status -ne 'Running') {
        New-ErpDiagnosticResult $category 'Print Spooler service' Fail "Status $($service.Status), start type $($service.StartType)." 'Start-Service Spooler (and set it to Automatic).'
    } else {
        New-ErpDiagnosticResult $category 'Print Spooler service' Pass 'Running'
    }

    $driver = Get-PrinterDriver -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq $script:ErpPdfDriver }
    if ($driver) {
        New-ErpDiagnosticResult $category 'PDF driver' Pass "'$script:ErpPdfDriver' is installed."
    } else {
        New-ErpDiagnosticResult $category 'PDF driver' Fail "'$script:ErpPdfDriver' is not installed, so jobs cannot be rendered to PDF." "Repair printers in the Status tab, or enable the 'Microsoft Print to PDF' Windows feature (Printing-PrintToPDFServices-Features)."
    }

    # Windows protected print mode removes third-party port monitors and drivers.
    $wpp = $null
    foreach ($path in 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Printers\WPP', 'HKLM:\SYSTEM\CurrentControlSet\Control\Print\WPP') {
        $values = Get-ErpRegistryValues -Path $path
        foreach ($name in 'EnableWindowsProtectedPrint', 'WindowsProtectedPrintMode') {
            if ($values.ContainsKey($name) -and $null -eq $wpp) { $wpp = [int]$values[$name] }
        }
    }
    if ($wpp -eq 1) {
        New-ErpDiagnosticResult $category 'Windows protected print mode' Fail 'Windows protected print mode is on.' 'It blocks local ports and classic drivers, so ERP Printer queues cannot print. Turn the policy off for these machines.'
    } else {
        New-ErpDiagnosticResult $category 'Windows protected print mode' Pass 'Not enabled.'
    }

    $log = Get-WinEvent -ListLog 'Microsoft-Windows-PrintService/Operational' -ErrorAction SilentlyContinue
    if (-not $log) {
        New-ErpDiagnosticResult $category 'PrintService Operational log' Info 'The log is not present on this system.'
    } elseif (-not $log.IsEnabled) {
        New-ErpDiagnosticResult $category 'PrintService Operational log' Warn 'The log is disabled, so the spooler leaves no record of what happened to a job.' 'wevtutil sl Microsoft-Windows-PrintService/Operational /e:true   (Install-ErpPrinter normally does this; a policy may have switched it back.)'
    } else {
        New-ErpDiagnosticResult $category 'PrintService Operational log' Pass 'Enabled.'
    }

    $since = (Get-Date).AddHours(-24)
    foreach ($logName in 'Admin', 'Operational') {
        $events = @(Get-ErpPrintServiceEvents -Log $logName -Since $since -Level 1, 2, 3 -Max 8)
        if ($events.Count) {
            $lines = ($events | Select-Object -First 5 | ForEach-Object { Format-ErpEventLine $_ }) -join [Environment]::NewLine
            New-ErpDiagnosticResult $category "Spooler $logName log" Warn "$($events.Count) warning/error event(s) in the last 24 hours:$([Environment]::NewLine)$lines" 'Look for your printer or port name in these messages: they are the spooler explaining why a job failed.'
        } else {
            New-ErpDiagnosticResult $category "Spooler $logName log" Pass 'No warnings or errors in the last 24 hours.'
        }
    }
}

function Get-ErpProfileChecks {
    <# Printer, port, queue and outbox state for one profile. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Config, [Parameter(Mandatory)] [System.Collections.IDictionary] $PrinterProfile)
    $category = "Profile '$($PrinterProfile.Name)'"
    if (-not $PrinterProfile.Enabled) {
        New-ErpDiagnosticResult $category 'Enabled' Warn 'The profile is disabled: it has no printer and nothing is delivered.' 'Tick Enabled and save.'
        return
    }
    $expectedPort = Get-ErpPortName $PrinterProfile.PipeName
    $printer = Get-Printer -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq $PrinterProfile.PrinterName } | Select-Object -First 1
    if (-not $printer) {
        New-ErpDiagnosticResult $category 'Windows printer' Fail "No printer named '$($PrinterProfile.PrinterName)' exists." 'Click Repair printers (Status tab) or run Sync-ErpPrinterQueue.'
    } else {
        $problems = New-Object System.Collections.Generic.List[string]
        if ($printer.PortName -ne $expectedPort) { $problems.Add("it is on port '$($printer.PortName)' instead of '$expectedPort', so jobs never reach the listener") }
        if ($printer.DriverName -ne $script:ErpPdfDriver) { $problems.Add("it uses driver '$($printer.DriverName)' instead of '$script:ErpPdfDriver', so the output is not a PDF") }
        if ($problems.Count) {
            New-ErpDiagnosticResult $category 'Windows printer' Fail "'$($printer.Name)': $($problems -join '; ')." 'Click Repair printers (Status tab) or run Sync-ErpPrinterQueue.'
        } else {
            New-ErpDiagnosticResult $category 'Windows printer' Pass "'$($printer.Name)' uses driver '$($printer.DriverName)' on port '$($printer.PortName)'."
        }
        $state = Get-ErpPrinterStateInfo -Printer $printer
        if ($state.Problem) {
            New-ErpDiagnosticResult $category 'Printer state' Fail $state.Problem $state.Hint
        } else {
            New-ErpDiagnosticResult $category 'Printer state' Pass $state.Summary
        }
        $port = Get-PrinterPort -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq $expectedPort } | Select-Object -First 1
        if (-not $port) {
            New-ErpDiagnosticResult $category 'Printer port' Fail "The port '$expectedPort' does not exist." 'Click Repair printers (Status tab); if that fails, restart the Print Spooler service.'
        } else {
            New-ErpDiagnosticResult $category 'Printer port' Pass "'$expectedPort' exists."
        }

        $jobs = @(Get-PrintJob -PrinterName $printer.Name -ErrorAction SilentlyContinue)
        if (-not $jobs.Count) {
            New-ErpDiagnosticResult $category 'Print queue' Pass 'Empty.'
        } else {
            $lines = ($jobs | Select-Object -First 5 | ForEach-Object { "job $($_.Id) '$($_.DocumentName)' by $($_.UserName): $($_.JobStatus), submitted $($_.SubmittedTime)" }) -join [Environment]::NewLine
            $stuck = @($jobs | Where-Object { Test-ErpBadJobStatus ([string]$_.JobStatus) -or ($_.SubmittedTime -and $_.SubmittedTime -lt (Get-Date).AddMinutes(-2)) })
            if ($stuck.Count) {
                New-ErpDiagnosticResult $category 'Print queue' Fail "$($jobs.Count) job(s) are sitting in the queue instead of reaching the listener:$([Environment]::NewLine)$lines" 'A job that stays here means the spooler cannot write to the pipe (listener not running, pipe missing or wrong permissions) or the printer is offline. Fix the cause, then delete the stuck jobs.'
            } else {
                New-ErpDiagnosticResult $category 'Print queue' Info "$($jobs.Count) job(s) in progress:$([Environment]::NewLine)$lines"
            }
        }
    }

    $pipe = Test-ErpPipeExists -PipeName $PrinterProfile.PipeName
    if ($null -eq $pipe) {
        New-ErpDiagnosticResult $category 'Pipe' Info "Could not tell whether '$expectedPort' exists."
    } elseif ($pipe) {
        New-ErpDiagnosticResult $category 'Pipe' Pass "'$expectedPort' is being hosted, so the listener is ready to receive."
    } else {
        New-ErpDiagnosticResult $category 'Pipe' Fail "Nothing is hosting '$expectedPort'. Print jobs sent now would fail or stall in the spooler." 'The listener is not running, has not opened this pipe yet, or crashed. See Background tasks and Service history.'
    }

    if (-not (Test-ErpFolderReadable $Config.Global.DataRoot)) {
        New-ErpDiagnosticResult $category 'Documents' Warn 'The outbox cannot be read from this session.' 'Run elevated.'
        return
    }
    $paths = Get-ErpProfilePaths -DataRoot $Config.Global.DataRoot -ProfileName $PrinterProfile.Name
    $pending = @(Get-ErpOutboxItems -Folder $paths.Outbox)
    $failed = @(Get-ErpOutboxItems -Folder $paths.Failed)
    $stats = Get-ErpDeliveryStats -Paths $paths
    $lastSent = if ($stats.lastSentAt) { (ConvertTo-ErpDateTime $stats.lastSentAt).ToString('yyyy-MM-dd HH:mm:ss') } else { 'never' }
    New-ErpDiagnosticResult $category 'Documents' Info "$($pending.Count) waiting, $($failed.Count) failed, $([int]$stats.count) delivered (last: $lastSent)."
    $errored = @($pending + $failed | Where-Object { $_.Meta.lastError })
    if ($errored.Count) {
        $latest = $errored | Sort-Object { $_.Meta.lastAttempt } | Select-Object -Last 1
        New-ErpDiagnosticResult $category 'Delivery error' Fail "'$($latest.Meta.title)': $($latest.Meta.lastError)" 'Fix the cause shown (credentials, URL, model/field names), then use "Retry failed" and "Send now" in the Status tab.'
    } elseif ($pending.Count) {
        $oldest = $pending | ForEach-Object { ConvertTo-ErpDateTime $_.Meta.printed } | Sort-Object | Select-Object -First 1
        if ($oldest -lt (Get-Date).AddMinutes(-5)) {
            New-ErpDiagnosticResult $category 'Delivery' Warn "The oldest waiting document was printed $($oldest.ToString('HH:mm:ss')) and has no error recorded." 'The uploader is probably not running or cannot start; see Background tasks.'
        }
    }
}

#endregion
#region ERP connection ------------------------------------------------------------------

function Get-ErpConnectionChecks {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Config, [Parameter(Mandatory)] [System.Collections.IDictionary] $PrinterProfile)
    $category = "Profile '$($PrinterProfile.Name)'"
    $proxy = [string]$Config.Global.ProxyUrl
    $userProxy = $null
    try {
        $settings = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction Stop
        if ($settings.ProxyEnable -eq 1 -and $settings.ProxyServer) { $userProxy = [string]$settings.ProxyServer }
    } catch { Write-Verbose "No per-user proxy settings: $_" }
    if ($userProxy -and -not $proxy) {
        New-ErpDiagnosticResult $category 'Proxy' Warn "This user reaches the web through proxy '$userProxy', but the ProxyUrl setting is empty. The service runs as SYSTEM and does not see per-user proxy settings." 'Set ProxyUrl (Settings tab) if the ERP is only reachable through that proxy. A passing connection test from this session does not prove the service can connect.'
    } else {
        New-ErpDiagnosticResult $category 'Proxy' Info $(if ($proxy) { "ProxyUrl is $proxy." } else { 'No proxy configured.' })
    }
    $result = Test-ErpPrinterConnection -PrinterProfile $PrinterProfile
    if ($result.Success) {
        New-ErpDiagnosticResult $category 'ERP connection' Pass (($result.Message -split '\r?\n' | Select-Object -First 2) -join ' ')
    } else {
        New-ErpDiagnosticResult $category 'ERP connection' Fail $result.Message 'Check the server URL, database, user and API key. The uploader runs as SYSTEM, so also check the machine can reach the server without a user-level proxy or VPN.'
    }
}

#endregion
#region Report --------------------------------------------------------------------------

function Format-ErpDiagnosticReport {
    <# Renders results as plain text that can be pasted into a ticket. #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)] [object[]] $Results, [string] $Title = 'ERP Printer diagnostics')
    $newline = [Environment]::NewLine
    $builder = New-Object System.Text.StringBuilder
    $null = $builder.AppendLine($Title)
    $null = $builder.AppendLine(('Generated {0:yyyy-MM-dd HH:mm:ss} on {1}' -f (Get-Date), $env:COMPUTERNAME))
    $counts = @('Fail', 'Warn', 'Pass', 'Info') | ForEach-Object { '{0} {1}' -f @($Results | Where-Object Status -eq $_).Count, $_.ToLower() }
    $null = $builder.AppendLine(($counts -join ', '))
    $first = $Results | Where-Object Status -eq 'Fail' | Select-Object -First 1
    if ($first) {
        $null = $builder.AppendLine()
        $null = $builder.AppendLine("Start with: $($first.Category) / $($first.Name)")
        if ($first.Hint) { $null = $builder.AppendLine("  $($first.Hint)") }
    }
    $label = @{ Pass = 'PASS'; Info = 'INFO'; Warn = 'WARN'; Fail = 'FAIL' }
    foreach ($group in $Results | Group-Object Category) {
        $null = $builder.AppendLine()
        $null = $builder.AppendLine("== $($group.Name) ==")
        foreach ($r in $group.Group) {
            $lines = @(("$($r.Detail)" -split '\r?\n'))
            $null = $builder.AppendLine("[$($label[$r.Status])] $($r.Name): $($lines[0])")
            foreach ($line in $lines | Select-Object -Skip 1) { $null = $builder.AppendLine("       $line") }
            if ($r.Hint -and $r.Status -in 'Fail', 'Warn') { $null = $builder.AppendLine("       -> $($r.Hint)") }
        }
    }
    $builder.ToString().TrimEnd() + $newline
}

#endregion
#region Test print verdict --------------------------------------------------------------

function Get-ErpTestPrintVerdict {
    <#
    Turns the evidence gathered while a test page travelled through the pipeline into a stage
    reached, a one-sentence summary and a next step. Kept free of side effects so the decision
    table can be tested.

    Stages, in order: None, Submitted, InQueue, Spooled, Received, Delivered.
    Evidence keys: Submitted, JobSeen, LastJobStatus, JobStuck, JobGone, SpoolerPrinted,
    SpoolerPort, ExpectedPort, PipeExists, ListenerConnected, ListenerError, Received,
    LastDeliveryError, Failed, FailedError, Delivered, JobEverPrinting, JobsAhead, JobsAheadText,
    PrinterProblem, PrinterHint.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([Parameter(Mandatory)] [System.Collections.IDictionary] $Evidence)
    $e = $Evidence
    $has = { param($k) $e.Contains($k) -and $e[$k] }
    $make = {
        param($Stage, $Success, $Summary, $Hint)
        [pscustomobject]@{ Stage = $Stage; Success = $Success; Summary = $Summary; Hint = $Hint }
    }

    if (& $has 'Delivered') {
        return & $make 'Delivered' $true 'The test page went through the whole pipeline and reached the ERP.' ''
    }
    if (& $has 'Failed') {
        return & $make 'Received' $false "The listener received the page but the ERP rejected it: $($e.FailedError)" 'Fix the cause shown, then use "Retry failed" in the Status tab.'
    }
    if (& $has 'Received') {
        $lastError = if ($e.Contains('LastDeliveryError') -and $e.LastDeliveryError) { " The last delivery error was: $($e.LastDeliveryError)" } else { '' }
        return & $make 'Received' $false "The listener received the page and queued it, but it has not been delivered to the ERP yet.$lastError" 'The printing side works. Check that the Uploader task is running and that this computer can reach the ERP as SYSTEM (proxy, firewall); the Status tab shows the error and the retry time.'
    }
    if (& $has 'ListenerError') {
        return & $make 'Spooled' $false "The spooler reached the listener, but the listener could not take the page: $($e.ListenerError)" 'This is a problem inside ERP Printer on this machine (empty data, wrong driver, or a file error in the data folder). The listener log has the details.'
    }
    if (-not (& $has 'Submitted')) {
        return & $make 'None' $false 'The test page could not be sent to the Windows printer.' 'The printer does not exist or cannot be opened from this session. Use Repair printers.'
    }
    if (& $has 'JobStuck') {
        $port = if ($e.Contains('ExpectedPort')) { $e.ExpectedPort } else { 'the pipe' }
        $pipeNote = if ($e.Contains('PipeExists') -and $e.PipeExists -eq $false) { " Nothing is hosting ${port}: the listener is not running or has not opened it." } else { '' }
        $neverPrinting = $e.Contains('JobEverPrinting') -and -not $e.JobEverPrinting
        $why = ''
        if ($e.Contains('PrinterProblem') -and $e.PrinterProblem) { $why += " The printer is not ready: $($e.PrinterProblem)" }
        $ahead = if ($e.Contains('JobsAhead')) { [int]$e.JobsAhead } else { 0 }
        if ($ahead -gt 0) {
            $why += " $ahead job(s) were already in the queue ($($e.JobsAheadText)); the spooler prints one job at a time per queue, so an older stuck job blocks this page."
        }
        if ($neverPrinting -and -not $why) { $why = " The job never reached 'Printing', so the spooler never tried to open the pipe." }
        $hint = 'Make sure the Listener task is running and the pipe is visible, that the printer is not set offline, and read the Print Spooler Admin log for the exact port error. Then delete the stuck job and try again.'
        if ($e.Contains('PrinterProblem') -and $e.PrinterProblem) { $hint = $e.PrinterHint }
        elseif ($ahead -gt 0) { $hint = 'Delete the older job(s) (Get-PrintJob -PrinterName <printer> | Remove-PrintJob) and print again. If they will not delete or come back, restart the Print Spooler service (Restart-Service Spooler).' }
        elseif ($neverPrinting) { $hint = "Nothing in the printer state or queue explains it, and the pipe is not involved yet. Restart the Print Spooler service (Restart-Service Spooler) and try again. If the job still sits at '$($e.LastJobStatus)', check Event Viewer (PrintService Admin, and System for Print Spooler faults) and whether 'Microsoft Print to PDF' prints to a file on this machine." }
        return & $make 'InQueue' $false "The spooler accepted the page but never handed it to the printer port; the job is stuck as '$($e.LastJobStatus)'.$why$pipeNote" $hint
    }
    if (& $has 'SpoolerPrinted') {
        $port = if ($e.Contains('SpoolerPort') -and $e.SpoolerPort) { " to '$($e.SpoolerPort)'" } else { '' }
        $mismatch = ''
        if ($e.Contains('SpoolerPort') -and $e.SpoolerPort -and $e.Contains('ExpectedPort') -and $e.ExpectedPort -and ($e.SpoolerPort -notlike "*$($e.ExpectedPort)*")) {
            $mismatch = " That is not the listener's pipe ($($e.ExpectedPort))."
        }
        $pipeNote = if ($e.Contains('PipeExists') -and $e.PipeExists -eq $false) { ' No process is hosting the pipe, so the data had nowhere to go.' } else { '' }
        if ($e.Contains('ListenerConnected') -and $e.ListenerConnected -eq $false) { $pipeNote += ' The listener log shows no connection from the spooler.' }
        return & $make 'Spooled' $false "The spooler reports the job as printed$port, but the listener never received it.$mismatch$pipeNote" 'Run Repair printers so the queue uses the right port, make sure only one Listener process runs, and check the listener log for a connection line ("The spooler connected to pipe"). If there is none, the data went somewhere else.'
    }
    if (& $has 'JobGone') {
        return & $make 'Spooled' $false 'The job left the print queue, but the listener never received it and the spooler did not log what it did.' 'Enable the PrintService Operational log (wevtutil sl Microsoft-Windows-PrintService/Operational /e:true) and repeat; event 307 shows the port the spooler used.'
    }
    if (& $has 'JobSeen') {
        return & $make 'InQueue' $false "The job is in the print queue (status '$($e.LastJobStatus)') but is not moving." 'The spooler is waiting on something; check the printer state, the Spooler service and the Admin log.'
    }
    & $make 'Submitted' $false 'The test page was sent to the spooler but never appeared in the print queue.' 'Nothing reached the Windows spooler for this printer. Check the Print Spooler service, and run the app that printed as an elevated user.'
}

#endregion
