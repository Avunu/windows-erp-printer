BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
    Import-ErpTestModule

    function New-TestProfile([string] $Backend, [hashtable] $Overrides = @{}) {
        $p = InModuleScope ErpPrinter { Resolve-ErpSettings -Schema $script:ErpProfileSchema -Context @{ Name = 'P' } }
        $p['Name'] = 'P'
        $p['Backend'] = $Backend
        $p['ServerUrl'] = 'https://erp.example.com'
        foreach ($k in $Overrides.Keys) { $p[$k] = $Overrides[$k] }
        $p
    }

    $script:Meta = [pscustomobject]@{
        id = 'doc1'; profile = 'P'; user = 'CORP\jdoe'; title = 'Invoice 7'; computer = 'PC-01'
        jobId = '3'; pages = 2; printed = '2026-01-02T03:04:05Z'
    }

    # Replies are queued per test; each call is recorded with its body decoded to text.
    function Set-HttpReplies([object[]] $Replies) {
        $script:Replies = New-Object System.Collections.Queue
        foreach ($r in $Replies) { $script:Replies.Enqueue($r) }
        $script:Calls = New-Object System.Collections.Generic.List[object]
        Mock -ModuleName ErpPrinter Invoke-ErpHttp {
            $body = if ($JsonBody) { $JsonBody } elseif ($Content) { $Content.ReadAsStringAsync().GetAwaiter().GetResult() } else { '' }
            $script:Calls.Add([pscustomobject]@{ Method = $Method; Uri = $Uri; Headers = $Headers; Body = $body })
            $reply = $script:Replies.Dequeue()
            $json = $null
            if ($reply.Body) { $json = ConvertFrom-Json $reply.Body }
            [pscustomobject]@{ StatusCode = $reply.Status; Body = $reply.Body; Json = $json }
        }
    }

    function Invoke-Backend([string] $Action, $PrinterProfile, [hashtable] $Secrets = @{ ApiKey = 'key'; ApiSecret = 'secret' }) {
        $pdf = New-TestPdf (Join-Path $TestDrive 'doc.pdf')
        InModuleScope ErpPrinter -Parameters @{ P = $PrinterProfile; S = $Secrets; Meta = $script:Meta; Pdf = $pdf; Action = $Action } {
            param($P, $S, $Meta, $Pdf, $Action)
            $command = Get-ErpBackendCommand -Backend $P.Backend -Action $Action
            if ($Action -eq 'Send') {
                & $command -PrinterProfile $P -Meta $Meta -PdfPath $Pdf -FileName 'Invoice 7.pdf' -Secrets $S
            } else {
                & $command -PrinterProfile $P -Secrets $S
            }
        }
    }
}

