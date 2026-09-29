BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
    Import-ErpTestModule

    function New-TestManifest([string] $Dir, [string] $Version = '1.2.3', [string] $Hash) {
        New-Item -ItemType Directory -Force -Path $Dir | Out-Null
        $msi = Join-Path $Dir "ErpPrinter-$Version-x64.msi"
        Set-Content -Path $msi -Value 'not really an msi'
        if (-not $Hash) { $Hash = (Get-FileHash $msi -Algorithm SHA256).Hash }
        $manifest = Join-Path $Dir 'latest.json'
        @{ version = $Version; url = "ErpPrinter-$Version-x64.msi"; sha256 = $Hash } | ConvertTo-Json | Set-Content $manifest
        $manifest
    }
}

Describe 'Compare-ErpVersion' {
    It 'compares <Current> with <Available> as <Expected>' -TestCases @(
        @{ Current = '1.2.3'; Available = '1.2.4'; Expected = 1 }
        @{ Current = '1.2.3'; Available = '1.2.3'; Expected = 0 }
        @{ Current = '1.10.0'; Available = '1.9.9'; Expected = -1 }
        @{ Current = '0.0.0'; Available = 'v2.0.0'; Expected = 1 }
        @{ Current = '1.0.0'; Available = '1.0.1-beta.1'; Expected = 1 }
    ) {
        param($Current, $Available, $Expected)
        InModuleScope ErpPrinter -Parameters @{ C = $Current; A = $Available } { param($C, $A) Compare-ErpVersion -Current $C -Available $A } |
            Should -Be $Expected
    }
}

Describe 'Resolve-ErpUpdatePackageUrl' {
    It 'resolves <Package> against <Manifest>' -TestCases @(
        @{ Manifest = 'https://github.com/o/r/releases/latest/download/latest.json'; Package = 'ErpPrinter-1.0.0-x64.msi'; Expected = 'https://github.com/o/r/releases/latest/download/ErpPrinter-1.0.0-x64.msi' }
        @{ Manifest = 'https://updates.example/erp/latest.json'; Package = 'https://cdn.example/p.msi'; Expected = 'https://cdn.example/p.msi' }
        @{ Manifest = '\\fs01\deploy\erp\latest.json'; Package = '\\fs02\p.msi'; Expected = '\\fs02\p.msi' }
    ) {
        param($Manifest, $Package, $Expected)
        InModuleScope ErpPrinter -Parameters @{ M = $Manifest; P = $Package } { param($M, $P) Resolve-ErpUpdatePackageUrl -ManifestUrl $M -PackageUrl $P } |
            Should -Be $Expected
    }
}

Describe 'Get-ErpUpdateManifest' {
    It 'reads a manifest from a file path' {
        $path = New-TestManifest (Join-Path $TestDrive 'm1')
        $manifest = InModuleScope ErpPrinter -Parameters @{ P = $path } { param($P) Get-ErpUpdateManifest -Url $P }
        $manifest.version | Should -Be '1.2.3'
    }

    It 'rejects manifests with missing fields' {
        $path = Join-Path $TestDrive 'bad.json'
        '{"version": "1.0.0"}' | Set-Content $path
        { InModuleScope ErpPrinter -Parameters @{ P = $path } { param($P) Get-ErpUpdateManifest -Url $P } } | Should -Throw "*missing 'url'*"
    }
}

Describe 'Assert-ErpUpdatePackage' {
    It 'deletes a package whose hash does not match' {
        $file = Join-Path $TestDrive 'pkg.msi'
        Set-Content $file 'tampered'
        { InModuleScope ErpPrinter -Parameters @{ F = $file } { param($F) Assert-ErpUpdatePackage -Path $F -Sha256 ('0' * 64) } } | Should -Throw '*hash mismatch*'
        Test-Path $file | Should -BeFalse
    }

    It 'accepts a matching hash regardless of case' {
        $file = Join-Path $TestDrive 'ok.msi'
        Set-Content $file 'fine'
        $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToUpperInvariant()
        { InModuleScope ErpPrinter -Parameters @{ F = $file; H = $hash } { param($F, $H) Assert-ErpUpdatePackage -Path $F -Sha256 $H } } | Should -Not -Throw
    }
}

Describe 'Invoke-ErpPrinterUpdate' {
    BeforeEach { Initialize-FakeRegistry }

    It 'does nothing when automatic updates are disabled' {
        Set-FakeRegistryValue 'HKLM:\SOFTWARE\ErpPrinter' AutoUpdate 0
        (Invoke-ErpPrinterUpdate).Message | Should -Be 'Automatic updates are disabled.'
    }

    It 'reports a missing manifest URL' {
        (Invoke-ErpPrinterUpdate -CheckOnly).Message | Should -BeLike '*No update manifest URL*'
    }

    It 'reports an available update without installing it' {
        $manifest = New-TestManifest (Join-Path $TestDrive 'm2') -Version '9.9.9'
        Set-FakeRegistryValue 'HKLM:\SOFTWARE\ErpPrinter' UpdateManifestUrl $manifest
        $result = Invoke-ErpPrinterUpdate -CheckOnly
        $result.UpdateAvailable | Should -BeTrue
        $result.AvailableVersion | Should -Be '9.9.9'
        $result.Installed | Should -BeFalse
    }

    It 'reports up to date when the manifest is not newer' {
        $manifest = New-TestManifest (Join-Path $TestDrive 'm3') -Version '0.0.0'
        Set-FakeRegistryValue 'HKLM:\SOFTWARE\ErpPrinter' UpdateManifestUrl $manifest
        (Invoke-ErpPrinterUpdate).Message | Should -BeLike 'Up to date*'
    }

    It 'verifies the package and runs msiexec' {
        $dir = Join-Path $TestDrive 'm4'
        $manifest = New-TestManifest $dir -Version '9.9.9'
        Set-FakeRegistryValue 'HKLM:\SOFTWARE\ErpPrinter' UpdateManifestUrl $manifest
        Set-FakeRegistryValue 'HKLM:\SOFTWARE\ErpPrinter' DataRoot (Join-Path $TestDrive 'data')
        Mock -ModuleName ErpPrinter Assert-ErpAdministrator { }
        Mock -ModuleName ErpPrinter Start-Process { [pscustomobject]@{ ExitCode = 0 } }
        $result = Invoke-ErpPrinterUpdate -Confirm:$false
        $result.Installed | Should -BeTrue
        Should -Invoke -ModuleName ErpPrinter Start-Process -Times 1 -Exactly -ParameterFilter {
            $FilePath -like '*msiexec.exe' -and ($ArgumentList -join ' ') -like '/i *ErpPrinter-9.9.9.msi* /qn*LAUNCHCONFIG=0'
        }
        Test-Path (Join-Path $TestDrive 'data/updates/ErpPrinter-9.9.9.msi') | Should -BeTrue
    }

    It 'refuses a package with the wrong hash' {
        $manifest = New-TestManifest (Join-Path $TestDrive 'm5') -Version '9.9.9' -Hash ('a' * 64)
        Set-FakeRegistryValue 'HKLM:\SOFTWARE\ErpPrinter' UpdateManifestUrl $manifest
        Set-FakeRegistryValue 'HKLM:\SOFTWARE\ErpPrinter' DataRoot (Join-Path $TestDrive 'data5')
        Mock -ModuleName ErpPrinter Assert-ErpAdministrator { }
        Mock -ModuleName ErpPrinter Start-Process { throw 'should not run' }
        { Invoke-ErpPrinterUpdate -Confirm:$false } | Should -Throw '*hash mismatch*'
    }
}
