# One HttpClient-based transport for every backend. It works the same on Windows
# PowerShell 5.1 and PowerShell 7, streams file uploads instead of buffering them,
# honours an explicit proxy (the SYSTEM account has no per-user proxy settings), and
# returns the status code instead of throwing so errors can be classified.

$script:ErpHttpClients = @{}

function New-ErpException {
    <# Creates an exception tagged as permanent (do not retry) or transient (retry later). #>
    [CmdletBinding()]
    [OutputType([System.Exception])]
    param([Parameter(Mandatory)] [string] $Message, [switch] $Permanent)
    $ex = New-Object System.Exception $Message
    $ex.Data['ErpPermanent'] = [bool]$Permanent
    $ex
}

function Test-ErpPermanentError {
    [CmdletBinding()]
    [OutputType([bool])]
    param([Parameter(Mandatory)] $ErrorRecord)
    $ex = if ($ErrorRecord -is [System.Management.Automation.ErrorRecord]) { $ErrorRecord.Exception } else { $ErrorRecord }
    while ($ex) {
        if ($ex.Data.Contains('ErpPermanent')) { return [bool]$ex.Data['ErpPermanent'] }
        $ex = $ex.InnerException
    }
    $false
}

function Get-ErpHttpClient {
    [CmdletBinding()]
    param([System.Collections.IDictionary] $GlobalSettings = @{})
    $proxy = [string]$GlobalSettings.ProxyUrl
    $timeout = if ($GlobalSettings.HttpTimeoutSeconds) { [int]$GlobalSettings.HttpTimeoutSeconds } else { 120 }
    $cacheKey = "$proxy|$timeout"
    if (-not $script:ErpHttpClients.ContainsKey($cacheKey)) {
        Add-Type -AssemblyName System.Net.Http
        $handler = New-Object System.Net.Http.HttpClientHandler
        $handler.AutomaticDecompression = [System.Net.DecompressionMethods]::GZip -bor [System.Net.DecompressionMethods]::Deflate
        if ($proxy) {
            $webProxy = New-Object System.Net.WebProxy($proxy, $true)
            $webProxy.UseDefaultCredentials = $true
            $handler.Proxy = $webProxy
            $handler.UseProxy = $true
        }
        $client = New-Object System.Net.Http.HttpClient($handler)
        $client.Timeout = [TimeSpan]::FromSeconds($timeout)
        $null = $client.DefaultRequestHeaders.TryAddWithoutValidation('User-Agent', "ErpPrinter/$($script:ErpBuildInfo.Version)")
        $script:ErpHttpClients[$cacheKey] = $client
    }
    $script:ErpHttpClients[$cacheKey]
}

function Invoke-ErpHttp {
    <#
    .SYNOPSIS
        Sends an HTTP request and returns StatusCode, Body and (if JSON) Json.
    .DESCRIPTION
        Network failures and timeouts are thrown as transient errors. HTTP error statuses
        are returned; call Assert-ErpHttpSuccess to turn them into classified exceptions.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [ValidateSet('GET', 'POST', 'PUT', 'HEAD')] [string] $Method = 'POST',
        [Parameter(Mandatory)] [string] $Uri,
        [hashtable] $Headers = @{},
        [string] $JsonBody,
        [System.Net.Http.HttpContent] $Content,
        [System.Collections.IDictionary] $GlobalSettings = @{},
        [string] $OutFile
    )
    $client = Get-ErpHttpClient -GlobalSettings $GlobalSettings
    $request = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod $Method), $Uri)
    foreach ($name in $Headers.Keys) {
        $null = $request.Headers.TryAddWithoutValidation($name, [string]$Headers[$name])
    }
    if ($PSBoundParameters.ContainsKey('JsonBody')) {
        $request.Content = New-Object System.Net.Http.StringContent($JsonBody, [Text.Encoding]::UTF8, 'application/json')
    } elseif ($Content) {
        $request.Content = $Content
    }
    $completion = if ($OutFile) { [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead } else { [System.Net.Http.HttpCompletionOption]::ResponseContentRead }
    $response = $null
    try {
        try {
            $response = $client.SendAsync($request, $completion).GetAwaiter().GetResult()
        } catch {
            # PowerShell wraps .NET exceptions in MethodInvocationException; look inside.
            $inner = $_.Exception
            while ($inner.InnerException -and -not ($inner -is [System.Threading.Tasks.TaskCanceledException])) { $inner = $inner.InnerException }
            if ($inner -is [System.Threading.Tasks.TaskCanceledException]) { throw (New-ErpException "Timed out calling $Method $Uri.") }
            throw (New-ErpException "Could not reach $Uri`: $($inner.Message)")
        }
        $status = [int]$response.StatusCode
        if ($OutFile -and $response.IsSuccessStatusCode) {
            $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $file = [IO.File]::Create($OutFile)
            try { $stream.CopyTo($file) } finally { $file.Dispose(); $stream.Dispose() }
            return [pscustomobject]@{ StatusCode = $status; Body = ''; Json = $null }
        }
        $body = if ($response.Content) { $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() } else { '' }
        $json = $null
        $contentType = if ($response.Content -and $response.Content.Headers.ContentType) { $response.Content.Headers.ContentType.MediaType } else { '' }
        if ($body -and ($contentType -match 'json' -or $body.TrimStart().StartsWith('{') -or $body.TrimStart().StartsWith('['))) {
            try { $json = ConvertFrom-Json $body -ErrorAction Stop } catch { $json = $null }
        }
        [pscustomobject]@{ StatusCode = $status; Body = $body; Json = $json }
    } finally {
        if ($response) { $response.Dispose() }
        $request.Dispose()
    }
}

