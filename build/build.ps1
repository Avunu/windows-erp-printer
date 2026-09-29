<#
.SYNOPSIS
    Builds, tests and packages ERP Printer.
.DESCRIPTION
    Tasks (run in the order given):
      Lint      PSScriptAnalyzer over src, build and tests
      Test      Pester unit tests (NUnit XML to out/test-results.xml)
      Generate  Regenerate policies/*.admx|adml and docs/settings.md from the settings schema
      Stage     Copy src to out/payload and stamp version/build info
      Sign      Authenticode-sign staged scripts (needs -CertificatePath)
      Msi       Build the MSI with WiX v5 (Windows only), and sign it if a certificate is given
      Manifest  Write latest.json, the .sha256 file and the ADMX zip for a release
    Lint, Test, Generate and Stage also run under PowerShell 7 on Linux/macOS.
.EXAMPLE
    ./build/build.ps1 -Task Lint, Test
.EXAMPLE
    ./build/build.ps1 -Task Stage, Msi, Manifest -Version 1.2.0 -UpdateManifestUrl https://github.com/acme/windows-erp-printer/releases/latest/download/latest.json
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Build console output.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'CertificatePasswordEnvVar', Justification = 'Holds the name of an environment variable, not a password.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Parameters are used by nested functions.')]
[CmdletBinding()]
param(
    [string[]] $Task = @('Lint', 'Test', 'Stage', 'Msi', 'Manifest'),
    # Defaults to the module version, which release-please bumps on every release.
    [ValidatePattern('^\d{1,3}\.\d{1,3}\.\d{1,5}$')]
    [string] $Version,
    [string] $UpdateManifestUrl = '',
    [string] $ReleaseDownloadUrl = '',
    [string] $ProjectUrl = 'https://github.com',
    [string] $Commit = '',
    [string] $OutputDir = (Join-Path $PSScriptRoot '..\out'),
    [string] $CertificatePath,
    [string] $CertificatePasswordEnvVar = 'SIGNING_CERT_PASSWORD',
    [string] $TimestampServer = 'http://timestamp.digicert.com',
    [string] $WixUtilVersion = '5.0.2'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
# Accept "-Task Lint,Test" from powershell.exe -File, which passes lists as one string.
$Task = @($Task | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$knownTasks = 'Lint', 'Test', 'Generate', 'Stage', 'Sign', 'Msi', 'Manifest'
foreach ($name in $Task) { if ($knownTasks -notcontains $name) { throw "Unknown task '$name'. Valid tasks: $($knownTasks -join ', ')." } }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Version) { $Version = (Import-PowerShellDataFile (Join-Path $root 'src\Modules\ErpPrinter\ErpPrinter.psd1')).ModuleVersion }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path
$payload = Join-Path $OutputDir 'payload'
$msiName = "ErpPrinter-$Version-x64.msi"
$msiPath = Join-Path $OutputDir $msiName
if (-not $Commit) { try { $Commit = (git -C $root rev-parse --short HEAD 2>$null) } catch { $Commit = '' } }
if (-not $Commit) { $Commit = 'local' }

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

function Get-SigningCertificate {
    if (-not $CertificatePath) { return $null }
    $password = [Environment]::GetEnvironmentVariable($CertificatePasswordEnvVar)
    $flags = [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable
    New-Object Security.Cryptography.X509Certificates.X509Certificate2($CertificatePath, $password, $flags)
}

function Invoke-Sign([string[]] $Path) {
    $cert = Get-SigningCertificate
    if (-not $cert) { Write-Warning 'No signing certificate; skipping signing.'; return }
    foreach ($file in $Path) {
        $result = Set-AuthenticodeSignature -FilePath $file -Certificate $cert -TimestampServer $TimestampServer -HashAlgorithm SHA256
        if ($result.Status -ne 'Valid') { throw "Signing $file failed: $($result.Status) $($result.StatusMessage)" }
    }
}

foreach ($step in $Task) {
    switch ($step) {
        'Lint' {
            Write-Step 'PSScriptAnalyzer'
            Import-Module PSScriptAnalyzer
            $settings = Join-Path $root 'PSScriptAnalyzerSettings.psd1'
            $findings = @(foreach ($dir in 'src', 'build', 'tests') {
                    $analyzerArgs = @{ Path = (Join-Path $root $dir); Recurse = $true; Settings = $settings }
                    # Tests use Pester 5, which the Windows PowerShell 5.1 profile does not know (it ships Pester 3).
                    if ($dir -eq 'tests') { $analyzerArgs.ExcludeRule = 'PSUseCompatibleCommands' }
                    Invoke-ScriptAnalyzer @analyzerArgs
                })
            if ($findings.Count) {
                $findings | Format-Table RuleName, Severity, ScriptName, Line, Message -AutoSize -Wrap | Out-String -Width 220 | Write-Host
                throw "PSScriptAnalyzer reported $($findings.Count) issue(s)."
            }
            Write-Host 'No analyzer findings.'
        }
        'Test' {
            Write-Step 'Pester'
            # This script's strict mode would leak into test and mock bodies; the module sets its own.
            Set-StrictMode -Off
            Import-Module Pester -MinimumVersion 5.5
            $config = New-PesterConfiguration
            $config.Run.Path = Join-Path $root 'tests\Unit'
            $config.Run.PassThru = $true
            $config.Output.Verbosity = 'Detailed'
            $config.TestResult.Enabled = $true
            $config.TestResult.OutputFormat = 'NUnitXml'
            $config.TestResult.OutputPath = Join-Path $OutputDir 'test-results.xml'
            # Splatted: the analyzer's 5.1 profile only knows the Pester 3 bundled with Windows.
            $pesterArgs = @{ Configuration = $config }
            $result = Invoke-Pester @pesterArgs
            if ($result.FailedCount -gt 0 -or $result.Result -ne 'Passed') { throw "$($result.FailedCount) test(s) failed." }
        }
        'Generate' {
            Write-Step 'Generating ADMX/ADML and the settings reference'
            & (Join-Path $PSScriptRoot 'New-PolicyTemplate.ps1') -OutputDir (Join-Path $root 'policies')
            & (Join-Path $PSScriptRoot 'New-SettingsReference.ps1') -OutputPath (Join-Path $root 'docs\settings.md')
        }
        'Stage' {
            Write-Step "Staging payload $Version ($Commit)"
            if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
            Copy-Item -Path (Join-Path $root 'src') -Destination $payload -Recurse
            Copy-Item -Path (Join-Path $root 'policies') -Destination (Join-Path $payload 'Policies') -Recurse
            Copy-Item -Path (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE') -Destination $payload
            $moduleDir = Join-Path $payload 'Modules\ErpPrinter'
            $manifest = Join-Path $moduleDir 'ErpPrinter.psd1'
            (Get-Content $manifest -Raw) -replace "ModuleVersion\s*=\s*'[^']*'", "ModuleVersion        = '$Version'" |
                Set-Content -Path $manifest -Encoding UTF8 -NoNewline
            $escape = { param($s) $s -replace "'", "''" }
            @(
                '# Generated by build/build.ps1'
                '@{'
                "    Version           = '$Version'"
                "    Commit            = '$(& $escape $Commit)'"
                "    UpdateManifestUrl = '$(& $escape $UpdateManifestUrl)'"
                '}'
            ) | Set-Content -Path (Join-Path $moduleDir 'BuildInfo.psd1') -Encoding UTF8
            $info = Import-PowerShellDataFile (Join-Path $moduleDir 'BuildInfo.psd1')
            if ($info.Version -ne $Version) { throw 'BuildInfo stamping failed.' }
        }
        'Sign' {
            Write-Step 'Signing scripts'
            $files = Get-ChildItem -Path $payload -Recurse -Include '*.ps1', '*.psm1', '*.psd1' | ForEach-Object FullName
            Invoke-Sign $files
        }
        'Msi' {
            Write-Step "Building $msiName"
            if (-not (Get-Command wix -ErrorAction SilentlyContinue)) { throw 'The wix CLI is not installed: dotnet tool install --global wix --version 5.0.2' }
            $extensions = (& wix extension list -g) -join "`n"
            if ($extensions -notmatch 'WixToolset\.Util\.wixext') {
                & wix extension add -g "WixToolset.Util.wixext/$WixUtilVersion"
                if ($LASTEXITCODE) { throw 'wix extension add failed.' }
            }
            & wix build (Join-Path $root 'installer\ErpPrinter.wxs') -arch x64 -ext WixToolset.Util.wixext `
                -d "Version=$Version" -d "PayloadDir=$payload" -d "ProjectUrl=$ProjectUrl" -o $msiPath
            if ($LASTEXITCODE) { throw "wix build failed with exit code $LASTEXITCODE." }
            Remove-Item (Join-Path $OutputDir '*.wixpdb') -ErrorAction SilentlyContinue
            if ($CertificatePath) { Invoke-Sign @($msiPath) }
        }
        'Manifest' {
            Write-Step 'Writing release manifest'
            if (-not (Test-Path $msiPath)) { throw "$msiPath not found; run the Msi task first." }
            $hash = (Get-FileHash $msiPath -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $msiName" | Set-Content -Path "$msiPath.sha256" -Encoding ASCII
            $url = if ($ReleaseDownloadUrl) { $ReleaseDownloadUrl.TrimEnd('/') + "/$msiName" } else { $msiName }
            [ordered]@{
                version   = $Version
                url       = $url
                sha256    = $hash
                published = (Get-Date).ToUniversalTime().ToString('o')
                notes     = if ($ReleaseDownloadUrl) { "$ProjectUrl/releases/tag/v$Version" } else { '' }
            } | ConvertTo-Json | Set-Content -Path (Join-Path $OutputDir 'latest.json') -Encoding UTF8
            $zip = Join-Path $OutputDir "ErpPrinter-$Version-admx.zip"
            if (Test-Path $zip) { Remove-Item $zip }
            Compress-Archive -Path (Join-Path $root 'policies\*') -DestinationPath $zip
            Get-ChildItem $OutputDir -File | Format-Table Name, Length -AutoSize | Out-String | Write-Host
        }
    }
}