Describe 'OdooJson2 backend' {
    BeforeEach {
        Mock -ModuleName ErpPrinter Get-ErpDirectoryIdentity { @('john.doe@corp.example') }
        InModuleScope ErpPrinter { $script:ErpUserCache.Clear(); $script:ErpEdiTypeCache.Clear() }
    }

    It 'creates a documents.document with folder and base64 content' {
        Set-HttpReplies @(@{ Status = 200; Body = '[42]' })
        $id = Invoke-Backend Send (New-TestProfile OdooJson2 @{ OdooFolderId = 7; Database = 'prod' })
        $id | Should -Be '42'
        $call = $script:Calls[0]
        $call.Uri | Should -Be 'https://erp.example.com/json/2/documents.document/create'
        $call.Headers.Authorization | Should -Be 'bearer key'
        $call.Headers['X-Odoo-Database'] | Should -Be 'prod'
        $vals = (ConvertFrom-Json $call.Body).vals_list[0]
        $vals.name | Should -Be 'Invoice 7.pdf'
        $vals.folder_id | Should -Be 7
        [Text.Encoding]::ASCII.GetString([Convert]::FromBase64String($vals.datas)) | Should -BeLike '%PDF*'
        $vals.PSObject.Properties['owner_id'] | Should -BeNullOrEmpty
    }

    It 'maps the Windows user to owner_id and caches the lookup' {
        Set-HttpReplies @(@{ Status = 200; Body = '[{"id": 9}]' }, @{ Status = 200; Body = '[1]' }, @{ Status = 200; Body = '[2]' })
        $printerProfile = New-TestProfile OdooJson2 @{ UserMapping = $true; UserEmailDomain = 'corp.example' }
        Invoke-Backend Send $printerProfile | Should -Be '1'
        Invoke-Backend Send $printerProfile | Should -Be '2'
        $script:Calls.Count | Should -Be 3
        $search = ConvertFrom-Json $script:Calls[0].Body
        $script:Calls[0].Uri | Should -BeLike '*/json/2/res.users/search_read'
        $search.domain[0] | Should -Be '|'
        $search.domain[1][2] | Should -Contain 'jdoe@corp.example'
        $search.domain[1][2] | Should -Contain 'john.doe@corp.example'
        (ConvertFrom-Json $script:Calls[1].Body).vals_list[0].owner_id | Should -Be 9
    }

    It 'sends a single user candidate as a list' {
        Mock -ModuleName ErpPrinter Get-ErpDirectoryIdentity { @() }
        Set-HttpReplies @(@{ Status = 200; Body = '[]' }, @{ Status = 200; Body = '[1]' })
        $script:Meta.user = 'jdoe'
        try { Invoke-Backend Send (New-TestProfile OdooJson2 @{ UserMapping = $true }) | Out-Null }
        finally { $script:Meta.user = 'CORP\jdoe' }
        $script:Calls[0].Body | Should -BeLike '*["login","in",["jdoe"]]*'
    }

    It 'attaches to a record with ir.attachment and merges extra fields' {
        Set-HttpReplies @(@{ Status = 200; Body = '[5]' })
        $printerProfile = New-TestProfile OdooJson2 @{ OdooModel = 'ir.attachment'; OdooResModel = 'res.partner'; OdooResId = 3; OdooExtraFields = '{"description": "From {user} on {computer}", "public": false}' }
        Invoke-Backend Send $printerProfile | Should -Be '5'
        $script:Calls[0].Uri | Should -BeLike '*/json/2/ir.attachment/create'
        $vals = (ConvertFrom-Json $script:Calls[0].Body).vals_list[0]
        $vals.res_model | Should -Be 'res.partner'
        $vals.res_id | Should -Be 3
        $vals.mimetype | Should -Be 'application/pdf'
        $vals.description | Should -Be 'From CORP\jdoe on PC-01'
        $vals.public | Should -BeFalse
    }

    It 'creates a received edi.exchange.record, preferring the type pinned to the backend' {
        Set-HttpReplies @(
            @{ Status = 200; Body = '[{"id": 3, "backend_type_id": [2, "PDF import"]}]' }
            @{ Status = 200; Body = '[{"id": 8, "backend_id": false, "direction": "input"}, {"id": 9, "backend_id": [3, "Printers"], "direction": "input"}]' }
            @{ Status = 200; Body = '[55]' }
            @{ Status = 200; Body = '[56]' }
        )
        $printerProfile = New-TestProfile OdooJson2 @{ OdooModel = 'edi.exchange.record'; OdooEdiBackendId = 3; OdooEdiExchangeType = 'printed_po' }
        Invoke-Backend Send $printerProfile | Should -Be '55'
        Invoke-Backend Send $printerProfile | Should -Be '56'
        $script:Calls.Count | Should -Be 4
        $script:Calls[0].Uri | Should -BeLike '*/json/2/edi.backend/search_read'
        $typeSearch = ConvertFrom-Json $script:Calls[1].Body
        $script:Calls[1].Uri | Should -BeLike '*/json/2/edi.exchange.type/search_read'
        $typeSearch.domain[0] | Should -Be @('code', '=', 'printed_po')
        $typeSearch.domain[5] | Should -Be @('backend_type_id', '=', 2)
        $script:Calls[2].Uri | Should -BeLike '*/json/2/edi.exchange.record/create'
        $vals = (ConvertFrom-Json $script:Calls[2].Body).vals_list[0]
        $vals.backend_id | Should -Be 3
        $vals.type_id | Should -Be 9
        $vals.edi_exchange_state | Should -Be 'input_received'
        $vals.exchange_filename | Should -Be 'Invoice 7.pdf'
        [Text.Encoding]::ASCII.GetString([Convert]::FromBase64String($vals.exchange_file)) | Should -BeLike '%PDF*'
        $vals.PSObject.Properties['name'] | Should -BeNullOrEmpty
        $vals.PSObject.Properties['datas'] | Should -BeNullOrEmpty
    }

    It 'refuses an EDI exchange type that is <Case>' -TestCases @(
        @{ Case = 'missing'; Types = '[]'; Expected = "*No EDI exchange type with code 'printed_po' on backend #3*" }
        @{ Case = 'an output type'; Types = '[{"id": 8, "backend_id": false, "direction": "output"}]'; Expected = '*is an output type*' }
    ) {
        param($Types, $Expected)
        Set-HttpReplies @(@{ Status = 200; Body = '[{"id": 3, "backend_type_id": [2, "PDF import"]}]' }, @{ Status = 200; Body = $Types })
        $printerProfile = New-TestProfile OdooJson2 @{ OdooModel = 'edi.exchange.record'; OdooEdiBackendId = 3; OdooEdiExchangeType = 'printed_po' }
        { Invoke-Backend Send $printerProfile } | Should -Throw $Expected
        $script:Calls.Count | Should -Be 2
    }

    It 'reports a missing EDI backend' {
        Set-HttpReplies @(@{ Status = 200; Body = '[]' })
        $printerProfile = New-TestProfile OdooJson2 @{ OdooModel = 'edi.exchange.record'; OdooEdiBackendId = 3; OdooEdiExchangeType = 'printed_po' }
        { Invoke-Backend Send $printerProfile } | Should -Throw '*EDI backend #3 does not exist*'
    }

    It 'explains a missing JSON-2 endpoint' {
        Set-HttpReplies @(@{ Status = 404; Body = '' })
        { Invoke-Backend Send (New-TestProfile OdooJson2) } | Should -Throw '*OdooJsonRpc backend*'
    }

    It 'tests the connection with search_count' {
        Set-HttpReplies @(@{ Status = 200; Body = '12' })
        Invoke-Backend Test (New-TestProfile OdooJson2) | Should -BeLike '*12 documents.document*'
        $script:Calls[0].Uri | Should -BeLike '*/documents.document/search_count'
    }
}

