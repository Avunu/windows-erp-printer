<#
.SYNOPSIS
    Builds, tests and packages ERP Printer.
.DESCRIPTION
    Tasks (run in the order given):
      Build     dotnet build of ErpPrinter.slnx (off Windows: only the portable Core project and its tests)
      Test      xUnit tests: Core anywhere, Windows and Config tests on Windows (see -TestScope)
      Generate  Regenerate policies/ (ADMX/ADML) and docs/settings.md from the settings schema with "erpprinter schema"
      Publish   Self-contained win-x64 publish of the service, CLI and configuration window into out/payload
      Sign      Authenticode-sign the product's own executables and assemblies (needs -CertificatePath)
      Msi       Build out/ErpPrinter-<version>-x64.msi with the repo-local WiX 5 tool, and sign it if a certificate is given
      Manifest  Write latest.json, the .sha256 file and the ADMX zip for a release into out/
    The version defaults to Directory.Build.props, which release-please bumps on every release.
.EXAMPLE
    ./build/build.ps1 -Task Build, Test
.EXAMPLE
    ./build/build.ps1 -Task Publish, Msi, Manifest -Version 1.2.0 -UpdateManifestUrl https://github.com/acme/windows-erp-printer/releases/latest/download/latest.json
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Build console output.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'CertificatePasswordEnvVar', Justification = 'Holds the name of an environment variable, not a password.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Parameters are used by nested functions.')]
[CmdletBinding()]
param(
    [string[]] $Task = @('Build', 'Test', 'Publish', 'Msi', 'Manifest'),
    [ValidatePattern('^\d{1,3}\.\d{1,3}\.\d{1,5}$')]
    [string] $Version,
    [string] $UpdateManifestUrl = '',
    [string] $ReleaseDownloadUrl = '',
    [string] $ProjectUrl = 'https://github.com',
    [string] $Commit = '',
    [string] $OutputDir,
    [string] $CertificatePath,
    [string] $CertificatePasswordEnvVar = 'SIGNING_CERT_PASSWORD',
    [string] $TimestampServer = 'http://timestamp.digicert.com',
    [string] $WixUtilVersion = '5.0.2',
    [string] $Configuration = 'Release',
    [ValidateSet('All', 'Core', 'Windows')]
    [string] $TestScope = 'All'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
# Accept "-Task Build,Test" from powershell.exe -File, which passes lists as one string.
$Task = @($Task | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$knownTasks = 'Build', 'Test', 'Generate', 'Publish', 'Sign', 'Msi', 'Manifest'
foreach ($name in $Task) { if ($knownTasks -notcontains $name) { throw "Unknown task '$name'. Valid tasks: $($knownTasks -join ', ')." } }
# Windows PowerShell 5.1 leaves $PSScriptRoot empty in param() defaults under -File, so resolve here.
if (-not $OutputDir) { $OutputDir = Join-Path $PSScriptRoot '..\out' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path
$payload = Join-Path $OutputDir 'payload'
$onWindows = ($PSVersionTable.PSEdition -eq 'Desktop') -or $IsWindows

# The product versions itself from Directory.Build.props (release-please bumps it on every release).
if (-not $Version) {
    $buildProps = [xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)
    $Version = [string]($buildProps.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
}
$msiName = "ErpPrinter-$Version-x64.msi"
$msiPath = Join-Path $OutputDir $msiName
if (-not $Commit) { try { $Commit = (git -C $root rev-parse --short HEAD 2>$null) } catch { $Commit = '' } }
if (-not $Commit) { $Commit = 'local' }

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

function Invoke-Dotnet([string[]] $Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE." }
}

function Get-StampArguments {
    @("-p:BuildCommit=$Commit", "-p:UpdateManifestUrl=$UpdateManifestUrl")
}

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

# WiX is a repo-local tool (.config/dotnet-tools.json pins 5.0.2) and its extensions live in the repo's .wix folder,
# so the build never depends on a global installation (a different WiX on PATH is never used).
function Invoke-Wix([string[]] $Arguments) {
    Push-Location $root
    try {
        & dotnet tool run wix @Arguments
        if ($LASTEXITCODE) { throw "wix $($Arguments[0]) failed with exit code $LASTEXITCODE." }
    } finally { Pop-Location }
}

function Initialize-LocalWix {
    Push-Location $root
    try {
        Invoke-Dotnet @('tool', 'restore')
        $installed = (& dotnet tool run wix --version) -join ''
        if ($installed -notmatch '^5\.') { throw "Expected the repo-local WiX 5, got '$installed'. Check .config/dotnet-tools.json." }
        $extensions = (& dotnet tool run wix extension list) -join "`n"
        if ($extensions -notmatch 'WixToolset\.Util\.wixext') {
            Invoke-Dotnet @('tool', 'run', 'wix', 'extension', 'add', "WixToolset.Util.wixext/$WixUtilVersion")
        }
    } finally { Pop-Location }
}

foreach ($step in $Task) {
    switch ($step) {
        'Build' {
            Write-Step "dotnet build ($Configuration)"
            # The Windows projects target net10.0-windows; off Windows only the portable core and its tests build.
            $targets = if ($onWindows) { @(Join-Path $root 'ErpPrinter.slnx') } else {
                @((Join-Path $root 'src\ErpPrinter.Core\ErpPrinter.Core.csproj'), (Join-Path $root 'tests\ErpPrinter.Core.Tests\ErpPrinter.Core.Tests.csproj'))
            }
            foreach ($target in $targets) { Invoke-Dotnet (@('build', $target, '-c', $Configuration, '-nologo', '-v:minimal') + (Get-StampArguments)) }
        }
        'Test' {
            Write-Step "dotnet test ($TestScope)"
            $results = Join-Path $OutputDir 'test-results'
            $projects = New-Object System.Collections.Generic.List[string]
            if ($TestScope -in 'All', 'Core') { $projects.Add('ErpPrinter.Core.Tests') }
            if ($TestScope -in 'All', 'Windows') {
                if ($onWindows) { $projects.Add('ErpPrinter.Windows.Tests'); $projects.Add('ErpPrinter.Config.Tests') }
                elseif ($TestScope -eq 'Windows') { throw 'The Windows tests need Windows.' }
                else { Write-Warning 'Skipping the Windows and Config tests: not running on Windows.' }
            }
            foreach ($name in $projects) {
                $project = Join-Path $root "tests\$name\$name.csproj"
                Invoke-Dotnet (@('test', $project, '-c', $Configuration, '-nologo', '-v:minimal', '--logger', "trx;LogFileName=$name.trx", '--results-directory', $results) + (Get-StampArguments))
            }
        }
        'Generate' {
            Write-Step 'Generating ADMX/ADML and the settings reference with erpprinter schema'
            $cli = Join-Path $root 'src\ErpPrinter.Cli\ErpPrinter.Cli.csproj'
            Invoke-Dotnet @('run', '--project', $cli, '-c', $Configuration, '--', 'schema', '--admx', (Join-Path $root 'policies'))
            Invoke-Dotnet @('run', '--project', $cli, '-c', $Configuration, '--', 'schema', '--settings-md', (Join-Path $root 'docs\settings.md'))
        }
        'Publish' {
            Write-Step "Publishing $Version ($Commit)"
            & (Join-Path $PSScriptRoot 'Publish.ps1') -OutputDir $payload -Version $Version `
                -UpdateManifestUrl $UpdateManifestUrl -Commit $Commit -Configuration $Configuration
        }
        'Sign' {
            Write-Step 'Signing the executables and assemblies'
            if (-not (Test-Path $payload)) { throw "$payload not found; run the Publish task first." }
            # Only the product's own binaries: the runtime files are already signed by Microsoft.
            $files = Get-ChildItem -Path $payload -File | Where-Object { $_.Name -match '^(erpprinter|ErpPrinter.*)\.(exe|dll)$' } | ForEach-Object FullName
            Invoke-Sign $files
        }
        'Msi' {
            Write-Step "Building $msiName"
            if (-not (Test-Path $payload)) { throw "$payload not found; run the Publish task first." }
            Initialize-LocalWix
            Invoke-Wix @('build', (Join-Path $root 'installer\ErpPrinter.wxs'), '-arch', 'x64', '-ext', 'WixToolset.Util.wixext',
                '-d', "Version=$Version", '-d', "PayloadDir=$payload", '-d', "ProjectUrl=$ProjectUrl",
                '-d', "IconFile=$(Join-Path $root 'assets\erp-printer.ico')", '-o', $msiPath)
            Remove-Item (Join-Path $OutputDir '*.wixpdb') -ErrorAction SilentlyContinue
            if ($CertificatePath) { Invoke-Sign @($msiPath) }
        }
        'Manifest' {
            Write-Step 'Writing the release manifest'
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
