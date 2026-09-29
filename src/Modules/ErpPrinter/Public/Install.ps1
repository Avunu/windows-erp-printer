function Install-ErpPrinter {
    <#
    .SYNOPSIS
        Provisions ERP Printer on this machine. Idempotent; the MSI runs it on install and upgrade.
    .DESCRIPTION
        Registers the event log source, creates the data folder and secrets key with locked-down
        ACLs, optionally seeds a printer profile, installs the PDF driver, creates printer queues
        for all enabled profiles, and registers and starts the scheduled tasks.
    .EXAMPLE
        Install-ErpPrinter -InstallDir 'C:\Program Files\ERP Printer' -ProfileName 'Send to Odoo' -Backend OdooJson2 -ServerUrl https://acme.odoo.com -ApiKey $key
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'Values arrive from msiexec properties as plain text.')]
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string] $InstallDir,
        [string] $ProfileName = 'Send to ERP',
        [string] $Backend,
        [string] $ServerUrl,
        [string] $Database,
        [string] $Username,
        [string] $ApiKey,
        [string] $ApiSecret,
        [string] $AutoUpdate,
        [string] $UpdateManifestUrl,
        [switch] $NoStart
    )
    Assert-ErpAdministrator
    if (-not $PSCmdlet.ShouldProcess($env:COMPUTERNAME, "Install $script:ErpProductName")) { return }
    Set-ErpLogContext -Component 'install'

    if (-not [Diagnostics.EventLog]::SourceExists($script:ErpEventSource)) {
        [Diagnostics.EventLog]::CreateEventSource($script:ErpEventSource, 'Application')
    }

    Set-ErpRegistryValue -Path $script:ErpRegistryRoot -Name 'InstallDir' -Value $InstallDir
    Set-ErpRegistryValue -Path $script:ErpRegistryRoot -Name 'InstalledVersion' -Value (Get-ErpPrinterVersion)
    Initialize-ErpSecretsKey

    if ($AutoUpdate -ne '') { Set-ErpPrinterSetting -Name AutoUpdate -Value $AutoUpdate }
    if ($UpdateManifestUrl) { Set-ErpPrinterSetting -Name UpdateManifestUrl -Value $UpdateManifestUrl }

    $config = Get-ErpPrinterConfig
    Set-ErpLogContext -Component 'install' -GlobalSettings $config.Global
    Initialize-ErpDataRoot -Path $config.Global.DataRoot

    if ($ServerUrl) {
        $settings = @{ ServerUrl = $ServerUrl }
        if ($Backend) { $settings['Backend'] = $Backend }
        if ($Database) { $settings['Database'] = $Database }
        if ($Username) { $settings['Username'] = $Username }
        if ($ApiKey) { $settings['ApiKey'] = $ApiKey }
        if ($ApiSecret) { $settings['ApiSecret'] = $ApiSecret }
        New-ErpPrinterProfile -Name $ProfileName -Settings $settings -Force
        Write-ErpLog -Message "Seeded printer profile '$ProfileName' ($($settings.Backend)) from installer properties."
    }

    try {
        & wevtutil.exe sl Microsoft-Windows-PrintService/Operational /e:true
    } catch { Write-ErpLog -Level Warning -Message "Could not enable the PrintService operational log: $_" }

    try {
        Install-ErpPdfDriver
        Sync-ErpPrinterQueue -NoRestart
    } catch {
        # Do not fail the whole installation; the GUI and Get-ErpPrinterStatus surface this.
        Write-ErpLog -Level Error -Message "Printer provisioning failed: $($_.Exception.Message)"
    }

    Register-ErpScheduledTasks -InstallDir $InstallDir
    if (-not $NoStart) { Restart-ErpPrinterService }
    Write-ErpLog -Message "$script:ErpProductName $(Get-ErpPrinterVersion) installed in $InstallDir."
}

function Initialize-ErpDataRoot {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)] [string] $Path)
    if (-not $PSCmdlet.ShouldProcess($Path, 'Create data folder')) { return }
    foreach ($sub in '', 'spool', 'logs', 'updates') {
        $dir = if ($sub) { Join-Path $Path $sub } else { $Path }
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    }
    # SYSTEM + Administrators only, by SID. Print jobs can contain sensitive documents.
    $output = & icacls.exe $Path /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' 2>&1
    if ($LASTEXITCODE -ne 0) { throw "icacls failed on ${Path}: $output" }
}

