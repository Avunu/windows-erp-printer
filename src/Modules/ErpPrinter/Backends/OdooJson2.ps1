# Odoo 19+ external JSON-2 API: POST /json/2/<model>/<method> with a bearer API key.

function Get-ErpOdooJson2Headers {
    param([System.Collections.IDictionary] $PrinterProfile, [hashtable] $Secrets)
    $headers = @{ Authorization = "bearer $($Secrets.ApiKey)" }
    if ($PrinterProfile.Database) { $headers['X-Odoo-Database'] = $PrinterProfile.Database }
    $headers
}

function Invoke-ErpOdooJson2 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $PrinterProfile,
        [System.Collections.IDictionary] $GlobalSettings = @{},
        [Parameter(Mandatory)] [hashtable] $Secrets,
        [Parameter(Mandatory)] [string] $Model,
        [Parameter(Mandatory)] [string] $Method,
        [Parameter(Mandatory)] $Body,
        [string] $PayloadPath
    )
    $json = if ($PayloadPath) { ConvertTo-ErpJsonWithPayload -InputObject $Body -FilePath $PayloadPath } else { ConvertTo-Json $Body -Depth 20 -Compress }
    $response = Invoke-ErpHttp -Method POST -Uri (Join-ErpUrl $PrinterProfile.ServerUrl "json/2/$Model/$Method") `
        -Headers (Get-ErpOdooJson2Headers $PrinterProfile $Secrets) -JsonBody $json -GlobalSettings $GlobalSettings
    if ($response.StatusCode -eq 404 -and -not $response.Json) {
        throw (New-ErpException "The server has no JSON-2 API at $($PrinterProfile.ServerUrl) (HTTP 404). For Odoo 18 and older choose the OdooJsonRpc backend.")
    }
    Assert-ErpHttpSuccess -Response $response -Context "Odoo $Model.$Method"
    $response.Json
}

function Send-ErpOdooJson2Document {
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
        $users = Invoke-ErpOdooJson2 @common -Model 'res.users' -Method 'search_read' -Body @{ domain = $domain; fields = @('id'); limit = 1 }
        $first = @($users) | Select-Object -First 1
        if ($first) { $first.id }
    }
    $vals = Get-ErpOdooVals -PrinterProfile $PrinterProfile -Meta $Meta -FileName $FileName -OwnerId $ownerId
    $result = Invoke-ErpOdooJson2 @common -Model $PrinterProfile.OdooModel -Method 'create' -Body @{ vals_list = @(, $vals) } -PayloadPath $PdfPath
    [string](@($result) | Select-Object -First 1)
}

function Test-ErpOdooJson2Connection {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $PrinterProfile,
        [System.Collections.IDictionary] $GlobalSettings = @{},
        [Parameter(Mandatory)] [hashtable] $Secrets
    )
    $count = Invoke-ErpOdooJson2 -PrinterProfile $PrinterProfile -GlobalSettings $GlobalSettings -Secrets $Secrets `
        -Model $PrinterProfile.OdooModel -Method 'search_count' -Body @{ domain = @() }
    "Connected to Odoo (JSON-2). The API user can see $count $($PrinterProfile.OdooModel) records."
}