function Assert-ErpHttpSuccess {
    <#
    Throws for non-2xx. 408, 425, 429, 5xx, 401 and 403 are transient (the server or
    credentials may be fixed later); other 4xx mean the request itself is wrong and are permanent.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Response, [Parameter(Mandatory)] [string] $Context)
    $code = $Response.StatusCode
    if ($code -ge 200 -and $code -lt 300) { return }
    $detail = Get-ErpErrorDetail $Response
    $message = "$Context failed with HTTP $code$(if ($detail) { ": $detail" })"
    if ($code -in 401, 403) { throw (New-ErpException "$message (check the API key and the user's access rights)") }
    $permanent = $code -ge 400 -and $code -lt 500 -and $code -notin 408, 425, 429
    throw (New-ErpException $message -Permanent:$permanent)
}

function Get-ErpErrorDetail {
    param($Response)
    $json = $Response.Json
    if ($json) {
        foreach ($path in @('message', 'error.data.message', 'error.message', 'exception', '_server_messages')) {
            $value = $json
            foreach ($part in $path.Split('.')) {
                if ($null -ne $value -and $value.PSObject.Properties[$part]) { $value = $value.$part } else { $value = $null; break }
            }
            if ($value -and $value -is [string]) { return (Limit-ErpString $value 500) }
        }
    }
    if ($Response.Body) { return (Limit-ErpString (($Response.Body -replace '<[^>]+>', ' ') -replace '\s+', ' ').Trim() 300) }
    ''
}

function Limit-ErpString {
    param([string] $Text, [int] $Max)
    if ($Text.Length -le $Max) { return $Text }
    $Text.Substring(0, $Max) + '...'
}

function Join-ErpUrl {
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)] [string] $Base, [Parameter(Mandatory)] [string] $Path)
    $Base.TrimEnd('/') + '/' + $Path.TrimStart('/')
}

function New-ErpMultipartContent {
    <#
    Builds multipart/form-data with string fields and one streamed file part.
    The caller must dispose the returned content (which closes the file).
    #>
    [CmdletBinding()]
    param(
        [System.Collections.IDictionary] $Fields = @{},
        [Parameter(Mandatory)] [string] $FileField,
        [Parameter(Mandatory)] [string] $FilePath,
        [Parameter(Mandatory)] [string] $FileName,
        [string] $MediaType = 'application/pdf'
    )
    Add-Type -AssemblyName System.Net.Http
    $multipart = New-Object System.Net.Http.MultipartFormDataContent
    foreach ($name in $Fields.Keys) {
        $multipart.Add((New-Object System.Net.Http.StringContent([string]$Fields[$name], [Text.Encoding]::UTF8)), $name)
    }
    $stream = [IO.File]::OpenRead($FilePath)
    $fileContent = New-Object System.Net.Http.StreamContent($stream)
    $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse($MediaType)
    $multipart.Add($fileContent, $FileField, $FileName)
    # MultipartFormDataContent is enumerable; without -NoEnumerate PowerShell would unroll it into its parts.
    Write-Output -NoEnumerate -InputObject $multipart
}

function ConvertTo-ErpJsonWithPayload {
    <#
    Serializes an object whose string property value is the placeholder '@@ERP_PAYLOAD@@',
    then splices in the base64 file content. This avoids ConvertTo-Json limits and slowness
    on multi-megabyte strings in Windows PowerShell 5.1.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)] $InputObject, [Parameter(Mandatory)] [string] $FilePath)
    $json = ConvertTo-Json $InputObject -Depth 20 -Compress
    $base64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($FilePath))
    $json.Replace('"@@ERP_PAYLOAD@@"', '"' + $base64 + '"')
}
