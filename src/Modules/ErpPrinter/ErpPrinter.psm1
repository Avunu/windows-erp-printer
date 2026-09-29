#Requires -Version 5.1
# Version 1 catches uninitialised variables but still lets optional dictionary keys and
# sidecar fields read as $null, which a long-running service loop should tolerate.
Set-StrictMode -Version 1.0

$script:ErpProductName  = 'ERP Printer'
$script:ErpEventSource  = 'ErpPrinter'
$script:ErpTaskPath     = '\ERP Printer\'
$script:ErpRegistryRoot = 'HKLM:\SOFTWARE\ErpPrinter'
$script:ErpPolicyRoot   = 'HKLM:\SOFTWARE\Policies\ErpPrinter'
$script:ErpOutboxSignal = 'Global\ErpPrinterOutbox'
$script:ErpLogComponent = 'module'
$script:ErpIsWindows    = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT

$buildInfoPath = Join-Path $PSScriptRoot 'BuildInfo.psd1'
$script:ErpBuildInfo = if (Test-Path $buildInfoPath) { Import-PowerShellDataFile $buildInfoPath } else { @{ Version = '0.0.0'; Commit = 'dev'; UpdateManifestUrl = '' } }

# Loaded up front so parameters can be typed with System.Net.Http classes.
Add-Type -AssemblyName System.Net.Http

# Windows PowerShell 5.1 on older .NET builds does not offer TLS 1.2 by default.
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
} catch { Write-Verbose "Could not enable TLS 1.2: $_" }

foreach ($folder in 'Private', 'Backends', 'Public') {
    $dir = Join-Path $PSScriptRoot $folder
    if (Test-Path $dir) {
        foreach ($file in Get-ChildItem -Path $dir -Filter '*.ps1' | Sort-Object Name) {
            . $file.FullName
        }
    }
}
