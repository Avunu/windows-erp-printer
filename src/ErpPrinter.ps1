<#
.SYNOPSIS
    ERP Printer command-line entry point, used by the scheduled tasks, the MSI and administrators.
.DESCRIPTION
    Commands:
      Listen        Run the pipe listener (scheduled task).
      Upload        Run the uploader loop (scheduled task). Add -Once for a single pass.
      Update        Check for and install updates (scheduled task). Add -CheckOnly or -Force.
      Install       Provision printers, folders and tasks (MSI). Accepts Name=Value arguments.
      Uninstall     Remove printers and tasks (MSI). "Purge=1" also removes config and data.
      Sync          Recreate printer queues from the current profiles.
      Status        Show tasks, printers and outbox counts.
      Test          Test the connection of one profile: -ProfileName <name>.
      Diagnose      Check every link from the printer to the ERP and save a report. Run elevated.
                    Add -ProfileName to limit it to one profile, -SkipConnection to stay offline.
      TestPrint     Print a test page and follow it to the ERP: -ProfileName <name>.
      Retry         Move failed documents back to the outbox.
      Config        Open the configuration GUI.

    Whatever goes wrong, the failure is also written to <data folder>\logs\cli-yyyyMMdd.log.
.EXAMPLE
    & 'C:\Program Files\ERP Printer\ErpPrinter.ps1' -Command Status
.EXAMPLE
    & 'C:\Program Files\ERP Printer\ErpPrinter.ps1' -Command Test -ProfileName 'Send to Odoo'
.EXAMPLE
    & 'C:\Program Files\ERP Printer\ErpPrinter.ps1' -Command Diagnose
.EXAMPLE
    & 'C:\Program Files\ERP Printer\ErpPrinter.ps1' -Command TestPrint -ProfileName 'Send to Odoo'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('Listen', 'Upload', 'Update', 'Install', 'Uninstall', 'Sync', 'Status', 'Test', 'Diagnose', 'TestPrint', 'Retry', 'Config')]
    [string] $Command,
    [Alias('Profile')] [string] $ProfileName,
    [switch] $Once,
    [switch] $CheckOnly,
    [switch] $Force,
    [switch] $SkipConnection,
    [Parameter(ValueFromRemainingArguments)] [string[]] $Arguments
)
$ErrorActionPreference = 'Stop'

# The module's own logging is not available until it has loaded, and a scheduled task has no
# console, so a failure here (a blocked module, a bad registry value, an exception thrown out of
# a service loop) would otherwise vanish. This writes to the default log folder, or to %TEMP%.
function Write-BootstrapLog {
    param([string] $Level, [string] $Message)
    $line = '{0:yyyy-MM-dd HH:mm:ss.fff} [{1,-11}] [cli] [{2}, PID {3}] {4}' -f (Get-Date), $Level, $Command, $PID, $Message
    $folders = @()
    try { $folders += Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'ErpPrinter\logs' } catch { $null = $_ }
    $folders += [IO.Path]::GetTempPath()
    foreach ($folder in $folders) {
        try {
            if (-not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }
            [IO.File]::AppendAllText((Join-Path $folder ('cli-{0:yyyyMMdd}.log' -f (Get-Date))), $line + [Environment]::NewLine, [Text.Encoding]::UTF8)
            return
        } catch { $null = $_ }
    }
}

if ($Command -in 'Listen', 'Upload', 'Update') {
    Write-BootstrapLog -Level Information -Message "Starting '$Command' as $([Security.Principal.WindowsIdentity]::GetCurrent().Name) (PowerShell $($PSVersionTable.PSVersion), script $PSCommandPath)."
}

try {
    Import-Module (Join-Path $PSScriptRoot 'Modules\ErpPrinter\ErpPrinter.psd1') -Force

    # msiexec passes installer properties as Name=Value pairs because Windows PowerShell's
    # -File drops empty quoted arguments.
    $named = @{}
    foreach ($arg in @($Arguments)) {
        if ($arg -match '^(?<k>[A-Za-z]+)=(?<v>.*)$') { $named[$Matches.k] = $Matches.v }
    }

    switch ($Command) {
        'Listen' { Start-ErpPrinterListener }
        'Upload' {
            if ($Once) { Invoke-ErpPrinterUpload -Force:$Force | Format-Table -AutoSize | Out-String | Write-Output }
            else { Start-ErpPrinterUploader }
        }
        'Update' { Invoke-ErpPrinterUpdate -CheckOnly:$CheckOnly -Force:$Force | Format-List | Out-String | Write-Output }
        'Install' {
            $params = @{ InstallDir = $PSScriptRoot }
            foreach ($key in 'ProfileName', 'Backend', 'ServerUrl', 'Database', 'Username', 'ApiKey', 'ApiSecret', 'AutoUpdate', 'UpdateManifestUrl') {
                if ($named.ContainsKey($key) -and $named[$key] -ne '') { $params[$key] = $named[$key] }
            }
            Install-ErpPrinter @params -Confirm:$false
        }
        'Uninstall' { Uninstall-ErpPrinter -Purge:($named['Purge'] -eq '1') -Confirm:$false }
        'Sync' { Sync-ErpPrinterQueue -Confirm:$false }
        'Status' {
            $status = Get-ErpPrinterStatus
            $status | Select-Object Version, Listener, Uploader, Updater, DataRoot | Format-List | Out-String | Write-Output
            $status.Profiles | Format-Table Profile, Enabled, Backend, Printer, PrinterOk, Pending, Failed, Sent, LastSent, LastRemoteId -AutoSize | Out-String | Write-Output
            $status.Profiles | Where-Object LastError | ForEach-Object { Write-Output "[$($_.Profile)] last error: $($_.LastError)" }
        }
        'Test' {
            if (-not $ProfileName) { throw '-ProfileName is required.' }
            $result = Test-ErpPrinterConnection -ProfileName $ProfileName
            Write-Output $result.Message
            if (-not $result.Success) { exit 2 }
        }
        'Diagnose' {
            $diagnostics = Invoke-ErpPrinterDiagnostics -ProfileName $ProfileName -SkipConnection:$SkipConnection
            Write-Output $diagnostics.Report
            if ($diagnostics.ReportPath) { Write-Output "Report saved to $($diagnostics.ReportPath)" }
            if ($diagnostics.Failures) { exit 2 }
        }
        'TestPrint' {
            if (-not $ProfileName) { throw '-ProfileName is required.' }
            $result = Invoke-ErpPrinterTestPrint -ProfileName $ProfileName -OnProgress { param($message) Write-Information $message -InformationAction Continue }
            Write-Output $result.Report
            if (-not $result.Success) { exit 2 }
        }
        'Retry' { Write-Output "$(Restore-ErpPrinterFailedDocument -ProfileName $ProfileName -Confirm:$false) document(s) re-queued." }
        'Config' { & (Join-Path $PSScriptRoot 'ErpPrinterConfig.ps1') }
    }
} catch {
    $detail = "$($_.Exception.GetType().Name): $($_.Exception.Message)"
    $stack = "$($_.ScriptStackTrace)" -replace '\r?\n', ' | '
    Write-BootstrapLog -Level Error -Message "'$Command' failed. $detail Stack: $stack"
    Write-Error -ErrorRecord $_ -ErrorAction Continue
    exit 1
}
