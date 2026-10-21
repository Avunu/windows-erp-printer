BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
    Import-ErpTestModule

    function Get-Verdict([hashtable] $Evidence) {
        InModuleScope ErpPrinter -Parameters @{ Evidence = $Evidence } {
            param($Evidence)
            Get-ErpTestPrintVerdict -Evidence $Evidence
        }
    }

    function Use-TestLogs([string] $Root, [string] $Level = 'Information', [string] $Component = 'listener') {
        InModuleScope ErpPrinter -Parameters @{ Root = $Root; Level = $Level; Component = $Component } {
            param($Root, $Level, $Component)
            $g = Resolve-ErpSettings -Schema $script:ErpGlobalSchema
            $g['DataRoot'] = $Root
            $g['LogLevel'] = $Level
            Set-ErpLogContext -Component $Component -GlobalSettings $g -Confirm:$false
        }
    }
}

AfterAll {
    InModuleScope ErpPrinter { $script:ErpLogSettings = $null; $script:ErpLogComponent = 'module' }
}

Describe 'Test print verdict' {
    It 'reports success once the page is delivered' {
        $v = Get-Verdict @{ Submitted = $true; Received = $true; Delivered = $true }
        $v.Success | Should -BeTrue
        $v.Stage | Should -Be 'Delivered'
    }

    It 'blames the ERP when the document was received but rejected' {
        $v = Get-Verdict @{ Submitted = $true; Received = $true; Failed = $true; FailedError = 'HTTP 400: bad field' }
        $v.Success | Should -BeFalse
        $v.Stage | Should -Be 'Received'
        $v.Summary | Should -BeLike '*HTTP 400: bad field*'
    }

    It 'says the printing side works when only the upload is missing' {
        $v = Get-Verdict @{ Submitted = $true; Received = $true; LastDeliveryError = 'Could not reach https://erp' }
        $v.Stage | Should -Be 'Received'
        $v.Summary | Should -BeLike '*Could not reach https://erp*'
        $v.Hint | Should -BeLike '*printing side works*'
    }

    It 'reports a job stuck in the queue, naming the missing pipe' {
        $v = Get-Verdict @{ Submitted = $true; JobSeen = $true; JobStuck = $true; LastJobStatus = 'Error, Offline'; PipeExists = $false; ExpectedPort = '\\.\pipe\x' }
        $v.Stage | Should -Be 'InQueue'
        $v.Summary | Should -BeLike "*stuck as 'Error, Offline'*"
        $v.Summary | Should -BeLike '*Nothing is hosting \\.\pipe\x*'
    }

    It 'explains a job that never leaves Normal because an older job is ahead of it' {
        $v = Get-Verdict @{ Submitted = $true; JobSeen = $true; JobStuck = $true; LastJobStatus = 'Normal'; JobEverPrinting = $false; PipeExists = $true; JobsAhead = 1; JobsAheadText = "job 150 'Old' (Error)" }
        $v.Stage | Should -Be 'InQueue'
        $v.Summary | Should -BeLike "*stuck as 'Normal'*"
        $v.Summary | Should -BeLike "*1 job(s) were already in the queue (job 150 'Old' (Error))*"
        $v.Hint | Should -BeLike '*Remove-PrintJob*'
    }

    It 'blames a printer that is paused or offline before anything else' {
        $v = Get-Verdict @{ Submitted = $true; JobSeen = $true; JobStuck = $true; LastJobStatus = 'Normal'; JobEverPrinting = $false; JobsAhead = 2; JobsAheadText = 'x'; PrinterProblem = 'The spooler reports the printer as Paused.'; PrinterHint = 'Resume it.' }
        $v.Summary | Should -BeLike '*printer is not ready: The spooler reports the printer as Paused.*'
        $v.Hint | Should -Be 'Resume it.'
    }

    It 'says the pipe is not involved when the spooler never starts printing and nothing else explains it' {
        $v = Get-Verdict @{ Submitted = $true; JobSeen = $true; JobStuck = $true; LastJobStatus = 'Normal'; JobEverPrinting = $false; PipeExists = $true; JobsAhead = 0 }
        $v.Summary | Should -BeLike "*never reached 'Printing'*never tried to open the pipe*"
        $v.Hint | Should -BeLike '*Restart-Service Spooler*'
    }
    It 'catches a spooler that printed to a different port' {
        $v = Get-Verdict @{ Submitted = $true; SpoolerPrinted = $true; SpoolerPort = 'PORTPROMPT:'; ExpectedPort = '\\.\pipe\x' }
        $v.Stage | Should -Be 'Spooled'
        $v.Summary | Should -BeLike "*printed to 'PORTPROMPT:'*"
        $v.Summary | Should -BeLike "*not the listener's pipe*"
    }

    It 'notes when the spooler printed to the right port but the listener saw nothing' {
        $v = Get-Verdict @{ Submitted = $true; SpoolerPrinted = $true; SpoolerPort = '\\.\pipe\x'; ExpectedPort = '\\.\pipe\x'; PipeExists = $true; ListenerConnected = $false }
        $v.Stage | Should -Be 'Spooled'
        $v.Summary | Should -Not -BeLike "*not the listener's pipe*"
        $v.Summary | Should -BeLike '*no connection from the spooler*'
    }

    It 'surfaces a listener-side failure' {
        $v = Get-Verdict @{ Submitted = $true; JobGone = $true; ListenerConnected = $true; ListenerError = 'Discarded an empty job' }
        $v.Summary | Should -BeLike '*Discarded an empty job*'
    }

    It 'explains a job that vanished without a trace' {
        $v = Get-Verdict @{ Submitted = $true; JobSeen = $true; JobGone = $true }
        $v.Stage | Should -Be 'Spooled'
        $v.Hint | Should -BeLike '*PrintService Operational*'
    }

    It 'reports a page that never reached the queue' {
        $v = Get-Verdict @{ Submitted = $true }
        $v.Stage | Should -Be 'Submitted'
        $v.Success | Should -BeFalse
    }

    It 'reports a page that could not be submitted' {
        (Get-Verdict @{ Submitted = $false }).Stage | Should -Be 'None'
    }
}

