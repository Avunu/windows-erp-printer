# Odoo 14-18 external API over /jsonrpc (execute_kw). Odoo is phasing this out in favour
# of JSON-2; use the OdooJson2 backend on Odoo 19 and later.

$script:ErpOdooSessions = @{}

function Invoke-ErpOdooRpcCall {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $PrinterProfile,
        [System.Collections.IDictionary] $GlobalSettings = @{},
        [Parameter(Mandatory)] [string] $Service,
        [Parameter(Mandatory)] [string] $Method,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Arguments,
        [string] $PayloadPath
    )
    $body = [ordered]@{
        jsonrpc = '2.0'
        method  = 'call'
        id      = Get-Random -Maximum 1000000
        params  = [ordered]@{ service = $Service; method = $Method; args = $Arguments }
    }
    $json = if ($PayloadPath) { ConvertTo-ErpJsonWithPayload -InputObject $body -FilePath $PayloadPath } else { ConvertTo-Json $body -Depth 20 -Compress }
    $response = Invoke-ErpHttp -Method POST -Uri (Join-ErpUrl $PrinterProfile.ServerUrl 'jsonrpc') -JsonBody $json -GlobalSettings $GlobalSettings
    Assert-ErpHttpSuccess -Response $response -Context "Odoo $Service.$Method"
    if (-not $response.Json) { throw (New-ErpException "Odoo returned a non-JSON response to $Service.$Method.") }
    if ($response.Json.PSObject.Properties['error'] -and $response.Json.error) {
        $err = $response.Json.error
        $name = ''
        $message = [string]$err.message
        if ($err.PSObject.Properties['data'] -and $err.data) {
            $name = [string]$err.data.name
            if ($err.data.message) { $message = [string]$err.data.message }
        }
        # Errors about the request itself will not go away by retrying.
        $permanent = $name -match '(ValidationError|UserError|ValueError|MissingError|KeyError|TypeError)$'
        throw (New-ErpException "Odoo $Service.$Method failed: $message$(if ($name) { " ($name)" })" -Permanent:$permanent)
    }
    $response.Json.result
}

function Get-ErpOdooRpcSession {
    <# Resolves (and caches) the database name and uid for the profile's login + API key. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $PrinterProfile,
        [System.Collections.IDictionary] $GlobalSettings = @{},
        [Parameter(Mandatory)] [hashtable] $Secrets,
        [switch] $Refresh
    )
    $cacheKey = "$($PrinterProfile.ServerUrl)|$($PrinterProfile.Database)|$($PrinterProfile.Username)"
    if (-not $Refresh -and $script:ErpOdooSessions.ContainsKey($cacheKey)) { return $script:ErpOdooSessions[$cacheKey] }
    $rpc = @{ PrinterProfile = $PrinterProfile; GlobalSettings = $GlobalSettings }
    $database = $PrinterProfile.Database
    if (-not $database) {
        $databases = @(Invoke-ErpOdooRpcCall @rpc -Service 'db' -Method 'list' -Arguments @())
        if ($databases.Count -ne 1) { throw (New-ErpException "Set the Database setting; the server lists $($databases.Count) databases.") }
        $database = [string]$databases[0]
    }
    $uid = Invoke-ErpOdooRpcCall @rpc -Service 'common' -Method 'authenticate' -Arguments @($database, $PrinterProfile.Username, $Secrets.ApiKey, @{})
    if (-not $uid) { throw (New-ErpException "Odoo rejected login '$($PrinterProfile.Username)' with the stored API key on database '$database'.") }
    $session = @{ Database = $database; Uid = [int]$uid }
    $script:ErpOdooSessions[$cacheKey] = $session
    $session
}

function Invoke-ErpOdooExecuteKw {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $PrinterProfile,
        [System.Collections.IDictionary] $GlobalSettings = @{},
        [Parameter(Mandatory)] [hashtable] $Secrets,
        [Parameter(Mandatory)] [string] $Model,
        [Parameter(Mandatory)] [string] $Method,
        [AllowEmptyCollection()] [object[]] $Positional = @(),
        [System.Collections.IDictionary] $Keyword = @{},
        [string] $PayloadPath
    )
    $session = Get-ErpOdooRpcSession -PrinterProfile $PrinterProfile -GlobalSettings $GlobalSettings -Secrets $Secrets
    $arguments = @($session.Database, $session.Uid, $Secrets.ApiKey, $Model, $Method, $Positional, $Keyword)
    try {
        Invoke-ErpOdooRpcCall -PrinterProfile $PrinterProfile -GlobalSettings $GlobalSettings -Service 'object' -Method 'execute_kw' -Arguments $arguments -PayloadPath $PayloadPath
    } catch {
        # Re-authenticate next time in case the key was rotated or the user changed.
        $script:ErpOdooSessions.Clear()
        throw
    }
}

function Send-ErpOdooJsonRpcDocument {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $PrinterProfile,
        [System.Collections.IDictionary] $GlobalSettings = @{},
        [Parameter(Mandatory)] $Meta,
        [Parameter(Mandatory)] [string] $PdfPath,
        [Parameter(Mandatory)] [string] $FileName,
        [Parameter(Mandatory)] [hashtable] $Secrets
    )
    $common = @{ PrinterProfile = $PrinterProfile; GlobalSettings = $GlobalSettings; Secrets = $Secrets }
    $ownerId = Resolve-ErpOdooOwnerId -PrinterProfile $PrinterProfile -Meta $Meta -SearchUser {
        param($domain)
        $users = Invoke-ErpOdooExecuteKw @common -Model 'res.users' -Method 'search_read' -Positional @(, $domain) -Keyword @{ fields = @('id'); limit = 1 }
        $first = @($users) | Select-Object -First 1
        if ($first) { $first.id }
    }
    $vals = Get-ErpOdooVals -PrinterProfile $PrinterProfile -Meta $Meta -FileName $FileName -OwnerId $ownerId
    $id = Invoke-ErpOdooExecuteKw @common -Model $PrinterProfile.OdooModel -Method 'create' -Positional @(, $vals) -PayloadPath $PdfPath
    [string](@($id) | Select-Object -First 1)
}

function Test-ErpOdooJsonRpcConnection {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $PrinterProfile,
        [System.Collections.IDictionary] $GlobalSettings = @{},
        [Parameter(Mandatory)] [hashtable] $Secrets
    )
    $session = Get-ErpOdooRpcSession -PrinterProfile $PrinterProfile -GlobalSettings $GlobalSettings -Secrets $Secrets -Refresh
    $count = Invoke-ErpOdooExecuteKw -PrinterProfile $PrinterProfile -GlobalSettings $GlobalSettings -Secrets $Secrets `
        -Model $PrinterProfile.OdooModel -Method 'search_count' -Positional @(, @())
    "Connected to Odoo database '$($session.Database)' as uid $($session.Uid). The API user can see $count $($PrinterProfile.OdooModel) records."
}