function Uninstall-ErpPrinter {
    <#
    .SYNOPSIS
        Removes the scheduled tasks and managed printers and ports.
    .PARAMETER Purge
        Also delete configuration, secrets and the data folder, including undelivered documents.
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param([switch] $Purge)
    Assert-ErpAdministrator
    if (-not $PSCmdlet.ShouldProcess($env:COMPUTERNAME, "Uninstall $script:ErpProductName")) { return }
    Set-ErpLogContext -Component 'install'
    $config = Get-ErpPrinterConfig

    Unregister-ErpScheduledTasks -Confirm:$false
    foreach ($entry in Get-ErpManagedPrinters) {
        try {
            if (Get-Printer | Where-Object { $_.Name -eq $entry.Printer }) { Remove-Printer -Name $entry.Printer }
            Remove-ErpPrinterPort -PortName $entry.Port -Confirm:$false
        } catch { Write-ErpLog -Level Warning -Message "Could not remove printer '$($entry.Printer)': $($_.Exception.Message)" }
    }
    foreach ($name in 'ManagedPrinters', 'InstalledVersion', 'InstallDir') {
        Remove-ErpRegistryValue -Path $script:ErpRegistryRoot -Name $name -Confirm:$false
    }

    $pending = @(Get-ChildItem -Path (Join-Path $config.Global.DataRoot 'spool') -Recurse -Filter '*.json' -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq 'outbox' })
    if ($Purge) {
        if ($pending.Count) { Write-ErpLog -Level Warning -Message "Purging $($pending.Count) undelivered document(s)." }
        Remove-Item -LiteralPath $config.Global.DataRoot -Recurse -Force -ErrorAction SilentlyContinue
        Remove-ErpRegistryKey -Path $script:ErpRegistryRoot -Confirm:$false
        if ([Diagnostics.EventLog]::SourceExists($script:ErpEventSource)) { [Diagnostics.EventLog]::DeleteEventSource($script:ErpEventSource) }
    } elseif ($pending.Count) {
        Write-ErpLog -Level Warning -Message "$($pending.Count) undelivered document(s) remain in $($config.Global.DataRoot)."
    }
}

function Get-ErpManagedPrinters {
    $values = Get-ErpRegistryValues -Path $script:ErpRegistryRoot
    foreach ($line in @($values['ManagedPrinters'])) {
        if ($line -and $line.Contains('|')) {
            $parts = $line.Split('|')
            [pscustomobject]@{ Printer = $parts[0]; Port = $parts[1] }
        }
    }
}

function Sync-ErpPrinterQueue {
    <#
    .SYNOPSIS
        Makes Windows printers match the enabled profiles: adds or repairs queues and removes
        queues this tool created for profiles that were deleted, disabled or renamed.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([switch] $NoRestart)
    Assert-ErpAdministrator
    $config = Get-ErpPrinterConfig
    $desired = @(foreach ($p in $config.Profiles | Where-Object { $_.Enabled }) {
            [pscustomobject]@{ Printer = $p.PrinterName; Port = (Get-ErpPortName $p.PipeName); Profile = $p.Name }
        })
    $previous = @(Get-ErpManagedPrinters)
    $managed = New-Object System.Collections.Generic.List[string]
    $errors = New-Object System.Collections.Generic.List[string]

    foreach ($entry in $desired) {
        try {
            Set-ErpPrinterQueue -PrinterName $entry.Printer -PortName $entry.Port -ProfileName $entry.Profile
            $managed.Add("$($entry.Printer)|$($entry.Port)")
        } catch {
            $errors.Add("$($entry.Printer): $($_.Exception.Message)")
        }
    }
    foreach ($old in $previous) {
        $stillWanted = $desired | Where-Object { $_.Printer -eq $old.Printer -and $_.Port -eq $old.Port }
        if ($stillWanted) { continue }
        $samePrinterNewPort = $desired | Where-Object { $_.Printer -eq $old.Printer }
        try {
            if (-not $samePrinterNewPort -and (Get-Printer | Where-Object { $_.Name -eq $old.Printer }) -and $PSCmdlet.ShouldProcess($old.Printer, 'Remove printer')) {
                Remove-Printer -Name $old.Printer
                Write-ErpLog -Message "Removed printer '$($old.Printer)'."
            }
            Remove-ErpPrinterPort -PortName $old.Port
        } catch {
            $errors.Add("$($old.Printer): $($_.Exception.Message)")
            $managed.Add("$($old.Printer)|$($old.Port)")
        }
    }
    if ($PSCmdlet.ShouldProcess('ManagedPrinters', 'Save managed printer list')) {
        Set-ErpRegistryValue -Path $script:ErpRegistryRoot -Name 'ManagedPrinters' -Value ([string[]]$managed.ToArray()) -Kind MultiString
    }
    if (-not $NoRestart -and (Get-ErpTaskState 'Listener') -eq 'Running') { Restart-ErpPrinterService -Component Listener }
    if ($errors.Count) { throw ("Some printers could not be provisioned:`n" + ($errors -join "`n")) }
}

function Restart-ErpPrinterService {
    <# Restarts the listener and/or uploader scheduled tasks. #>
    [CmdletBinding(SupportsShouldProcess)]
    param([ValidateSet('All', 'Listener', 'Uploader')] [string] $Component = 'All')
    $names = if ($Component -eq 'All') { 'Listener', 'Uploader' } else { @($Component) }
    foreach ($name in $names) {
        $task = Get-ScheduledTask -TaskPath $script:ErpTaskPath -TaskName $name -ErrorAction SilentlyContinue
        if (-not $task) { continue }
        if ($PSCmdlet.ShouldProcess($name, 'Restart scheduled task')) {
            Stop-ScheduledTask -InputObject $task -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 500
            Start-ScheduledTask -InputObject $task
        }
    }
}
