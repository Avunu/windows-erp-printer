BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
    Import-ErpTestModule
}

Describe 'Assert-ErpHttpSuccess' {
    It 'classifies HTTP <Code> as permanent=<Permanent>' -TestCases @(
        @{ Code = 400; Permanent = $true }
        @{ Code = 404; Permanent = $true }
        @{ Code = 413; Permanent = $true }
        @{ Code = 417; Permanent = $true }
        @{ Code = 401; Permanent = $false }
        @{ Code = 403; Permanent = $false }
        @{ Code = 408; Permanent = $false }
        @{ Code = 429; Permanent = $false }
        @{ Code = 500; Permanent = $false }
        @{ Code = 503; Permanent = $false }
    ) {
        param($Code, $Permanent)
        InModuleScope ErpPrinter -Parameters @{ Code = $Code; Permanent = $Permanent } {
            param($Code, $Permanent)
            $response = [pscustomobject]@{ StatusCode = $Code; Body = ''; Json = $null }
            $caught = $null
            try { Assert-ErpHttpSuccess -Response $response -Context 'Call' } catch { $caught = $_ }
            $caught | Should -Not -BeNullOrEmpty
            Test-ErpPermanentError $caught | Should -Be $Permanent
        }
    }

    It 'passes 2xx responses' {
        InModuleScope ErpPrinter {
            { Assert-ErpHttpSuccess -Response ([pscustomobject]@{ StatusCode = 201; Body = ''; Json = $null }) -Context 'x' } | Should -Not -Throw
        }
    }

    It 'includes the server error message' {
        InModuleScope ErpPrinter {
            $json = ConvertFrom-Json '{"error": {"message": "Odoo Server Error", "data": {"message": "Invalid field folder_id"}}}'
            { Assert-ErpHttpSuccess -Response ([pscustomobject]@{ StatusCode = 422; Body = ''; Json = $json }) -Context 'Create' } |
                Should -Throw '*Invalid field folder_id*'
        }
    }
}

Describe 'Helpers' {
    It 'joins URLs regardless of slashes' {
        InModuleScope ErpPrinter {
            Join-ErpUrl 'https://a.example/' '/json/2/x' | Should -Be 'https://a.example/json/2/x'
            Join-ErpUrl 'https://a.example/odoo' 'jsonrpc' | Should -Be 'https://a.example/odoo/jsonrpc'
        }
    }

    It 'splices base64 file content into JSON' {
        $pdf = New-TestPdf (Join-Path $TestDrive 'splice.pdf')
        $json = InModuleScope ErpPrinter -Parameters @{ Pdf = $pdf } {
            param($Pdf)
            ConvertTo-ErpJsonWithPayload -InputObject @{ vals_list = @(, [ordered]@{ name = 'a"b.pdf'; datas = '@@ERP_PAYLOAD@@' }) } -FilePath $Pdf
        }
        $parsed = ConvertFrom-Json $json
        $parsed.vals_list[0].name | Should -Be 'a"b.pdf'
        [Text.Encoding]::ASCII.GetString([Convert]::FromBase64String($parsed.vals_list[0].datas)) | Should -BeLike '%PDF-1.4*'
    }

    It 'finds the permanent flag on inner exceptions' {
        InModuleScope ErpPrinter {
            $inner = New-ErpException 'inner' -Permanent
            $outer = New-Object System.Exception('outer', $inner)
            Test-ErpPermanentError $outer | Should -BeTrue
            Test-ErpPermanentError (New-Object System.Exception 'plain') | Should -BeFalse
        }
    }
}

Describe 'Invoke-ErpHttp against a loopback server' {
    BeforeAll { $script:Server = Start-TestHttpServer }
    AfterAll { Stop-TestHttpServer $script:Server }
    BeforeEach {
        $script:Server.State.Requests.Clear()
        $script:Server.State.Status = 200
        $script:Server.State.Body = '{"id": "remote-7"}'
    }

    It 'sends JSON with custom headers and parses the reply' {
        $response = InModuleScope ErpPrinter -Parameters @{ Url = $script:Server.Url } {
            param($Url)
            Invoke-ErpHttp -Method POST -Uri "$Url/json/2/x/create" -Headers @{ Authorization = 'bearer k'; 'X-Odoo-Database' = 'db' } -JsonBody '{"a":1}'
        }
        $response.StatusCode | Should -Be 200
        $response.Json.id | Should -Be 'remote-7'
        $request = $script:Server.State.Requests[0]
        $request.Path | Should -Be '/json/2/x/create'
        $request.Headers['Authorization'] | Should -Be 'bearer k'
        $request.Headers['X-Odoo-Database'] | Should -Be 'db'
        $request.Headers['User-Agent'] | Should -BeLike 'ErpPrinter/*'
        $request.ContentType | Should -BeLike 'application/json*'
        $request.Body | Should -Be '{"a":1}'
    }

    It 'returns error statuses instead of throwing' {
        $script:Server.State.Status = 503
        $script:Server.State.Body = 'maintenance'
        $response = InModuleScope ErpPrinter -Parameters @{ Url = $script:Server.Url } {
            param($Url)
            Invoke-ErpHttp -Method GET -Uri "$Url/x"
        }
        $response.StatusCode | Should -Be 503
        $response.Body | Should -Be 'maintenance'
    }

    It 'streams multipart uploads' {
        $pdf = New-TestPdf (Join-Path $TestDrive 'multi.pdf')
        InModuleScope ErpPrinter -Parameters @{ Url = $script:Server.Url; Pdf = $pdf } {
            param($Url, $Pdf)
            $content = New-ErpMultipartContent -Fields ([ordered]@{ is_private = '1'; folder = 'Home' }) -FileField 'file' -FilePath $Pdf -FileName 'Invoice 7.pdf'
            try { $null = Invoke-ErpHttp -Method POST -Uri "$Url/api/method/upload_file" -Content $content } finally { $content.Dispose() }
        }
        $request = $script:Server.State.Requests[0]
        $request.ContentType | Should -BeLike 'multipart/form-data; boundary=*'
        $request.Body | Should -Match 'name="?is_private"?'
        $request.Body | Should -Match 'filename="?Invoice 7.pdf"?'
        $request.Body | Should -Match '%PDF-1.4'
    }

    It 'downloads to a file' {
        $script:Server.State.Body = 'binary-ish'
        $out = Join-Path $TestDrive 'download.bin'
        InModuleScope ErpPrinter -Parameters @{ Url = $script:Server.Url; Out = $out } {
            param($Url, $Out)
            $null = Invoke-ErpHttp -Method GET -Uri "$Url/pkg.msi" -OutFile $Out
        }
        Get-Content -Raw $out | Should -Be 'binary-ish'
    }

    It 'reports unreachable hosts as transient errors' {
        $port = Get-FreeTcpPort
        InModuleScope ErpPrinter -Parameters @{ Port = $port } {
            param($Port)
            $caught = $null
            try { Invoke-ErpHttp -Method GET -Uri "http://127.0.0.1:$Port/" } catch { $caught = $_ }
            $caught.Exception.Message | Should -BeLike 'Could not reach*'
            Test-ErpPermanentError $caught | Should -BeFalse
        }
    }
}