Describe 'OdooJsonRpc backend' {
    BeforeEach {
        Mock -ModuleName ErpPrinter Get-ErpDirectoryIdentity { @() }
        InModuleScope ErpPrinter { $script:ErpOdooSessions.Clear(); $script:ErpEdiTypeCache.Clear() }
    }

    It 'authenticates, then creates through execute_kw' {
        Set-HttpReplies @(
            @{ Status = 200; Body = '{"jsonrpc": "2.0", "id": 1, "result": 6}' }
            @{ Status = 200; Body = '{"jsonrpc": "2.0", "id": 2, "result": 77}' }
        )
        $printerProfile = New-TestProfile OdooJsonRpc @{ Database = 'prod'; Username = 'api@corp.example'; OdooFolderId = 4 }
        Invoke-Backend Send $printerProfile | Should -Be '77'
        $auth = ConvertFrom-Json $script:Calls[0].Body
        $auth.params.service | Should -Be 'common'
        $auth.params.method | Should -Be 'authenticate'
        $auth.params.args[0..2] | Should -Be @('prod', 'api@corp.example', 'key')
        $create = ConvertFrom-Json $script:Calls[1].Body
        $create.params.method | Should -Be 'execute_kw'
        $create.params.args[1] | Should -Be 6
        $create.params.args[3] | Should -Be 'documents.document'
        $create.params.args[4] | Should -Be 'create'
        $create.params.args[5][0].folder_id | Should -Be 4
    }

    It 'resolves the EDI exchange type through search_read, then creates the record' {
        Set-HttpReplies @(
            @{ Status = 200; Body = '{"result": 6}' }
            @{ Status = 200; Body = '{"result": [{"id": 3, "backend_type_id": [2, "PDF import"]}]}' }
            @{ Status = 200; Body = '{"result": [{"id": 8, "backend_id": false, "direction": "input"}]}' }
            @{ Status = 200; Body = '{"result": 91}' }
        )
        $printerProfile = New-TestProfile OdooJsonRpc @{ Database = 'prod'; Username = 'api'; OdooModel = 'edi.exchange.record'; OdooEdiBackendId = 3; OdooEdiExchangeType = 'printed_po' }
        Invoke-Backend Send $printerProfile | Should -Be '91'
        $backendSearch = (ConvertFrom-Json $script:Calls[1].Body).params.args
        $backendSearch[3..4] | Should -Be @('edi.backend', 'search_read')
        $backendSearch[6].fields | Should -Be @('backend_type_id')
        $create = (ConvertFrom-Json $script:Calls[3].Body).params.args
        $create[3..4] | Should -Be @('edi.exchange.record', 'create')
        $create[5][0].type_id | Should -Be 8
        $create[5][0].edi_exchange_state | Should -Be 'input_received'
    }

    It 'names the EDI exchange type in the connection test' {
        Set-HttpReplies @(
            @{ Status = 200; Body = '{"result": 6}' }
            @{ Status = 200; Body = '{"result": 4}' }
            @{ Status = 200; Body = '{"result": [{"id": 3, "backend_type_id": [2, "PDF import"]}]}' }
            @{ Status = 200; Body = '{"result": [{"id": 8, "backend_id": false, "direction": "input"}]}' }
        )
        $printerProfile = New-TestProfile OdooJsonRpc @{ Database = 'prod'; Username = 'api'; OdooModel = 'edi.exchange.record'; OdooEdiBackendId = 3; OdooEdiExchangeType = 'printed_po' }
        Invoke-Backend Test $printerProfile | Should -BeLike "*4 edi.exchange.record records. Exchange type 'printed_po' is #8 on EDI backend #3."
    }

    It 'discovers the database when the server has exactly one' {
        Set-HttpReplies @(
            @{ Status = 200; Body = '{"result": ["only"]}' }
            @{ Status = 200; Body = '{"result": 6}' }
            @{ Status = 200; Body = '{"result": 3}' }
        )
        Invoke-Backend Test (New-TestProfile OdooJsonRpc @{ Username = 'api' }) | Should -BeLike "*database 'only' as uid 6*"
    }

    It 'treats <Name> as permanent=<Permanent>' -TestCases @(
        @{ Name = 'odoo.exceptions.ValidationError'; Permanent = $true }
        @{ Name = 'builtins.ValueError'; Permanent = $true }
        @{ Name = 'odoo.exceptions.AccessError'; Permanent = $false }
        @{ Name = 'psycopg2.OperationalError'; Permanent = $false }
    ) {
        param($Name, $Permanent)
        $errorBody = @{ error = @{ code = 200; message = 'Odoo Server Error'; data = @{ name = $Name; message = 'nope' } } } | ConvertTo-Json -Depth 5
        Set-HttpReplies @(@{ Status = 200; Body = '{"result": 6}' }, @{ Status = 200; Body = $errorBody })
        $caught = $null
        try { Invoke-Backend Send (New-TestProfile OdooJsonRpc @{ Database = 'db'; Username = 'api' }) } catch { $caught = $_ }
        $caught.Exception.Message | Should -BeLike "*nope ($Name)*"
        InModuleScope ErpPrinter -Parameters @{ E = $caught } { param($E) Test-ErpPermanentError $E } | Should -Be $Permanent
    }

    It 'reports a rejected login' {
        Set-HttpReplies @(@{ Status = 200; Body = '{"result": false}' })
        { Invoke-Backend Send (New-TestProfile OdooJsonRpc @{ Database = 'db'; Username = 'api' }) } | Should -Throw "*rejected login 'api'*"
    }
}

