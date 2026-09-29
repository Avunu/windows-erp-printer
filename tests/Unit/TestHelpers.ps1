# Shared test helpers. Dot-source from BeforeAll.
# The module's registry and DPAPI access is funnelled through a few private wrappers;
# these helpers replace them with an in-memory fake so the suite runs on any OS.

$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:ModuleManifest = Join-Path $script:RepoRoot 'src/Modules/ErpPrinter/ErpPrinter.psd1'

function Import-ErpTestModule {
    Get-Module ErpPrinter | Remove-Module -Force
    Import-Module $script:ModuleManifest -Force
}

function Initialize-FakeRegistry {
    $script:FakeRegistry = @{}
    $script:FakeKinds = @{}

    Mock -ModuleName ErpPrinter Get-ErpRegistryValues {
        if ($script:FakeRegistry.ContainsKey($Path)) { return $script:FakeRegistry[$Path].Clone() }
        @{}
    }
    Mock -ModuleName ErpPrinter Get-ErpRegistrySubKeyNames {
        $prefix = "$Path\"
        @($script:FakeRegistry.Keys | Where-Object { $_.StartsWith($prefix) } |
                ForEach-Object { $_.Substring($prefix.Length).Split('\')[0] } | Sort-Object -Unique)
    }
    Mock -ModuleName ErpPrinter Test-ErpRegistryKey { $script:FakeRegistry.ContainsKey($Path) }
    Mock -ModuleName ErpPrinter Initialize-ErpRegistryKey {
        if (-not $script:FakeRegistry.ContainsKey($Path)) { $script:FakeRegistry[$Path] = @{} }
    }
    Mock -ModuleName ErpPrinter Set-ErpRegistryValue {
        if (-not $script:FakeRegistry.ContainsKey($Path)) { $script:FakeRegistry[$Path] = @{} }
        $script:FakeRegistry[$Path][$Name] = $Value
        $script:FakeKinds["$Path|$Name"] = $Kind
    }
    Mock -ModuleName ErpPrinter Remove-ErpRegistryValue {
        if ($script:FakeRegistry.ContainsKey($Path)) { $script:FakeRegistry[$Path].Remove($Name) }
    }
    Mock -ModuleName ErpPrinter Remove-ErpRegistryKey {
        foreach ($key in @($script:FakeRegistry.Keys)) {
            if ($key -eq $Path -or $key.StartsWith("$Path\")) { $script:FakeRegistry.Remove($key) }
        }
    }
    Mock -ModuleName ErpPrinter Initialize-ErpSecretsKey { }
    # Reversible stand-in for DPAPI.
    Mock -ModuleName ErpPrinter Protect-ErpSecret { [Text.Encoding]::UTF8.GetBytes("enc:$PlainText") }
    Mock -ModuleName ErpPrinter Unprotect-ErpSecret { [Text.Encoding]::UTF8.GetString($CipherBytes).Substring(4) }
}

function Set-FakeRegistryValue([string] $Path, [string] $Name, $Value) {
    if (-not $script:FakeRegistry.ContainsKey($Path)) { $script:FakeRegistry[$Path] = @{} }
    $script:FakeRegistry[$Path][$Name] = $Value
}

function Get-FreeTcpPort {
    $tcp = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    $tcp.Start()
    try { $tcp.LocalEndpoint.Port } finally { $tcp.Stop() }
}

function Start-TestHttpServer {
    <#
    Minimal loopback HTTP server on a background runspace. Records every request in
    .State.Requests and answers with .State.Status / .State.Body (changeable per test).
    #>
    $port = Get-FreeTcpPort
    $state = [hashtable]::Synchronized(@{
            Requests    = [Collections.ArrayList]::Synchronized((New-Object Collections.ArrayList))
            Status      = 200
            Body        = '{"id": "remote-7"}'
            ContentType = 'application/json'
        })
    $listener = New-Object Net.HttpListener
    $listener.Prefixes.Add("http://127.0.0.1:$port/")
    $listener.Start()
    $ps = [powershell]::Create()
    $null = $ps.AddScript({
            param($listener, $state)
            while ($listener.IsListening) {
                try { $context = $listener.GetContext() } catch { break }
                $buffer = New-Object IO.MemoryStream
                $context.Request.InputStream.CopyTo($buffer)
                $headers = @{}
                foreach ($key in $context.Request.Headers.AllKeys) { $headers[$key] = $context.Request.Headers[$key] }
                $null = $state.Requests.Add([pscustomobject]@{
                        Method      = $context.Request.HttpMethod
                        Path        = $context.Request.Url.AbsolutePath
                        Headers     = $headers
                        ContentType = $context.Request.ContentType
                        Body        = [Text.Encoding]::UTF8.GetString($buffer.ToArray())
                    })
                $bytes = [Text.Encoding]::UTF8.GetBytes([string]$state.Body)
                $context.Response.StatusCode = $state.Status
                $context.Response.ContentType = $state.ContentType
                $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
                $context.Response.Close()
            }
        }).AddArgument($listener).AddArgument($state)
    $handle = $ps.BeginInvoke()
    [pscustomobject]@{ Url = "http://127.0.0.1:$port"; State = $state; Listener = $listener; PowerShell = $ps; Handle = $handle }
}

function Stop-TestHttpServer($Server) {
    if (-not $Server) { return }
    $Server.Listener.Stop()
    $Server.Listener.Close()
    try { $null = $Server.PowerShell.EndInvoke($Server.Handle) } catch { Write-Verbose "Server runspace: $_" }
    $Server.PowerShell.Dispose()
}

function New-TestPdf([string] $Path, [int] $ExtraBytes = 0) {
    $content = "%PDF-1.4`n% test document`n" + ('x' * $ExtraBytes) + "`n%%EOF`n"
    [IO.File]::WriteAllText($Path, $content)
    $Path
}
