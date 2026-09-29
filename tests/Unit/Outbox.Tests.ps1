BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
    Import-ErpTestModule

    function New-TestOutbox([string] $Root) {
        InModuleScope ErpPrinter -Parameters @{ Root = $Root } {
            param($Root)
            $paths = Get-ErpProfilePaths -DataRoot $Root -ProfileName 'Send to Odoo'
            Initialize-ErpProfilePaths $paths
            $paths
        }
    }

    function Add-TestItem([hashtable] $Paths, [string] $Id = ([guid]::NewGuid().ToString('N')), [int] $ExtraBytes = 0) {
        New-TestPdf -Path (Join-Path $Paths.Outbox "$Id.pdf") -ExtraBytes $ExtraBytes | Out-Null
        InModuleScope ErpPrinter -Parameters @{ Paths = $Paths; Id = $Id } {
            param($Paths, $Id)
            $meta = New-ErpOutboxMeta -Id $Id -ProfileName 'Send to Odoo' -User 'CORP\jdoe' -Title 'Invoice 7' -JobId '3' -Pages 1 -SizeBytes 40
            Save-ErpOutboxMeta -Folder $Paths.Outbox -Meta $meta
        }
        $Id
    }

    function New-TestSettings([hashtable] $Overrides = @{}) {
        $g = InModuleScope ErpPrinter { Resolve-ErpSettings -Schema $script:ErpGlobalSchema }
        foreach ($k in $Overrides.Keys) { $g[$k] = $Overrides[$k] }
        $g
    }

    function Invoke-SendItem([hashtable] $Paths, [string] $Id, $GlobalSettings, [bool] $Enabled = $true) {
        InModuleScope ErpPrinter -Parameters @{ Paths = $Paths; Id = $Id; G = $GlobalSettings; Enabled = $Enabled } {
            param($Paths, $Id, $G, $Enabled)
            $item = Get-ErpOutboxItems -Folder $Paths.Outbox | Where-Object { $_.Meta.id -eq $Id }
            $printerProfile = @{ Name = 'Send to Odoo'; Backend = 'OdooJson2'; Enabled = $Enabled; FileNameTemplate = '{title}' }
            Send-ErpOutboxItem -Item $item -PrinterProfile $printerProfile -GlobalSettings $G -Paths $Paths -WarningAction SilentlyContinue
        }
    }

    function Get-Meta([string] $Folder, [string] $Id) {
        Get-Content -Raw (Join-Path $Folder "$Id.json") | ConvertFrom-Json
    }
}

Describe 'Outbox files' {
    It 'derives per-profile folders with a safe name' {
        $paths = New-TestOutbox $TestDrive
        $paths.Outbox | Should -BeLike '*spool*Send_to_Odoo*outbox'
        Test-Path $paths.Failed | Should -BeTrue
    }

    It 'round-trips sidecar metadata' {
        $paths = New-TestOutbox (Join-Path $TestDrive 'rt')
        $id = Add-TestItem $paths
        $items = @(InModuleScope ErpPrinter -Parameters @{ F = $paths.Outbox } { param($F) Get-ErpOutboxItems -Folder $F })
        $items.Count | Should -Be 1
        $items[0].Meta.user | Should -Be 'CORP\jdoe'
        $items[0].Meta.attempts | Should -Be 0
        $items[0].PdfPath | Should -Be (Join-Path $paths.Outbox "$id.pdf")
    }

    It 'skips items whose retry time has not come' {
        $paths = New-TestOutbox (Join-Path $TestDrive 'due')
        $id = Add-TestItem $paths
        $meta = Get-Meta $paths.Outbox $id
        $meta.nextAttempt = (Get-Date).AddMinutes(10).ToString('o')
        $meta | ConvertTo-Json | Set-Content (Join-Path $paths.Outbox "$id.json")
        @(InModuleScope ErpPrinter -Parameters @{ F = $paths.Outbox } { param($F) Get-ErpOutboxItems -Folder $F -DueOnly }).Count | Should -Be 0
        @(InModuleScope ErpPrinter -Parameters @{ F = $paths.Outbox } { param($F) Get-ErpOutboxItems -Folder $F -DueOnly -Now (Get-Date).AddMinutes(11) }).Count | Should -Be 1
    }

    It 'adopts orphaned PDFs older than five minutes' {
        $paths = New-TestOutbox (Join-Path $TestDrive 'orphan')
        $pdf = New-TestPdf (Join-Path $paths.Outbox 'lost.pdf')
        (Get-Item $pdf).LastWriteTime = (Get-Date).AddMinutes(-10)
        New-TestPdf (Join-Path $paths.Outbox 'fresh.pdf') | Out-Null
        InModuleScope ErpPrinter -Parameters @{ F = $paths.Outbox } { param($F) Repair-ErpOrphanedPdfs -Folder $F -ProfileName 'Send to Odoo' -WarningAction SilentlyContinue }
        Test-Path (Join-Path $paths.Outbox 'lost.json') | Should -BeTrue
        Test-Path (Join-Path $paths.Outbox 'fresh.json') | Should -BeFalse
        (Get-Meta $paths.Outbox 'lost').title | Should -Be 'Recovered print job'
    }
}