Describe 'ERPNext backend' {
    It 'uploads with token auth and attachment fields' {
        Set-HttpReplies @(@{ Status = 200; Body = '{"message": {"name": "f00d", "file_url": "/private/files/Invoice 7.pdf"}}' })
        $printerProfile = New-TestProfile ERPNext @{ ERPNextDoctype = 'Purchase Invoice'; ERPNextDocname = 'PINV-{jobid}'; ERPNextFolder = 'Home/Scans' }
        Invoke-Backend Send $printerProfile | Should -Be 'f00d'
        $call = $script:Calls[0]
        $call.Uri | Should -Be 'https://erp.example.com/api/method/upload_file'
        $call.Headers.Authorization | Should -Be 'token key:secret'
        $call.Body | Should -Match 'Purchase Invoice'
        $call.Body | Should -Match 'PINV-3'
        $call.Body | Should -Match 'Home/Scans'
        $call.Body | Should -Match '%PDF-1.4'
    }

    It 'reads the logged-in user for the connection test' {
        Set-HttpReplies @(@{ Status = 200; Body = '{"message": "api@example.com"}' })
        Invoke-Backend Test (New-TestProfile ERPNext) | Should -Be 'Connected to ERPNext as api@example.com.'
        $script:Calls[0].Method | Should -Be 'GET'
    }

    It 'treats a Frappe validation error (HTTP 417) as permanent' {
        Set-HttpReplies @(@{ Status = 417; Body = '{"exception": "frappe.exceptions.ValidationError: Folder missing"}' })
        { Invoke-Backend Send (New-TestProfile ERPNext) } | Should -Throw '*Folder missing*'
    }
}

