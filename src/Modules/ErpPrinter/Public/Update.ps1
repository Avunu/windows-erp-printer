# Self-update. Releases publish latest.json next to the MSI:
#   { "version": "1.4.0", "url": "ErpPrinter-1.4.0-x64.msi", "sha256": "<hex>", "notes": "<url>" }
# "url" may be absolute or relative to the manifest. The manifest can live on GitHub
# releases (default), any HTTPS server, or a UNC share for air-gapped fleets.

function Get-ErpPrinterVersion {
    <# Returns the installed ERP Printer version. #>
    [CmdletBinding()]
    [OutputType([string])]
    param()
    [string]$script:ErpBuildInfo.Version
}

function Get-ErpUpdateManifest {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Url, [System.Collections.IDictionary] $GlobalSettings = @{})
    if ($Url -match '^https?://') {
        $response = Invoke-ErpHttp -Method GET -Uri $Url -GlobalSettings $GlobalSettings
        Assert-ErpHttpSuccess -Response $response -Context 'Update manifest download'
        $manifest = $response.Json
    } else {
        $path = if ($Url -match '^file://') { ([Uri]$Url).LocalPath } else { $Url }
        $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    }
    foreach ($field in 'version', 'url', 'sha256') {
        if (-not $manifest -or -not $manifest.PSObject.Properties[$field] -or -not $manifest.$field) {
            throw "Update manifest at $Url is missing '$field'."
        }
    }
    $manifest
}

function Resolve-ErpUpdatePackageUrl {
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)] [string] $ManifestUrl, [Parameter(Mandatory)] [string] $PackageUrl)
    if ($PackageUrl -match '^(https?|file)://' -or $PackageUrl -match '^\\\\' -or $PackageUrl -match '^[A-Za-z]:\\') { return $PackageUrl }
    if ($ManifestUrl -match '^https?://') { return ([Uri]::new([Uri]$ManifestUrl, $PackageUrl)).AbsoluteUri }
    $base = if ($ManifestUrl -match '^file://') { ([Uri]$ManifestUrl).LocalPath } else { $ManifestUrl }
    Join-Path (Split-Path -Parent $base) $PackageUrl
}

function Compare-ErpVersion {
    <# Returns 1 if $Available is newer than $Current, 0 if equal, -1 if older. #>
    [CmdletBinding()]
    [OutputType([int])]
    param([Parameter(Mandatory)] [string] $Current, [Parameter(Mandatory)] [string] $Available)
    $parse = { param($v) [version](($v -replace '^v', '' -replace '[-+].*$', '').Trim()) }
    (& $parse $Available).CompareTo((& $parse $Current))
}

