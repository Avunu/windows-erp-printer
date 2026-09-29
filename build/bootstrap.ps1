<#
.SYNOPSIS
    Installs the pinned build/test modules for the current PowerShell edition.
    Run it with each shell you test under (powershell.exe and pwsh keep separate module paths).
#>
[CmdletBinding()]
param(
    [string] $PesterVersion = '5.7.1',
    [string] $ScriptAnalyzerVersion = '1.25.0'
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
if (-not (Get-PackageProvider -ListAvailable -Name NuGet -ErrorAction SilentlyContinue)) {
    Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope CurrentUser | Out-Null
}
Set-PSRepository -Name PSGallery -InstallationPolicy Trusted
foreach ($module in @(@{ Name = 'Pester'; Version = $PesterVersion }, @{ Name = 'PSScriptAnalyzer'; Version = $ScriptAnalyzerVersion })) {
    if (-not (Get-Module -ListAvailable -Name $module.Name | Where-Object { $_.Version -eq [version]$module.Version })) {
        Install-Module -Name $module.Name -RequiredVersion $module.Version -Scope CurrentUser -Force -SkipPublisherCheck -AllowClobber
    }
    Import-Module -Name $module.Name -RequiredVersion $module.Version -Force
    Write-Output "$($module.Name) $($module.Version) ready for PowerShell $($PSVersionTable.PSVersion)."
}
