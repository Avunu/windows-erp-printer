BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
    $script:Checker = Join-Path $script:RepoRoot 'build/Test-CommitMessage.ps1'

    function Test-Message([string] $Message) {
        $output = & $script:Checker -Message $Message
        [pscustomobject]@{ Ok = ($LASTEXITCODE -eq 0); Output = ($output -join "`n") }
    }
}

Describe 'Test-CommitMessage.ps1' {
    It 'accepts <Message>' -TestCases @(
        @{ Message = 'feat(odoo): map users by UPN' }
        @{ Message = 'fix!: drop the JSON-RPC backend' }
        @{ Message = 'chore(main): release 0.2.0' }
        @{ Message = 'docs: explain ADMX templates' }
        @{ Message = "feat: x`n`nBody may mention <details> freely." }
        @{ Message = 'Merge pull request #12 from Avunu/feature' }
        @{ Message = 'Revert "feat: something"' }
        @{ Message = 'fixup! feat: something' }
        @{ Message = "# comment line`nci: build on tags" }
    ) {
        param($Message)
        $result = Test-Message $Message
        $result.Ok | Should -BeTrue -Because $result.Output
    }

    It 'rejects <Case>' -TestCases @(
        @{ Case = 'a free-form header'; Message = 'Update stuff'; Expected = '*not a conventional commit*' }
        @{ Case = 'an upper-case type'; Message = 'FEAT: add thing'; Expected = '*not a conventional commit*' }
        @{ Case = 'an unknown type'; Message = 'feature: add thing'; Expected = "*Unknown type 'feature'*" }
        @{ Case = 'a missing space'; Message = 'fix:thing'; Expected = '*not a conventional commit*' }
        @{ Case = 'a long header'; Message = 'fix: ' + ('x' * 100); Expected = '*keep it to 100*' }
        @{ Case = 'an all-caps subject'; Message = 'feat: ADD ALL THE THINGS'; Expected = '*upper case*' }
        @{ Case = 'a tag in the subject'; Message = 'feat: support <picture> elements'; Expected = '*<picture>*' }
        @{ Case = 'a tag in a breaking change note'; Message = "feat: x`n`nBREAKING CHANGE: drops <details> support"; Expected = '*BREAKING CHANGE note contains <details>*' }
        @{ Case = 'an empty message'; Message = "`n# only a comment"; Expected = '*empty*' }
    ) {
        param($Message, $Expected)
        $result = Test-Message $Message
        $result.Ok | Should -BeFalse
        $result.Output | Should -BeLike $Expected
    }
}

Describe 'release-please configuration' {
    BeforeAll {
        $script:Config = Get-Content -Raw (Join-Path $script:RepoRoot 'release-please-config.json') | ConvertFrom-Json
        $script:Package = $script:Config.packages.'.'
    }

    It 'tags releases as vX.Y.Z, which the asset build expects' {
        $script:Config.'include-v-in-tag' | Should -BeTrue
        $script:Config.'include-component-in-tag' | Should -BeFalse
    }

    It 'marks the version line in every extra file' {
        foreach ($file in $script:Package.'extra-files') {
            $path = Join-Path $script:RepoRoot $file
            Test-Path $path | Should -BeTrue -Because "$file is listed in extra-files"
            @(Select-String -Path $path -Pattern "'\d+\.\d+\.\d+' # x-release-please-version").Count | Should -Be 1 -Because $file
        }
    }

    It 'keeps the module manifest and build info on the same version' {
        $moduleVersion = (Import-PowerShellDataFile (Join-Path $script:RepoRoot 'src/Modules/ErpPrinter/ErpPrinter.psd1')).ModuleVersion
        (Import-PowerShellDataFile (Join-Path $script:RepoRoot 'src/Modules/ErpPrinter/BuildInfo.psd1')).Version | Should -Be $moduleVersion
    }

    It 'has a manifest that is valid JSON' {
        { Get-Content -Raw (Join-Path $script:RepoRoot '.release-please-manifest.json') | ConvertFrom-Json } | Should -Not -Throw
    }
}
