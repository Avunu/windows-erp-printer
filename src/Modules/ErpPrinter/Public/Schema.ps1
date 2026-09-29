function Get-ErpPrinterSettingSchema {
    <#
    .SYNOPSIS
        Returns the setting definitions for the global or per-printer scope.
    .PARAMETER Backend
        Only return profile settings that apply to this backend.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [ValidateSet('Global', 'Profile')] [string] $Scope = 'Profile',
        [string] $Backend
    )
    $schema = if ($Scope -eq 'Global') { $script:ErpGlobalSchema } else { $script:ErpProfileSchema }
    if ($Backend) {
        $schema = $schema | Where-Object { $_.Backends.Count -eq 0 -or $_.Backends -contains $Backend }
    }
    $schema
}
