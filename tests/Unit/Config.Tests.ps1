BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
    Import-ErpTestModule
    $script:Root = 'HKLM:\SOFTWARE\ErpPrinter'
    $script:Policy = 'HKLM:\SOFTWARE\Policies\ErpPrinter'
}

Describe 'Get-ErpPrinterConfig' {
    BeforeEach { Initialize-FakeRegistry }

    It 'returns schema defaults when nothing is configured' {
        $config = Get-ErpPrinterConfig
        $config.Global.PollIntervalSeconds | Should -Be 15
        $config.Global.AutoUpdate | Should -BeTrue
        $config.Global.LogLevel | Should -Be 'Information'
        $config.Global.DataRoot | Should -Match 'ErpPrinter$'
        $config.Profiles | Should -BeNullOrEmpty
    }

    It 'lets machine values override defaults' {
        Set-FakeRegistryValue $script:Root PollIntervalSeconds 60
        Set-FakeRegistryValue $script:Root AutoUpdate 0
        $config = Get-ErpPrinterConfig
        $config.Global.PollIntervalSeconds | Should -Be 60
        $config.Global.AutoUpdate | Should -BeFalse
        $config.Global.ManagedSettings | Should -BeNullOrEmpty
    }

    It 'lets policy values override machine values and reports them as managed' {
        Set-FakeRegistryValue $script:Root PollIntervalSeconds 60
        Set-FakeRegistryValue $script:Policy PollIntervalSeconds 120
        Set-FakeRegistryValue $script:Policy LogLevel 'Verbose'
        $config = Get-ErpPrinterConfig
        $config.Global.PollIntervalSeconds | Should -Be 120
        $config.Global.LogLevel | Should -Be 'Verbose'
        $config.Global.ManagedSettings | Should -Contain 'PollIntervalSeconds'
        $config.Global.ManagedSettings | Should -Contain 'LogLevel'
    }

    It 'ignores invalid registry values' {
        Set-FakeRegistryValue $script:Root LogLevel 'Chatty'
        Set-FakeRegistryValue $script:Root PollIntervalSeconds 1
        $config = Get-ErpPrinterConfig -WarningAction SilentlyContinue
        $config.Global.LogLevel | Should -Be 'Information'
        $config.Global.PollIntervalSeconds | Should -Be 15
    }

    It 'lists machine and policy-only profiles with derived defaults' {
        Set-FakeRegistryValue "$script:Root\Printers\Send to Odoo" ServerUrl 'https://acme.odoo.com'
        Set-FakeRegistryValue "$script:Policy\Printers\Invoices" ServerUrl 'https://erp.example.com'
        Set-FakeRegistryValue "$script:Policy\Printers\Invoices" Backend 'ERPNext'
        $profiles = @((Get-ErpPrinterConfig).Profiles)
        $profiles.Count | Should -Be 2

        $odoo = $profiles | Where-Object Name -eq 'Send to Odoo'
        $odoo.PrinterName | Should -Be 'Send to Odoo'
        $odoo.PipeName | Should -Be 'ErpPrinter-Send_to_Odoo'
        $odoo.Backend | Should -Be 'OdooJson2'
        $odoo.PolicyDefined | Should -BeFalse

        $invoices = $profiles | Where-Object Name -eq 'Invoices'
        $invoices.Backend | Should -Be 'ERPNext'
        $invoices.PolicyDefined | Should -BeTrue
        $invoices.ManagedSettings | Should -Contain 'ServerUrl'
    }
}

