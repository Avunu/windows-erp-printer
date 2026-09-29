BeforeDiscovery {
    $root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    $script:ScriptFiles = @(Get-ChildItem -Path (Join-Path $root 'src'), (Join-Path $root 'build'), (Join-Path $root 'tests') -Recurse -Include '*.ps1', '*.psm1', '*.psd1' |
            ForEach-Object { @{ Path = $_.FullName; Name = $_.FullName.Substring($root.Length + 1) } })
}

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
    Import-ErpTestModule
}

Describe 'Source files' {
    It '<Name> parses without errors' -TestCases $script:ScriptFiles {
        param($Path)
        $tokens = $null
        $errors = $null
        $null = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
        $errors | Should -BeNullOrEmpty
    }

    It '<Name> is plain ASCII (Windows PowerShell 5.1 reads BOM-less files as ANSI)' -TestCases $script:ScriptFiles {
        param($Path)
        $bytes = [IO.File]::ReadAllBytes($Path)
        @($bytes | Where-Object { $_ -gt 127 }).Count | Should -Be 0
    }
}

Describe 'Module manifest' {
    It 'is valid' {
        { Test-ModuleManifest -Path $script:ModuleManifest -ErrorAction Stop } | Should -Not -Throw
    }

    It 'exports exactly the functions defined in Public/' {
        $publicDir = Join-Path $script:RepoRoot 'src/Modules/ErpPrinter/Public'
        $defined = foreach ($file in Get-ChildItem $publicDir -Filter *.ps1) {
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$null, [ref]$null)
            $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false) |
                Where-Object { $_.Name -like '*-ErpPrinter*' } | ForEach-Object Name
        }
        $exported = (Get-Module ErpPrinter).ExportedFunctions.Keys
        @($exported | Sort-Object) | Should -Be @($defined | Sort-Object -Unique)
    }

    It 'documents every exported function' {
        foreach ($name in (Get-Module ErpPrinter).ExportedFunctions.Keys) {
            (Get-Help $name).Synopsis | Should -Not -BeLike "$name*" -Because "$name needs a .SYNOPSIS"
        }
    }

    It 'reads build info' {
        Get-ErpPrinterVersion | Should -Match '^\d+\.\d+\.\d+$'
    }
}

Describe 'Settings schema' {
    It 'has unique names per scope' {
        foreach ($scope in 'Global', 'Profile') {
            $names = @(Get-ErpPrinterSettingSchema -Scope $scope | ForEach-Object Name)
            $names.Count | Should -Be (@($names | Sort-Object -Unique)).Count
        }
    }

    It 'gives every Choice setting a default among its choices' {
        foreach ($s in @(Get-ErpPrinterSettingSchema -Scope Global) + @(Get-ErpPrinterSettingSchema -Scope Profile) | Where-Object Type -eq 'Choice') {
            $s.Choices | Should -Contain $s.Default
        }
    }

    It 'only references known backends' {
        $known = InModuleScope ErpPrinter { $script:ErpBackendNames }
        foreach ($s in Get-ErpPrinterSettingSchema -Scope Profile) {
            foreach ($b in $s.Backends) { $known | Should -Contain $b }
        }
    }

    It 'has a Send and Test command for every backend' {
        InModuleScope ErpPrinter {
            foreach ($backend in $script:ErpBackendNames) {
                Get-Command "Send-Erp${backend}Document" -ErrorAction SilentlyContinue | Should -Not -BeNullOrEmpty
                Get-Command "Test-Erp${backend}Connection" -ErrorAction SilentlyContinue | Should -Not -BeNullOrEmpty
            }
        }
    }

    It 'filters profile settings by backend' {
        $names = Get-ErpPrinterSettingSchema -Scope Profile -Backend ERPNext | ForEach-Object Name
        $names | Should -Contain 'ApiSecret'
        $names | Should -Contain 'ERPNextFolder'
        $names | Should -Not -Contain 'OdooModel'
    }
}

Describe 'Policy templates' {
    It 'are up to date with the schema' {
        $out = Join-Path $TestDrive 'policies'
        & (Join-Path $script:RepoRoot 'build/New-PolicyTemplate.ps1') -OutputDir $out | Out-Null
        $committed = Join-Path $script:RepoRoot 'policies'
        foreach ($relative in 'ErpPrinter.admx', 'en-US/ErpPrinter.adml') {
            $expected = [IO.File]::ReadAllText((Join-Path $out $relative))
            $actual = [IO.File]::ReadAllText((Join-Path $committed $relative))
            $actual | Should -BeExactly $expected -Because "run ./build/build.ps1 -Task Generate after changing the schema ($relative)"
        }
    }

    It 'include an up-to-date settings reference' {
        $out = Join-Path $TestDrive 'settings.md'
        & (Join-Path $script:RepoRoot 'build/New-SettingsReference.ps1') -OutputPath $out | Out-Null
        $expected = [IO.File]::ReadAllText($out)
        $actual = [IO.File]::ReadAllText((Join-Path $script:RepoRoot 'docs/settings.md')) -replace "`r`n", "`n"
        $actual | Should -BeExactly $expected -Because 'run ./build/build.ps1 -Task Generate after changing the schema'
    }

    It 'produce well-formed XML' {
        foreach ($relative in 'ErpPrinter.admx', 'en-US/ErpPrinter.adml') {
            { [xml](Get-Content -Raw (Join-Path $script:RepoRoot "policies/$relative")) } | Should -Not -Throw
        }
    }
}

Describe 'Installer source' {
    It 'is well-formed XML with a fixed UpgradeCode' {
        [xml]$wxs = Get-Content -Raw (Join-Path $script:RepoRoot 'installer/ErpPrinter.wxs')
        $wxs.Wix.Package.UpgradeCode | Should -Match '^[0-9A-F]{8}(-[0-9A-F]{4}){3}-[0-9A-F]{12}$'
    }
}
