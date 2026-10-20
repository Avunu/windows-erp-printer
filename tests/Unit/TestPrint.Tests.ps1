BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')

    # The Windows printing cmdlets do not exist on every platform; Pester can only mock commands that resolve.
    foreach ($name in 'Get-Printer', 'Get-PrintJob', 'Get-WinEvent') {
        if (-not (Get-Command $name -ErrorAction SilentlyContinue)) { Set-Item -Path "function:global:$name" -Value { } }
    }
    Import-ErpTestModule
}

AfterAll {
    InModuleScope ErpPrinter { $script:ErpLogSettings = $null; $script:ErpLogComponent = 'module' }
}

Describe 'Invoke-ErpPrinterTestPrint' {
    BeforeEach {
        $script:Root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $script:PollCount = 0
        $script:SentToken = $null
        $script:Port = '\\.\pipe\ErpPrinter-Test'
        $settings = InModuleScope ErpPrinter { Resolve-ErpSettings -Schema $script:ErpGlobalSchema }
        $settings['DataRoot'] = $script:Root
        $script:Config = [pscustomobject]@{
            Global   = $settings
            Profiles = @(@{ Name = 'Send to Odoo'; Enabled = $true; PrinterName = 'Send to Odoo'; PipeName = 'ErpPrinter-Test' })
        }
        $script:Outbox = InModuleScope ErpPrinter -Parameters @{ Root = $script:Root } {
            param($Root)
            $paths = Get-ErpProfilePaths -DataRoot $Root -ProfileName 'Send to Odoo'
            Initialize-ErpProfilePaths $paths
            $paths.Outbox
        }
        InModuleScope ErpPrinter { $script:ErpTestPrintStuckSeconds = 1 }

        Mock -ModuleName ErpPrinter Get-ErpPrinterConfig { $script:Config }
        Mock -ModuleName ErpPrinter Get-Printer { [pscustomobject]@{ Name = 'Send to Odoo'; PortName = $script:Port; DriverName = 'Microsoft Print To PDF' } }
        Mock -ModuleName ErpPrinter Get-ErpTaskState { 'Running' }
        Mock -ModuleName ErpPrinter Test-ErpPipeExists { $true }
        Mock -ModuleName ErpPrinter Get-WinEvent { [pscustomobject]@{ IsEnabled = $true } }
        Mock -ModuleName ErpPrinter Get-ErpPrintServiceEvents { @() }
        Mock -ModuleName ErpPrinter Get-ErpRecentLogEntries { @() }
        Mock -ModuleName ErpPrinter Get-PrintJob { $null }
        Mock -ModuleName ErpPrinter Start-Sleep { [Threading.Thread]::Sleep(50) }
    }

    It 'follows a page from the queue through the listener to delivery' {
        Mock -ModuleName ErpPrinter Send-ErpTestPage {
            $script:SentToken = $Token
            InModuleScope ErpPrinter -Parameters @{ Outbox = $script:Outbox; Token = $Token } {
                param($Outbox, $Token)
                $id = [guid]::NewGuid().ToString('N')
                [IO.File]::WriteAllText((Join-Path $Outbox "$id.pdf"), '%PDF-1.4')
                Save-ErpOutboxMeta -Folder $Outbox -Meta (New-ErpOutboxMeta -Id $id -ProfileName 'Send to Odoo' -Title "ERP Printer test page [$Token]")
            }
        }
        Mock -ModuleName ErpPrinter Get-PrintJob {
            $script:PollCount++
            # The uploader delivers the document while the job is leaving the queue.
            if ($script:PollCount -eq 3) { Get-ChildItem $script:Outbox | Remove-Item -Force }
            if ($script:PollCount -lt 2) { [pscustomobject]@{ Id = 7; DocumentName = "ERP Printer test page [$($script:SentToken)]"; JobStatus = 'Printing' } }
        }
        $result = Invoke-ErpPrinterTestPrint -ProfileName 'Send to Odoo' -TimeoutSeconds 20
        $result.Success | Should -BeTrue
        $result.Stage | Should -Be 'Delivered'
        $result.Report | Should -BeLike '*Result: SUCCESS*'
        $result.Report | Should -BeLike '*in the print queue*'
        $result.Report | Should -BeLike '*listener received it*'
    }

    It 'stops early and names the problem when the job is stuck and nothing hosts the pipe' {
        Mock -ModuleName ErpPrinter Send-ErpTestPage { $script:SentToken = $Token }
        Mock -ModuleName ErpPrinter Test-ErpPipeExists { $false }
        Mock -ModuleName ErpPrinter Get-PrintJob { [pscustomobject]@{ Id = 9; DocumentName = "ERP Printer test page [$($script:SentToken)]"; JobStatus = 'Error, Offline' } }
        $started = Get-Date
        $result = Invoke-ErpPrinterTestPrint -ProfileName 'Send to Odoo' -TimeoutSeconds 60
        ((Get-Date) - $started).TotalSeconds | Should -BeLessThan 30
        $result.Success | Should -BeFalse
        $result.Stage | Should -Be 'InQueue'
        $result.Summary | Should -BeLike "*stuck as 'Error, Offline'*"
        $result.Summary | Should -BeLike '*Nothing is hosting*'
        $result.Report | Should -BeLike '*Nothing is hosting*'
    }

    It 'reports a spooler that printed to the wrong port' {
        Mock -ModuleName ErpPrinter Send-ErpTestPage { $script:SentToken = $Token }
        Mock -ModuleName ErpPrinter Get-PrintJob {
            $script:PollCount++
            if ($script:PollCount -lt 3) { [pscustomobject]@{ Id = 3; DocumentName = "ERP Printer test page [$($script:SentToken)]"; JobStatus = 'Printing' } }
        }
        Mock -ModuleName ErpPrinter Get-ErpPrintServiceEvents {
            if ($Log -ne 'Operational') { return @() }
            @([pscustomobject]@{
                    Id = 307; Level = 4; LevelDisplayName = 'Information'; TimeCreated = Get-Date
                    Message = "Document 3, ERP Printer test page [$($script:SentToken)] owned by jdoe was printed on Send to Odoo via port FILE:. Size in bytes: 4000. Pages printed: 1. No user action is required."
                })
        }
        $result = Invoke-ErpPrinterTestPrint -ProfileName 'Send to Odoo' -TimeoutSeconds 5
        $result.Success | Should -BeFalse
        $result.Stage | Should -Be 'Spooled'
        $result.Evidence.SpoolerPort | Should -Be 'FILE:'
        $result.Summary | Should -BeLike "*printed to 'FILE:'*"
        $result.Summary | Should -BeLike "*not the listener's pipe*"
    }

    It 'says the printing side works when the listener received it but the upload fails' {
        Mock -ModuleName ErpPrinter Send-ErpTestPage {
            InModuleScope ErpPrinter -Parameters @{ Outbox = $script:Outbox; Token = $Token } {
                param($Outbox, $Token)
                $id = [guid]::NewGuid().ToString('N')
                [IO.File]::WriteAllText((Join-Path $Outbox "$id.pdf"), '%PDF-1.4')
                $meta = New-ErpOutboxMeta -Id $id -ProfileName 'Send to Odoo' -Title "ERP Printer test page [$Token]"
                $meta.attempts = 1
                $meta.lastError = 'Could not reach https://erp.example: no such host'
                Save-ErpOutboxMeta -Folder $Outbox -Meta $meta
            }
        }
        $result = Invoke-ErpPrinterTestPrint -ProfileName 'Send to Odoo' -TimeoutSeconds 20
        $result.Success | Should -BeFalse
        $result.Stage | Should -Be 'Received'
        $result.Summary | Should -BeLike '*no such host*'
    }

    It 'reports a missing printer without printing' {
        Mock -ModuleName ErpPrinter Get-Printer { $null }
        Mock -ModuleName ErpPrinter Send-ErpTestPage { throw 'must not print' }
        $result = Invoke-ErpPrinterTestPrint -ProfileName 'Send to Odoo' -TimeoutSeconds 5
        $result.Success | Should -BeFalse
        $result.Stage | Should -Be 'None'
        $result.Report | Should -BeLike "*does not exist*"
    }

    It 'rejects unknown and disabled profiles' {
        { Invoke-ErpPrinterTestPrint -ProfileName 'Nope' } | Should -Throw "*No printer profile named 'Nope'*"
        $script:Config.Profiles[0].Enabled = $false
        { Invoke-ErpPrinterTestPrint -ProfileName 'Send to Odoo' } | Should -Throw '*disabled*'
    }
}