Describe 'Set-ErpPrinterSetting' {
    BeforeEach { Initialize-FakeRegistry }

    It 'stores booleans and integers as DWORD and strings as REG_SZ' {
        Set-ErpPrinterSetting -Name AutoUpdate -Value $false
        Set-ErpPrinterSetting -Name MaxAttempts -Value '25'
        Set-ErpPrinterSetting -Name ProxyUrl -Value 'http://proxy:8080'
        $script:FakeRegistry[$script:Root].AutoUpdate | Should -Be 0
        $script:FakeKinds["$script:Root|AutoUpdate"] | Should -Be 'DWord'
        $script:FakeRegistry[$script:Root].MaxAttempts | Should -Be 25
        $script:FakeKinds["$script:Root|ProxyUrl"] | Should -Be 'String'
    }

    It 'removes values that equal the default' {
        Set-FakeRegistryValue $script:Root PollIntervalSeconds 60
        Set-ErpPrinterSetting -Name PollIntervalSeconds -Value 15
        $script:FakeRegistry[$script:Root].ContainsKey('PollIntervalSeconds') | Should -BeFalse
    }

    It 'always stores the backend of a profile explicitly' {
        Set-ErpPrinterSetting -ProfileName 'P1' -Name Backend -Value 'OdooJson2'
        $script:FakeRegistry["$script:Root\Printers\P1"].Backend | Should -Be 'OdooJson2'
    }

    It 'keeps an all-defaults profile key' {
        Set-ErpPrinterSetting -ProfileName 'P1' -Name FileNameTemplate -Value '{title}'
        $script:FakeRegistry.ContainsKey("$script:Root\Printers\P1") | Should -BeTrue
    }

    It 'rejects unknown settings, secrets and out-of-range values' {
        { Set-ErpPrinterSetting -Name Nope -Value 1 } | Should -Throw '*Unknown*'
        { Set-ErpPrinterSetting -ProfileName 'P1' -Name ApiKey -Value 'x' } | Should -Throw '*Set-ErpPrinterSecret*'
        { Set-ErpPrinterSetting -Name PollIntervalSeconds -Value 0 } | Should -Throw '*between*'
        { Set-ErpPrinterSetting -Name LogLevel -Value 'Loud' } | Should -Throw '*one of*'
    }

    It 'clears a value with -Clear' {
        Set-FakeRegistryValue $script:Root ProxyUrl 'http://p'
        Set-ErpPrinterSetting -Name ProxyUrl -Clear
        $script:FakeRegistry[$script:Root].ContainsKey('ProxyUrl') | Should -BeFalse
    }
}

Describe 'Printer profiles and secrets' {
    BeforeEach { Initialize-FakeRegistry }

    It 'creates a profile with settings and encrypted secrets' {
        New-ErpPrinterProfile -Name 'Send to Odoo' -Settings @{ ServerUrl = 'https://acme.odoo.com'; OdooFolderId = 7; ApiKey = 's3cret' }
        $key = "$script:Root\Printers\Send to Odoo"
        $script:FakeRegistry[$key].ServerUrl | Should -Be 'https://acme.odoo.com'
        $script:FakeRegistry[$key].OdooFolderId | Should -Be 7
        $script:FakeRegistry[$key].Backend | Should -Be 'OdooJson2'
        $script:FakeRegistry[$key].ContainsKey('ApiKey') | Should -BeFalse
        $script:FakeKinds["$script:Root\Secrets\Send to Odoo|ApiKey"] | Should -Be 'Binary'
        Test-ErpPrinterSecret -ProfileName 'Send to Odoo' -Name ApiKey | Should -BeTrue
        InModuleScope ErpPrinter { (Get-ErpProfileSecrets -ProfileName 'Send to Odoo').ApiKey } | Should -Be 's3cret'
    }

    It 'refuses to overwrite a profile unless forced' {
        New-ErpPrinterProfile -Name 'A'
        { New-ErpPrinterProfile -Name 'A' } | Should -Throw '*already exists*'
        { New-ErpPrinterProfile -Name 'A' -Force -Settings @{ ServerUrl = 'https://x' } } | Should -Not -Throw
    }

    It 'rejects invalid profile names' {
        { New-ErpPrinterProfile -Name 'bad\name' } | Should -Throw '*Invalid printer profile name*'
        { New-ErpPrinterProfile -Name '' } | Should -Throw
    }

    It 'removes a profile together with its secrets' {
        New-ErpPrinterProfile -Name 'A' -Settings @{ ApiKey = 'k' }
        Remove-ErpPrinterProfile -Name 'A' -Confirm:$false
        $script:FakeRegistry.ContainsKey("$script:Root\Printers\A") | Should -BeFalse
        $script:FakeRegistry.ContainsKey("$script:Root\Secrets\A") | Should -BeFalse
    }

    It 'clears a secret when given an empty value' {
        Set-ErpPrinterSecret -ProfileName 'A' -Name ApiKey -Value 'k'
        Set-ErpPrinterSecret -ProfileName 'A' -Name ApiKey -Value ''
        Test-ErpPrinterSecret -ProfileName 'A' -Name ApiKey | Should -BeFalse
    }

    It 'accepts a SecureString' {
        $secure = New-Object Security.SecureString
        'abc'.ToCharArray() | ForEach-Object { $secure.AppendChar($_) }
        Set-ErpPrinterSecret -ProfileName 'A' -Name ApiSecret -Value $secure
        InModuleScope ErpPrinter { (Get-ErpProfileSecrets -ProfileName 'A').ApiSecret } | Should -Be 'abc'
    }
}

