<#
.SYNOPSIS
    Generates policies/ErpPrinter.admx and policies/en-US/ErpPrinter.adml with the C# CLI
    ("erpprinter schema --admx <dir>") instead of build/New-PolicyTemplate.ps1.
.DESCRIPTION
    The C# Schema class becomes the source of truth for the settings once the port replaces the PowerShell
    product. The PowerShell
    generator (build/New-PolicyTemplate.ps1, build.ps1 -Task Generate) remains authoritative.

    Contract with the CLI: "erpprinter schema --admx <dir>" writes <dir>\ErpPrinter.admx and
    <dir>\en-US\ErpPrinter.adml, byte-compatible with what the PowerShell generator writes (UTF-8, CRLF).

    -Check generates into a temporary folder and compares with -OutputDir instead of overwriting it. Use it as the
    parity test between the two generators during the migration.
.EXAMPLE
    ./build/New-PolicyTemplateFromCli.ps1 -ErpPrinterExe out/dotnet/payload/erpprinter.exe -Check
#>
[CmdletBinding()]
param(
    [string] $ErpPrinterExe,
    [string] $OutputDir,
    [switch] $Check
)
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 leaves $PSScriptRoot empty in param() defaults under -File, so resolve here.
if (-not $ErpPrinterExe) { $ErpPrinterExe = Join-Path $PSScriptRoot '..\out\dotnet\payload\erpprinter.exe' }
if (-not $OutputDir) { $OutputDir = Join-Path $PSScriptRoot '..\policies' }
if (-not (Test-Path -LiteralPath $ErpPrinterExe)) { throw "$ErpPrinterExe not found. Publish first: ./build/build.ps1 -Task DotnetPublish" }

$target = $OutputDir
if ($Check) { $target = Join-Path ([IO.Path]::GetTempPath()) ('erp-admx-' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Force -Path $target | Out-Null
try {
    $output = & $ErpPrinterExe schema --admx $target 2>&1
    if ($LASTEXITCODE) { throw "'erpprinter schema --admx' failed with exit code ${LASTEXITCODE} (is the schema command implemented yet?): $output" }
    foreach ($relative in 'ErpPrinter.admx', 'en-US\ErpPrinter.adml') {
        if (-not (Test-Path -LiteralPath (Join-Path $target $relative))) { throw "'erpprinter schema --admx' did not write $relative." }
        if ($Check) {
            $expected = [IO.File]::ReadAllBytes((Join-Path $OutputDir $relative))
            $actual = [IO.File]::ReadAllBytes((Join-Path $target $relative))
            if ([Convert]::ToBase64String($expected) -ne [Convert]::ToBase64String($actual)) { throw "$relative differs between the PowerShell generator and the C# CLI." }
        }
    }
    Write-Output $(if ($Check) { 'ADMX/ADML from the C# CLI match the committed files.' } else { "ADMX/ADML written to $OutputDir by the C# CLI." })
} finally {
    if ($Check -and (Test-Path -LiteralPath $target)) { Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue }
}
