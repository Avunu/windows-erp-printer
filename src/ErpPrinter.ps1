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
      Retry         Move failed documents back to the outbox.
      Config        Open the configuration GUI.
.EXAMPLE
    & 'C:\Program Files\ERP Printer\ErpPrinter.ps1' -Command Status
.EXAMPLE
    & 'C:\Program Files\ERP Printer\ErpPrinter.ps1' -Command Test -ProfileName 'Send to Odoo'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('Listen', 'Upload', 'Update', 'Install', 'Uninstall', 'Sync', 'Status', 'Test', 'Retry', 'Config')]
    [string] $Command,
    [Alias('Profile')] [string] $ProfileName,
    [switch] $Once,
    [switch] $CheckOnly,
    [switch] $Force,
    [Parameter(ValueFromRemainingArguments)] [string[]] $Arguments
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Modules\ErpPrinter\ErpPrinter.psd1') -Force

# msiexec passes installer properties as Name=Value pairs because Windows PowerShell's
# -File drops empty quoted arguments.
$named = @{}
foreach ($arg in @($Arguments)) {
    if ($arg -match '^(?<k>[A-Za-z]+)=(?<v>.*)$') { $named[$Matches.k] = $Matches.v }
}

try {
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
            $status.Profiles | Format-Table Profile, Enabled, Backend, Printer, PrinterOk, Pending, Failed, Sent -AutoSize | Out-String | Write-Output
            $status.Profiles | Where-Object LastError | ForEach-Object { Write-Output "[$($_.Profile)] last error: $($_.LastError)" }
        }
        'Test' {
            if (-not $ProfileName) { throw '-ProfileName is required.' }
            $result = Test-ErpPrinterConnection -ProfileName $ProfileName
            Write-Output $result.Message
            if (-not $result.Success) { exit 2 }
        }
        'Retry' { Write-Output "$(Restore-ErpPrinterFailedDocument -ProfileName $ProfileName -Confirm:$false) document(s) re-queued." }
        'Config' { & (Join-Path $PSScriptRoot 'ErpPrinterConfig.ps1') }
    }
} catch {
    Write-Error -ErrorRecord $_ -ErrorAction Continue
    exit 1
}