function Invoke-ErpPrinterUpdate {
    <#
    .SYNOPSIS
        Checks the update manifest and, if a newer version exists, downloads, verifies and installs it.
    .PARAMETER CheckOnly
        Report whether an update is available without installing it.
    .PARAMETER Force
        Install even if AutoUpdate is off or the version is not newer (reinstall/downgrade by manifest).
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([pscustomobject])]
    param([switch] $CheckOnly, [switch] $Force)
    $config = Get-ErpPrinterConfig
    $settings = $config.Global
    Set-ErpLogContext -Component 'updater' -GlobalSettings $settings
    $result = [pscustomobject]@{
        CurrentVersion   = Get-ErpPrinterVersion
        AvailableVersion = $null
        UpdateAvailable  = $false
        Installed        = $false
        Message          = ''
    }
    if (-not $settings.AutoUpdate -and -not $CheckOnly -and -not $Force) {
        $result.Message = 'Automatic updates are disabled.'
        return $result
    }
    if (-not $settings.UpdateManifestUrl) {
        $result.Message = 'No update manifest URL is configured.'
        return $result
    }

    $manifest = Get-ErpUpdateManifest -Url $settings.UpdateManifestUrl -GlobalSettings $settings
    $result.AvailableVersion = [string]$manifest.version
    $result.UpdateAvailable = (Compare-ErpVersion -Current $result.CurrentVersion -Available $manifest.version) -gt 0
    if (-not $result.UpdateAvailable -and -not $Force) {
        $result.Message = "Up to date ($($result.CurrentVersion))."
        return $result
    }
    if ($CheckOnly) {
        $result.Message = "Version $($manifest.version) is available."
        return $result
    }
    Assert-ErpAdministrator
    if (-not $PSCmdlet.ShouldProcess("version $($manifest.version)", 'Install update')) { return $result }

    $dir = Join-Path $settings.DataRoot 'updates'
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $package = Join-Path $dir ("ErpPrinter-{0}.msi" -f (ConvertTo-ErpSafeName ([string]$manifest.version)))
    $source = Resolve-ErpUpdatePackageUrl -ManifestUrl $settings.UpdateManifestUrl -PackageUrl $manifest.url
    $expected = ([string]$manifest.sha256).Trim().ToLowerInvariant()

    $haveValidCopy = (Test-Path -LiteralPath $package) -and ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant() -eq $expected)
    if (-not $haveValidCopy) {
        Write-ErpLog -Message "Downloading update $($manifest.version) from $source."
        if ($source -match '^https?://') {
            $response = Invoke-ErpHttp -Method GET -Uri $source -OutFile $package -GlobalSettings $settings
            Assert-ErpHttpSuccess -Response $response -Context 'Update download'
        } else {
            $path = if ($source -match '^file://') { ([Uri]$source).LocalPath } else { $source }
            Copy-Item -LiteralPath $path -Destination $package -Force
        }
    }
    Assert-ErpUpdatePackage -Path $package -Sha256 $expected -GlobalSettings $settings

    $log = Join-Path $dir ("install-{0}-{1:yyyyMMddHHmmss}.log" -f (ConvertTo-ErpSafeName ([string]$manifest.version)), (Get-Date))
    Write-ErpLog -Message "Installing update $($result.CurrentVersion) -> $($manifest.version). MSI log: $log" -EventId 1010
    $msiArgs = @('/i', "`"$package`"", '/qn', '/norestart', '/l*v', "`"$log`"", 'LAUNCHCONFIG=0')
    $msiexec = if ($env:SystemRoot) { Join-Path $env:SystemRoot 'System32\msiexec.exe' } else { 'msiexec.exe' }
    $process = Start-Process -FilePath $msiexec -ArgumentList $msiArgs -Wait -PassThru
    switch ($process.ExitCode) {
        { $_ -in 0, 3010, 1641 } {
            $result.Installed = $true
            $result.Message = "Installed version $($manifest.version)$(if ($_ -ne 0) { ' (a restart is required to finish)' })."
            Write-ErpLog -Message $result.Message -EventId 1011
        }
        1618 { throw 'Another installation is in progress; the update will be retried next time.' }
        default { throw "msiexec failed with exit code $($process.ExitCode). See $log." }
    }
    $result
}

function Assert-ErpUpdatePackage {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Path, [Parameter(Mandatory)] [string] $Sha256, [System.Collections.IDictionary] $GlobalSettings = @{})
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256.ToLowerInvariant()) {
        Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
        throw "Update package hash mismatch (expected $Sha256, got $actual). The download was discarded."
    }
    if ($GlobalSettings.RequireSignedUpdates -or $GlobalSettings.UpdateSignerThumbprint) {
        $signature = Get-AuthenticodeSignature -FilePath $Path
        if ($signature.Status -ne 'Valid') { throw "Update package signature is not valid: $($signature.Status) $($signature.StatusMessage)" }
        $wanted = ([string]$GlobalSettings.UpdateSignerThumbprint -replace '\s', '').ToUpperInvariant()
        if ($wanted -and $signature.SignerCertificate.Thumbprint.ToUpperInvariant() -ne $wanted) {
            throw "Update package is signed by $($signature.SignerCertificate.Subject), not the configured signer."
        }
    }
}
