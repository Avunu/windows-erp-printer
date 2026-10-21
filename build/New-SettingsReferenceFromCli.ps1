<#
.SYNOPSIS
    FUTURE SWITCH. Generates docs/settings.md with the C# CLI ("erpprinter schema --settings-md <file>") instead
    of build/New-SettingsReference.ps1.
.DESCRIPTION
    The C# Schema class becomes the source of truth for the settings once the port replaces the PowerShell
    product. Until the CLI implements "schema", this script fails with a clear message and the PowerShell
    generator (build/New-SettingsReference.ps1, build.ps1 -Task Generate) remains authoritative.

    -Check generates into a temporary file and compares with -OutputPath (ignoring line-ending differences)
    instead of overwriting it. Use it as the parity test between the two generators during the migration.
.EXAMPLE
    ./build/New-SettingsReferenceFromCli.ps1 -ErpPrinterExe out/dotnet/payload/erpprinter.exe -Check
#>
[CmdletBinding()]
param(
    [string] $ErpPrinterExe,
    [string] $OutputPath,
    [switch] $Check
)
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 leaves $PSScriptRoot empty in param() defaults under -File, so resolve here.
if (-not $ErpPrinterExe) { $ErpPrinterExe = Join-Path $PSScriptRoot '..\out\dotnet\payload\erpprinter.exe' }
if (-not $OutputPath) { $OutputPath = Join-Path $PSScriptRoot '..\docs\settings.md' }
if (-not (Test-Path -LiteralPath $ErpPrinterExe)) { throw "$ErpPrinterExe not found. Publish first: ./build/build.ps1 -Task DotnetPublish" }

$target = $OutputPath
if ($Check) { $target = Join-Path ([IO.Path]::GetTempPath()) ('erp-settings-' + [guid]::NewGuid().ToString('N') + '.md') }
try {
    $output = & $ErpPrinterExe schema --settings-md $target 2>&1
    if ($LASTEXITCODE) { throw "'erpprinter schema --settings-md' failed with exit code ${LASTEXITCODE} (is the schema command implemented yet?): $output" }
    if (-not (Test-Path -LiteralPath $target)) { throw "'erpprinter schema --settings-md' did not write $target." }
    if ($Check) {
        $normalise = { param($path) ([IO.File]::ReadAllText($path) -replace "`r`n", "`n").TrimEnd() }
        if ((& $normalise $OutputPath) -ne (& $normalise $target)) { throw 'docs/settings.md differs between the PowerShell generator and the C# CLI.' }
    }
    Write-Output $(if ($Check) { 'The settings reference from the C# CLI matches docs/settings.md.' } else { "Settings reference written to $OutputPath by the C# CLI." })
} finally {
    if ($Check -and (Test-Path -LiteralPath $target)) { Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue }
}
