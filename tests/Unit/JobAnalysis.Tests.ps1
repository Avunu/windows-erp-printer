BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')

    # The Windows printing cmdlets do not exist on every platform; Pester can only mock commands that resolve.
    foreach ($name in 'Get-Printer', 'Get-PrinterPort', 'Get-PrintJob', 'Get-CimInstance') {
        if (-not (Get-Command $name -ErrorAction SilentlyContinue)) { Set-Item -Path "function:global:$name" -Value { } }
    }
    Import-ErpTestModule

    function New-SpoolerEvent([int] $Id, [string] $Message, [DateTime] $Time = (Get-Date)) {
        [pscustomobject]@{ Id = $Id; Message = $Message; TimeCreated = $Time; Level = 4; LevelDisplayName = 'Information' }
    }
}

Describe 'Spooler stage verdict' {
    BeforeAll {
        function Get-StageVerdict([int[]] $Ids, [string] $Code = '') {
            InModuleScope ErpPrinter -Parameters @{ Ids = $Ids; Code = $Code } {
                param($Ids, $Code)
                Get-ErpSpoolerStageVerdict -EventIds $Ids -ProcessorCode $Code
            }
        }
    }

    It 'treats a job that was written to the port as printed' {
        $v = Get-StageVerdict 800, 801, 842, 805, 307 '0x0'
        $v.Stage | Should -Be 'Printed'
        $v.Failed | Should -BeFalse
    }

    It 'blames the conversion, not the pipe, when printing started but the print processor never finished' {
        $v = Get-StageVerdict 800, 801
        $v.Stage | Should -Be 'Printing'
        $v.Failed | Should -BeTrue
        $v.Summary | Should -BeLike '*before the ERP Printer pipe was involved*'
        $v.Hint | Should -BeLike '*Microsoft Print to PDF*'
        $v.Hint | Should -BeLike '*Ctrl+Shift+P*'
    }

    It 'reports a print processor error code' {
        $v = Get-StageVerdict 800, 801, 842 '0x5'
        $v.Stage | Should -Be 'ProcessorFailed'
        $v.Summary | Should -BeLike '*code 0x5*'
    }

    It 'points at the pipe when the job was rendered but never written to the port' {
        $v = Get-StageVerdict 800, 801, 842, 805 '0x0'
        $v.Stage | Should -Be 'Rendered'
        $v.Hint | Should -BeLike '*pipe*'
    }

    It 'reports a job that was never started' {
        (Get-StageVerdict 800).Stage | Should -Be 'Spooled'
    }

    It 'admits when the log has nothing' {
        $v = Get-StageVerdict @()
        $v.Stage | Should -Be 'Unknown'
        $v.Failed | Should -BeFalse
    }
}

Describe 'Job analysis' {
    It 'reproduces the diagnosis of a job that died in the print processor' {
        $t = [DateTime]'2026-10-20 17:27:35'
        Mock -ModuleName ErpPrinter Get-ErpPrintServiceEvents {
            @(
                (New-SpoolerEvent 307 'Document 3, Print Document owned by jdoe on \\PC was printed on Send to Odoo through port \\.\pipe\x. Size in bytes: 92349.' $t)
                (New-SpoolerEvent 800 'Spooling job 4.' $t)
                (New-SpoolerEvent 801 'Printing job 4.' $t.AddSeconds(5))
            )
        }
        Mock -ModuleName ErpPrinter Get-CimInstance { [pscustomobject]@{ JobId = 4; Datatype = 'RAW' } }
        Mock -ModuleName ErpPrinter Get-ErpSpoolFileFormat { 'TYPE_XPS_OPEN' }
        $job = [pscustomobject]@{ Id = 4; DocumentName = 'Wikipedia, the free encyclopedia'; Size = 1002815; TotalPages = 4; JobStatus = 'Error, Printing, Retained'; SubmittedTime = $t }
        $analysis = InModuleScope ErpPrinter -Parameters @{ Job = $job } { param($Job) Get-ErpJobAnalysis -Job $Job }
        $analysis.Stage | Should -Be 'Printing'
        $analysis.Failed | Should -BeTrue
        $analysis.Detail | Should -BeLike "*job 4 'Wikipedia, the free encyclopedia' (979 KB, 4 page(s)*"
        $analysis.Detail | Should -BeLike '*datatype RAW*'
        $analysis.Detail | Should -BeLike '*spool file TYPE_XPS_OPEN*'
        $analysis.Detail | Should -BeLike '*17:27:35 spooled -> 17:27:40 printing started*'
        # Job 3's events belong to another job and must not leak into this timeline.
        $analysis.Detail | Should -Not -BeLike '*written to port*'
    }
}

Describe 'Profile checks with jobs in the queue' {
    It 'analyses a failed job instead of throwing (regression: -or after a command call)' {
        $settings = InModuleScope ErpPrinter { Resolve-ErpSettings -Schema $script:ErpGlobalSchema }
        $settings['DataRoot'] = Join-Path $TestDrive 'data'
        $config = [pscustomobject]@{ Global = $settings; Profiles = @() }
        $printerProfile = @{ Name = 'Send to Odoo'; Enabled = $true; PrinterName = 'Send to Odoo'; PipeName = 'p' }
        Mock -ModuleName ErpPrinter Get-Printer { [pscustomobject]@{ Name = 'Send to Odoo'; PortName = '\\.\pipe\p'; DriverName = 'Microsoft Print To PDF'; PrinterStatus = 'Normal' } }
        Mock -ModuleName ErpPrinter Get-PrinterPort { [pscustomobject]@{ Name = '\\.\pipe\p' } }
        Mock -ModuleName ErpPrinter Get-ErpPrinterStateInfo { [pscustomobject]@{ Problem = $null; Hint = ''; Summary = 'Normal' } }
        Mock -ModuleName ErpPrinter Test-ErpPipeExists { $true }
        Mock -ModuleName ErpPrinter Get-PrintJob { [pscustomobject]@{ Id = 4; DocumentName = 'Edge page'; UserName = 'jdoe'; JobStatus = 'Error, Printing'; SubmittedTime = (Get-Date) } }
        Mock -ModuleName ErpPrinter Get-ErpJobAnalysis { [pscustomobject]@{ Stage = 'Printing'; Failed = $true; Detail = 'job 4 analysis text'; Hint = 'do this' } }
        $results = InModuleScope ErpPrinter -Parameters @{ Config = $config; P = $printerProfile } {
            param($Config, $P)
            Get-ErpProfileChecks -Config $Config -PrinterProfile $P
        }
        $queue = $results | Where-Object Name -eq 'Print queue'
        $queue.Status | Should -Be 'Fail'
        $job = $results | Where-Object Name -eq 'Job 4 analysis'
        $job.Status | Should -Be 'Fail'
        $job.Detail | Should -Be 'job 4 analysis text'
        $job.Hint | Should -Be 'do this'
    }
}
