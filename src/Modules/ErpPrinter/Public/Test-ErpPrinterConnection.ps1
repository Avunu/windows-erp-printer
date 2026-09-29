function Test-ErpPrinterConnection {
    <#
    .SYNOPSIS
        Checks that a profile's ERP is reachable and the credentials work.
    .PARAMETER ProfileName
        Test a saved profile.
    .PARAMETER PrinterProfile
        Test unsaved settings (as the GUI does). Pass -Secrets to use unsaved credentials;
        empty entries fall back to the stored ones.
    .EXAMPLE
        Test-ErpPrinterConnection -ProfileName 'Send to Odoo'
    #>
    [CmdletBinding(DefaultParameterSetName = 'ByName')]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ByName', Position = 0)] [string] $ProfileName,
        [Parameter(Mandatory, ParameterSetName = 'ByObject')] [System.Collections.IDictionary] $PrinterProfile,
        [Parameter(ParameterSetName = 'ByObject')] [hashtable] $Secrets = @{}
    )
    $config = Get-ErpPrinterConfig
    if ($PSCmdlet.ParameterSetName -eq 'ByName') {
        $PrinterProfile = $config.Profiles | Where-Object { $_.Name -eq $ProfileName } | Select-Object -First 1
        if (-not $PrinterProfile) { throw "No printer profile named '$ProfileName'." }
    }
    $effectiveSecrets = Get-ErpProfileSecrets -ProfileName $PrinterProfile.Name
    foreach ($key in @($Secrets.Keys)) { if ($Secrets[$key]) { $effectiveSecrets[$key] = $Secrets[$key] } }

    $problems = @(Test-ErpPrinterProfile -PrinterProfile $PrinterProfile -Secrets $effectiveSecrets)
    if ($problems.Count) {
        return [pscustomobject]@{ Success = $false; Message = ($problems -join [Environment]::NewLine) }
    }
    try {
        $command = Get-ErpBackendCommand -Backend $PrinterProfile.Backend -Action Test
        $message = & $command -PrinterProfile $PrinterProfile -GlobalSettings $config.Global -Secrets $effectiveSecrets
        [pscustomobject]@{ Success = $true; Message = $message }
    } catch {
        [pscustomobject]@{ Success = $false; Message = $_.Exception.Message }
    }
}