Describe 'Test-ErpPrinterProfile' {
    BeforeAll {
        function New-Candidate([hashtable] $Overrides = @{}) {
            $p = InModuleScope ErpPrinter { Resolve-ErpSettings -Schema $script:ErpProfileSchema -Context @{ Name = 'P' } }
            $p['Name'] = 'P'
            $p['ServerUrl'] = 'https://erp.example.com'
            foreach ($k in $Overrides.Keys) { $p[$k] = $Overrides[$k] }
            $p
        }
    }
    BeforeEach { Initialize-FakeRegistry }

    It 'accepts a complete Odoo profile' {
        Test-ErpPrinterProfile -PrinterProfile (New-Candidate) -Secrets @{ ApiKey = 'k' } | Should -BeNullOrEmpty
    }

    It 'uses the stored secret when none is passed' {
        Set-ErpPrinterSecret -ProfileName 'P' -Name ApiKey -Value 'k'
        Test-ErpPrinterProfile -PrinterProfile (New-Candidate) | Should -BeNullOrEmpty
    }

    It 'reports <Case>' -TestCases @(
        @{ Case = 'missing URL'; Overrides = @{ ServerUrl = '' }; Secrets = @{ ApiKey = 'k' }; Expected = '*Server URL is required*' }
        @{ Case = 'non-http URL'; Overrides = @{ ServerUrl = 'ftp://x' }; Secrets = @{ ApiKey = 'k' }; Expected = '*not an http(s) URL*' }
        @{ Case = 'missing API key'; Overrides = @{}; Secrets = @{}; Expected = '*API key is required*' }
        @{ Case = 'ERPNext without secret'; Overrides = @{ Backend = 'ERPNext' }; Secrets = @{ ApiKey = 'k' }; Expected = '*API secret*' }
        @{ Case = 'JSON-RPC without login'; Overrides = @{ Backend = 'OdooJsonRpc' }; Secrets = @{ ApiKey = 'k' }; Expected = '*login*' }
        @{ Case = 'bad extra fields'; Overrides = @{ OdooExtraFields = '{nope' }; Secrets = @{ ApiKey = 'k' }; Expected = '*JSON object*' }
        @{ Case = 'half an attachment target'; Overrides = @{ OdooModel = 'ir.attachment'; OdooResModel = 'res.partner' }; Secrets = @{ ApiKey = 'k' }; Expected = '*Attach to model*' }
        @{ Case = 'bad pipe name'; Overrides = @{ PipeName = 'a b' }; Secrets = @{ ApiKey = 'k' }; Expected = '*Pipe name*' }
    ) {
        param($Overrides, $Secrets, $Expected)
        $problems = Test-ErpPrinterProfile -PrinterProfile (New-Candidate $Overrides) -Secrets $Secrets
        ($problems -join "`n") | Should -BeLike $Expected
    }

    It 'allows a webhook without a key' {
        Test-ErpPrinterProfile -PrinterProfile (New-Candidate @{ Backend = 'Webhook' }) | Should -BeNullOrEmpty
    }

    It 'detects printer and pipe name clashes' {
        $a = New-Candidate
        $b = New-Candidate
        $b['Name'] = 'Other'
        $problems = Test-ErpPrinterProfile -PrinterProfile $a -Secrets @{ ApiKey = 'k' } -AllProfiles @($a, $b)
        ($problems -join "`n") | Should -BeLike "*Printer name 'P' is also used by profile 'Other'*"
        ($problems -join "`n") | Should -BeLike '*Pipe name*also used*'
    }
}
