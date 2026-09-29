# The listener and uploader run as SYSTEM scheduled tasks rather than services, because
# Windows PowerShell cannot host a real service without a wrapper. Each task starts at
# boot, and a 5-minute watchdog trigger (with MultipleInstances = IgnoreNew) restarts it
# if it ever exits.

$script:ErpTaskDefinitions = @(
    @{ Name = 'Listener'; Command = 'Listen'; Description = 'Receives print jobs from the ERP Printer queues over named pipes and writes them to the outbox.' }
    @{ Name = 'Uploader'; Command = 'Upload'; Description = 'Delivers documents from the ERP Printer outbox to the configured ERP, with retries.' }
    @{ Name = 'Updater';  Command = 'Update'; Description = 'Checks for and installs ERP Printer updates.' }
)

function Get-ErpPowerShellPath {
    Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
}

function Get-ErpTaskArguments {
    param([Parameter(Mandatory)] [string] $InstallDir, [Parameter(Mandatory)] [string] $Command)
    $entry = Join-Path $InstallDir 'ErpPrinter.ps1'
    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$entry`" -Command $Command"
}

function Register-ErpScheduledTasks {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)] [string] $InstallDir)
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    foreach ($definition in $script:ErpTaskDefinitions) {
        $arguments = Get-ErpTaskArguments -InstallDir $InstallDir -Command $definition.Command
        $existing = Get-ScheduledTask -TaskPath $script:ErpTaskPath -TaskName $definition.Name -ErrorAction SilentlyContinue
        if ($definition.Name -eq 'Updater' -and $existing -and $existing.Actions[0].Arguments -eq $arguments) {
            # Leave the updater alone: it may be the process running this very installation.
            continue
        }
        $action = New-ScheduledTaskAction -Execute (Get-ErpPowerShellPath) -Argument $arguments -WorkingDirectory $InstallDir
        if ($definition.Name -eq 'Updater') {
            $triggers = @(
                New-ScheduledTaskTrigger -Daily -At '03:00' -RandomDelay (New-TimeSpan -Hours 3)
            )
            $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
                -ExecutionTimeLimit (New-TimeSpan -Hours 1) -MultipleInstances IgnoreNew
        } else {
            $boot = New-ScheduledTaskTrigger -AtStartup
            $watchdog = New-ScheduledTaskTrigger -Once -At ((Get-Date).Date) -RepetitionInterval (New-TimeSpan -Minutes 5)
            $triggers = @($boot, $watchdog)
            $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
                -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew `
                -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1)
        }
        if ($PSCmdlet.ShouldProcess("$script:ErpTaskPath$($definition.Name)", 'Register scheduled task')) {
            Register-ScheduledTask -TaskPath $script:ErpTaskPath -TaskName $definition.Name -Action $action -Trigger $triggers `
                -Principal $principal -Settings $settings -Description $definition.Description -Force | Out-Null
        }
    }
}

function Unregister-ErpScheduledTasks {
    [CmdletBinding(SupportsShouldProcess)]
    param()
    foreach ($task in @(Get-ScheduledTask -TaskPath $script:ErpTaskPath -ErrorAction SilentlyContinue)) {
        if ($PSCmdlet.ShouldProcess($task.TaskName, 'Unregister scheduled task')) {
            Stop-ScheduledTask -InputObject $task -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -InputObject $task -Confirm:$false
        }
    }
    try {
        $service = New-Object -ComObject Schedule.Service
        $service.Connect()
        $service.GetFolder('\').DeleteFolder($script:ErpTaskPath.Trim('\'), 0)
    } catch { Write-Verbose "Task folder not removed: $_" }
}

function Get-ErpTaskState {
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)] [string] $Name)
    $task = Get-ScheduledTask -TaskPath $script:ErpTaskPath -TaskName $Name -ErrorAction SilentlyContinue
    if (-not $task) { return 'Not installed' }
    [string]$task.State
}