Describe 'Webhook backend' {
    It 'posts multipart with metadata and an idempotency key' {
        Set-HttpReplies @(@{ Status = 201; Body = '' })
        $printerProfile = New-TestProfile Webhook @{ ServerUrl = 'https://hooks.example.com/print'; WebhookAuthHeader = 'X-Api-Key'; WebhookFileField = 'document' }
        Invoke-Backend Send $printerProfile | Should -Be 'HTTP 201'
        $call = $script:Calls[0]
        $call.Uri | Should -Be 'https://hooks.example.com/print'
        $call.Headers['X-Api-Key'] | Should -Be 'key'
        $call.Headers['Idempotency-Key'] | Should -Be 'doc1'
        $call.Headers['X-ErpPrinter-Event'] | Should -Be 'document'
        $call.Body | Should -Match 'name="?document"?'
        $call.Body | Should -Match 'CORP\\jdoe'
    }

    It 'posts JSON with base64 content' {
        Set-HttpReplies @(@{ Status = 200; Body = '{"id": "w-1"}' })
        Invoke-Backend Send (New-TestProfile Webhook @{ WebhookFormat = 'Json' }) -Secrets @{ ApiKey = '' } | Should -Be 'w-1'
        $body = ConvertFrom-Json $script:Calls[0].Body
        $body.metadata.title | Should -Be 'Invoice 7'
        $body.metadata.fileName | Should -Be 'Invoice 7.pdf'
        [Text.Encoding]::ASCII.GetString([Convert]::FromBase64String($body.contentBase64)) | Should -BeLike '%PDF*'
        $script:Calls[0].Headers.ContainsKey('Authorization') | Should -BeFalse
    }
}

Describe 'Test-ErpPrinterConnection' {
    BeforeEach { Initialize-FakeRegistry }

    It 'returns validation problems without calling the server' {
        Set-HttpReplies @()
        $result = Test-ErpPrinterConnection -PrinterProfile (New-TestProfile OdooJson2 @{ ServerUrl = '' })
        $result.Success | Should -BeFalse
        $result.Message | Should -BeLike '*Server URL is required*'
        $script:Calls.Count | Should -Be 0
    }

    It 'prefers unsaved secrets and reports success' {
        Set-HttpReplies @(@{ Status = 200; Body = '{"message": "api@example.com"}' })
        $result = Test-ErpPrinterConnection -PrinterProfile (New-TestProfile ERPNext) -Secrets @{ ApiKey = 'new'; ApiSecret = 'pw' }
        $result.Success | Should -BeTrue
        $script:Calls[0].Headers.Authorization | Should -Be 'token new:pw'
    }

    It 'turns backend errors into a failed result' {
        Set-HttpReplies @(@{ Status = 401; Body = '' })
        $result = Test-ErpPrinterConnection -PrinterProfile (New-TestProfile ERPNext) -Secrets @{ ApiKey = 'k'; ApiSecret = 's' } -WarningAction SilentlyContinue
        $result.Success | Should -BeFalse
        $result.Message | Should -BeLike '*HTTP 401*'
    }
}