Describe 'Diagnostic helpers' {
    It 'turns a failing check into a warning instead of aborting' {
        $r = InModuleScope ErpPrinter { Invoke-ErpDiagnosticStep -Category 'C' -Name 'N' -Script { throw 'boom' } }
        $r.Status | Should -Be 'Warn'
        $r.Detail | Should -BeLike '*boom*'
    }

    It 'passes check results through unchanged' {
        $r = InModuleScope ErpPrinter { Invoke-ErpDiagnosticStep -Category 'C' -Name 'N' -Script { New-ErpDiagnosticResult 'C' 'N' Pass 'fine' } }
        $r.Status | Should -Be 'Pass'
    }

    It 'translates scheduled task results' {
        InModuleScope ErpPrinter {
            ConvertTo-ErpTaskResultText -Code 0 | Should -BeLike '*successfully*'
            ConvertTo-ErpTaskResultText -Code 267009 | Should -BeLike '0x00041301*running*'
            ConvertTo-ErpTaskResultText -Code (-2147024894) | Should -BeLike '0x80070002*file not found*'
            ConvertTo-ErpTaskResultText -Code 12345 | Should -BeLike '*unrecognized*'
        }
    }

    It 'recognises job states that will not move on their own' {
        InModuleScope ErpPrinter {
            Test-ErpBadJobStatus 'Error, Offline' | Should -BeTrue
            Test-ErpBadJobStatus 'UserIntervention' | Should -BeTrue
            Test-ErpBadJobStatus 'Printing' | Should -BeFalse
            Test-ErpBadJobStatus 'Spooling' | Should -BeFalse
            Test-ErpBadJobStatus '' | Should -BeFalse
        }
    }

    It 'treats a missing folder as readable so other checks can report it' {
        InModuleScope ErpPrinter -Parameters @{ Missing = (Join-Path $TestDrive 'nothing-here') } {
            param($Missing)
            Test-ErpFolderReadable -Path $Missing | Should -BeTrue
        }
    }

    It 'renders a report with the first failure called out' {
        $text = InModuleScope ErpPrinter {
            $results = @(
                (New-ErpDiagnosticResult 'Tasks' 'Listener task' Pass 'Running')
                (New-ErpDiagnosticResult 'Tasks' 'Uploader task' Fail "State Ready`nlast result 0x1" 'Start it')
                (New-ErpDiagnosticResult 'Printing' 'Spooler' Warn 'slow' 'Look at it')
            )
            Format-ErpDiagnosticReport -Results $results
        }
        $text | Should -BeLike '*1 fail, 1 warn, 1 pass, 0 info*'
        $text | Should -BeLike '*Start with: Tasks / Uploader task*'
        $text | Should -Match ([regex]::Escape('[FAIL] Uploader task: State Ready'))
        $text | Should -Match ([regex]::Escape('-> Start it'))
        $text | Should -BeLike '*== Printing ==*'
    }
}

