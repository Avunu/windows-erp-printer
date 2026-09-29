# ERPNext / Frappe: multipart POST to /api/method/upload_file with token auth.

function Get-ErpERPNextHeaders {
    param([hashtable] $Secrets)
    @{ Authorization = "token $($Secrets.ApiKey):$($Secrets.ApiSecret)"; Accept = 'application/json' }
}

function Send-ErpERPNextDocument {
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
    $fields = [ordered]@{
        is_private = if ($PrinterProfile.ERPNextIsPrivate) { '1' } else { '0' }
        folder     = if ($PrinterProfile.ERPNextFolder) { $PrinterProfile.ERPNextFolder } else { 'Home' }
        file_name  = $FileName
    }
    if ($PrinterProfile.ERPNextDoctype -and $PrinterProfile.ERPNextDocname) {
        $fields['doctype'] = $PrinterProfile.ERPNextDoctype
        $fields['docname'] = Expand-ErpTemplate -Template $PrinterProfile.ERPNextDocname -Tokens (Get-ErpTemplateTokens $Meta)
    }
    $content = New-ErpMultipartContent -Fields $fields -FileField 'file' -FilePath $PdfPath -FileName $FileName
    try {
        $response = Invoke-ErpHttp -Method POST -Uri (Join-ErpUrl $PrinterProfile.ServerUrl 'api/method/upload_file') `
            -Headers (Get-ErpERPNextHeaders $Secrets) -Content $content -GlobalSettings $GlobalSettings
    } finally { $content.Dispose() }
    Assert-ErpHttpSuccess -Response $response -Context 'ERPNext upload_file'
    if ($response.Json -and $response.Json.PSObject.Properties['message'] -and $response.Json.message) {
        return [string]$response.Json.message.name
    }
    ''
}

function Test-ErpERPNextConnection {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $PrinterProfile,
        [System.Collections.IDictionary] $GlobalSettings = @{},
        [Parameter(Mandatory)] [hashtable] $Secrets
    )
    $response = Invoke-ErpHttp -Method GET -Uri (Join-ErpUrl $PrinterProfile.ServerUrl 'api/method/frappe.auth.get_logged_user') `
        -Headers (Get-ErpERPNextHeaders $Secrets) -GlobalSettings $GlobalSettings
    Assert-ErpHttpSuccess -Response $response -Context 'ERPNext login check'
    "Connected to ERPNext as $($response.Json.message)."
}
