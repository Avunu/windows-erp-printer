function Invoke-ErpPrinterDiagnostics {
    <#
    .SYNOPSIS
        Checks every link between "Print" and the ERP and reports what is wrong.
    .DESCRIPTION
        Runs read-only checks on the machine: PowerShell environment, data folder and logs,
        the Listener and Uploader scheduled tasks and their processes, what the service logs
        say about the last 24 hours, the Print Spooler, the PDF driver, each profile's printer,
        port, queue and pipe, the outbox, and the ERP connection. The text report is saved to
        the log folder so it can be sent to whoever supports the machine.

        Run it elevated for the full picture. It does not print anything; use
        Invoke-ErpPrinterTestPrint to follow a real page through the pipeline.
    .PARAMETER ProfileName
        Limit the per-printer checks to one profile.
    .PARAMETER SkipConnection
        Do not contact the ERP.
    .PARAMETER NoSave
        Do not write the report to the log folder.
    .EXAMPLE
        (Invoke-ErpPrinterDiagnostics).Report
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([string] $ProfileName, [switch] $SkipConnection, [switch] $NoSave)
    $config = Get-ErpPrinterConfig
    $results = New-Object System.Collections.Generic.List[object]
    $run = {
        param([string] $Category, [string] $Name, [scriptblock] $Script)
        foreach ($r in Invoke-ErpDiagnosticStep -Category $Category -Name $Name -Script $Script) { if ($r) { $results.Add($r) } }
    }

    & $run 'System' 'System checks' { Get-ErpSystemChecks }
    & $run 'Storage and logs' 'Storage checks' { Get-ErpStorageChecks -Config $config }
    if ($script:ErpIsWindows) {
        & $run 'Background tasks' 'Task checks' { Get-ErpTaskChecks }
        & $run 'Service history (24 h)' 'Log history' { Get-ErpLogHistoryChecks -Config $config }
        & $run 'Windows printing' 'Spooler checks' { Get-ErpSpoolerChecks }
    }

    $profiles = @($config.Profiles | Where-Object { -not $ProfileName -or $_.Name -eq $ProfileName })
    if ($ProfileName -and -not $profiles.Count) { throw "No printer profile named '$ProfileName'." }
    if (-not $profiles.Count) {
        $results.Add((New-ErpDiagnosticResult 'Profiles' 'Printer profiles' Fail 'No printer profiles are configured.' 'Add a printer in the configuration tool.'))
    }
    foreach ($printerProfile in $profiles) {
        $current = $printerProfile
        if ($script:ErpIsWindows) {
            & $run "Profile '$($current.Name)'" 'Printer checks' { Get-ErpProfileChecks -Config $config -PrinterProfile $current }
        }
        $missing = @(Test-ErpPrinterProfile -PrinterProfile $current -Secrets (Get-ErpProfileSecrets -ProfileName $current.Name))
        if ($missing.Count) {
            $results.Add((New-ErpDiagnosticResult "Profile '$($current.Name)'" 'Settings' Fail ($missing -join [Environment]::NewLine) 'Complete the profile in the Printers tab.'))
        } elseif (-not $SkipConnection) {
            & $run "Profile '$($current.Name)'" 'Connection checks' { Get-ErpConnectionChecks -Config $config -PrinterProfile $current }
        }
    }

    $report = Format-ErpDiagnosticReport -Results $results.ToArray()
    $reportPath = $null
    if (-not $NoSave) { $reportPath = Save-ErpDiagnosticReport -Report $report -Config $config }
    [pscustomobject]@{
        Failures   = @($results | Where-Object Status -eq 'Fail').Count
        Warnings   = @($results | Where-Object Status -eq 'Warn').Count
        Results    = $results.ToArray()
        Report     = $report
        ReportPath = $reportPath
    }
}

function Save-ErpDiagnosticReport {
    <# Writes the report next to the logs (or to %TEMP% if that is not writable) and returns the path, or $null. #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)] [string] $Report, [Parameter(Mandatory)] $Config)
    $name = 'diagnostics-{0:yyyyMMdd-HHmmss}.log' -f (Get-Date)
    foreach ($dir in (Join-Path $Config.Global.DataRoot 'logs'), [IO.Path]::GetTempPath()) {
        try {
            if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
            $path = Join-Path $dir $name
            [IO.File]::WriteAllText($path, $Report, (New-Object Text.UTF8Encoding $false))
            return $path
        } catch { Write-Verbose "Could not save the report to ${dir}: $_" }
    }
    $null
}