Describe 'Log helpers' {
    BeforeEach {
        $script:Root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        Use-TestLogs $script:Root
    }

    It 'logs an exception with its type, root cause and stack at verbose level' {
        Use-TestLogs $script:Root 'Verbose'
        InModuleScope ErpPrinter {
            try { try { throw (New-Object System.IO.IOException 'disk full') } catch { throw (New-Object System.InvalidOperationException 'outer', $_.Exception) } }
            catch { Write-ErpExceptionLog -Message 'Pass failed' -ErrorRecord $_ }
        }
        $text = Get-Content -Raw (Get-ChildItem (Join-Path $script:Root 'logs') -Filter 'listener-*.log').FullName
        $text | Should -Match ([regex]::Escape('[listener] Pass failed: [InvalidOperationException] outer (caused by IOException: disk full)'))
        $text | Should -BeLike '*Stack trace:*'
    }

    It 'parses recent entries back, filtered by level and component' {
        InModuleScope ErpPrinter {
            Write-ErpLog -Message 'Listener started (version 1, PID 2).'
            Write-ErpLog -Level Warning -Message 'Discarded an empty job.'
            Set-ErpLogContext -Component 'uploader' -GlobalSettings @{ LogLevel = 'Information'; DataRoot = $script:ErpLogSettings.LogDir | Split-Path } -Confirm:$false
            Write-ErpLog -Message 'Delivered x.'
        }
        $dir = Join-Path $script:Root 'logs'
        InModuleScope ErpPrinter -Parameters @{ Dir = $dir } {
            param($Dir)
            $all = @(Get-ErpRecentLogEntries -LogDir $Dir -Hours 1)
            $all.Count | Should -Be 3
            $all[0].Message | Should -BeLike 'Listener started*'
            $all[0].Component | Should -Be 'listener'
            @(Get-ErpRecentLogEntries -LogDir $Dir -Hours 1 -Level Warning).Count | Should -Be 1
            @(Get-ErpRecentLogEntries -LogDir $Dir -Hours 1 -Component uploader).Message | Should -Be 'Delivered x.'
            @(Get-ErpRecentLogEntries -LogDir (Join-Path $Dir 'nope') -Hours 1).Count | Should -Be 0
        }
    }

    It 'describes the runtime in one line' {
        InModuleScope ErpPrinter {
            $line = Get-ErpRuntimeSummary -GlobalSettings (Resolve-ErpSettings -Schema $script:ErpGlobalSchema)
            $line | Should -BeLike 'Runtime: user *PowerShell *language mode *data folder*log level Information.'
        }
    }
}