Describe 'Get-ErpRetryDelaySeconds' {
    It 'backs off exponentially up to the cap' {
        InModuleScope ErpPrinter {
            @(1..8 | ForEach-Object { Get-ErpRetryDelaySeconds -Attempt $_ -MaxMinutes 60 }) |
                Should -Be @(30, 60, 120, 240, 480, 960, 1920, 3600)
            Get-ErpRetryDelaySeconds -Attempt 500 -MaxMinutes 5 | Should -Be 300
        }
    }
}

Describe 'Send-ErpOutboxItem' {
    BeforeEach {
        $script:Paths = New-TestOutbox (Join-Path $TestDrive ([guid]::NewGuid().ToString('N')))
        $script:Id = Add-TestItem $script:Paths
    }

    It 'deletes the item after a successful delivery' {
        Mock -ModuleName ErpPrinter Send-ErpDocument { 'remote-42' }
        Invoke-SendItem $script:Paths $script:Id (New-TestSettings) | Should -Be 'Sent'
        @(Get-ChildItem $script:Paths.Outbox).Count | Should -Be 0
        @(Get-ChildItem $script:Paths.Sent).Count | Should -Be 0
    }

    It 'archives delivered items when KeepSentDays is set' {
        Mock -ModuleName ErpPrinter Send-ErpDocument { 'remote-42' }
        Invoke-SendItem $script:Paths $script:Id (New-TestSettings @{ KeepSentDays = 7 }) | Should -Be 'Sent'
        $meta = Get-Meta $script:Paths.Sent $script:Id
        $meta.remoteId | Should -Be 'remote-42'
        $meta.sentAt | Should -Not -BeNullOrEmpty
        Test-Path (Join-Path $script:Paths.Sent "$script:Id.pdf") | Should -BeTrue
    }

    It 'schedules a retry on transient errors' {
        Mock -ModuleName ErpPrinter Send-ErpDocument { throw 'Could not reach server' }
        Invoke-SendItem $script:Paths $script:Id (New-TestSettings) | Should -Be 'Retry'
        $meta = Get-Meta $script:Paths.Outbox $script:Id
        $meta.attempts | Should -Be 1
        $meta.lastError | Should -BeLike '*Could not reach*'
        $meta.nextAttempt | Should -Not -BeNullOrEmpty
    }

    It 'moves the item to failed on permanent errors' {
        Mock -ModuleName ErpPrinter Send-ErpDocument {
            $ex = New-Object System.Exception 'HTTP 400: bad field'
            $ex.Data['ErpPermanent'] = $true
            throw $ex
        }
        Invoke-SendItem $script:Paths $script:Id (New-TestSettings) | Should -Be 'Failed'
        (Get-Meta $script:Paths.Failed $script:Id).lastError | Should -BeLike '*bad field*'
        Test-Path (Join-Path $script:Paths.Failed "$script:Id.pdf") | Should -BeTrue
        @(Get-ChildItem $script:Paths.Outbox).Count | Should -Be 0
    }

    It 'gives up after MaxAttempts' {
        Mock -ModuleName ErpPrinter Send-ErpDocument { throw 'timeout' }
        $settings = New-TestSettings @{ MaxAttempts = 2 }
        Invoke-SendItem $script:Paths $script:Id $settings | Should -Be 'Retry'
        Invoke-SendItem $script:Paths $script:Id $settings | Should -Be 'Failed'
        (Get-Meta $script:Paths.Failed $script:Id).attempts | Should -Be 2
    }

    It 'rejects documents over the size limit without calling the backend' {
        Mock -ModuleName ErpPrinter Send-ErpDocument { 'x' }
        $big = Add-TestItem $script:Paths -ExtraBytes (1MB + 10)
        Invoke-SendItem $script:Paths $big (New-TestSettings @{ MaxDocumentSizeMB = 1 }) | Should -Be 'Failed'
        Should -Invoke -ModuleName ErpPrinter Send-ErpDocument -Times 0 -Exactly
    }

    It 'keeps items of disabled profiles for later' {
        Mock -ModuleName ErpPrinter Send-ErpDocument { 'x' }
        Invoke-SendItem $script:Paths $script:Id (New-TestSettings) -Enabled $false | Should -Be 'Retry'
        Should -Invoke -ModuleName ErpPrinter Send-ErpDocument -Times 0 -Exactly
    }
}

Describe 'Restore-ErpPrinterFailedDocument' {
    It 'moves failed items back with a reset attempt counter' {
        Initialize-FakeRegistry
        $root = Join-Path $TestDrive 'restore'
        Set-FakeRegistryValue 'HKLM:\SOFTWARE\ErpPrinter' DataRoot $root
        Set-FakeRegistryValue 'HKLM:\SOFTWARE\ErpPrinter\Printers\Send to Odoo' ServerUrl 'https://x'
        $paths = New-TestOutbox $root
        $id = Add-TestItem $paths
        Move-Item (Join-Path $paths.Outbox "$id.*") $paths.Failed
        $meta = Get-Meta $paths.Failed $id
        $meta.attempts = 9
        $meta | ConvertTo-Json | Set-Content (Join-Path $paths.Failed "$id.json")
        Mock -ModuleName ErpPrinter Get-ErpOutboxSignal { [pscustomobject]@{} | Add-Member -MemberType ScriptMethod -Name Set -Value { $true } -PassThru }

        Restore-ErpPrinterFailedDocument -Confirm:$false | Should -Be 1
        (Get-Meta $paths.Outbox $id).attempts | Should -Be 0
        Test-Path (Join-Path $paths.Outbox "$id.pdf") | Should -BeTrue
    }
}