Describe 'Receiving a print job' {
    BeforeAll {
        function Receive([System.IO.Stream] $Stream) {
            InModuleScope ErpPrinter -Parameters @{ Stream = $Stream; P = $script:Profile; G = $script:Settings } {
                param($Stream, $P, $G)
                Receive-ErpPrintJob -Pipe $Stream -PrinterProfile $P -GlobalSettings $G
            }
        }
        function Get-LogText { Get-Content -Raw (Join-Path $script:Root 'logs\listener-*.log') }
    }

    BeforeEach {
        $script:Root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        Use-TestLogs $script:Root
        Mock -ModuleName ErpPrinter Get-ErpActivePrintJob {
            [pscustomobject]@{ UserName = 'CORP\jdoe'; DocumentName = 'Invoice 7'; Id = 4; TotalPages = 1; SubmittedTime = $null }
        }
        $script:Settings = InModuleScope ErpPrinter { Resolve-ErpSettings -Schema $script:ErpGlobalSchema }
        $script:Settings['DataRoot'] = $script:Root
        $script:Profile = @{ Name = 'Send to Odoo'; PrinterName = 'Send to Odoo'; PipeName = 'p' }
        $script:Outbox = InModuleScope ErpPrinter -Parameters @{ Root = $script:Root } { param($Root) (Get-ErpProfilePaths -DataRoot $Root -ProfileName 'Send to Odoo').Outbox }
    }

    It 'queues a PDF and writes its sidecar' {
        $bytes = [Text.Encoding]::ASCII.GetBytes("%PDF-1.4`nhello`n%%EOF")
        Receive (New-Object IO.MemoryStream (, $bytes))
        @(Get-ChildItem $script:Outbox -Filter '*.pdf').Count | Should -Be 1
        $meta = Get-Content -Raw (Get-ChildItem $script:Outbox -Filter '*.json').FullName | ConvertFrom-Json
        $meta.title | Should -Be 'Invoice 7'
        Get-LogText | Should -BeLike "*Queued 'Invoice 7'*"
    }

    It 'discards an empty job and says why' {
        Receive (New-Object IO.MemoryStream)
        @(Get-ChildItem $script:Outbox -ErrorAction SilentlyContinue).Count | Should -Be 0
        Get-LogText | Should -BeLike '*Discarded an empty job*sent no data*'
    }

    It 'warns with the first bytes when the data is not a PDF' {
        $bytes = [Text.Encoding]::ASCII.GetBytes('%!PS-Adobe-3.0 postscript')
        Receive (New-Object IO.MemoryStream (, $bytes))
        Get-LogText | Should -BeLike '*does not look like a PDF (starts with 25 21 50 53 2D,*'
    }

    It 'warns when the spooler lists no job, and still queues the document' {
        Mock -ModuleName ErpPrinter Get-ErpActivePrintJob { $null }
        $bytes = [Text.Encoding]::ASCII.GetBytes('%PDF-1.4 x')
        Receive (New-Object IO.MemoryStream (, $bytes))
        Get-LogText | Should -BeLike '*the spooler lists none*'
        @(Get-ChildItem $script:Outbox -Filter '*.json').Count | Should -Be 1
    }

    It 'does not leave a temp file behind when the pipe breaks mid-job' {
        Add-Type -TypeDefinition 'public class ErpBrokenStream : System.IO.Stream { public override bool CanRead { get { return true; } } public override bool CanSeek { get { return false; } } public override bool CanWrite { get { return false; } } public override long Length { get { throw new System.NotSupportedException(); } } public override long Position { get { return 0; } set { } } public override void Flush() { } public override int Read(byte[] b, int o, int c) { throw new System.IO.IOException("pipe broken"); } public override long Seek(long o, System.IO.SeekOrigin s) { return 0; } public override void SetLength(long v) { } public override void Write(byte[] b, int o, int c) { } }' -ErrorAction SilentlyContinue
        { Receive (New-Object ErpBrokenStream) } | Should -Throw '*pipe broken*'
        @(Get-ChildItem $script:Outbox -Filter '*.tmp' -ErrorAction SilentlyContinue).Count | Should -Be 0
    }
}
