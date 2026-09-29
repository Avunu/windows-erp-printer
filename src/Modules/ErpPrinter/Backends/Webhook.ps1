# Generic webhook: POST the PDF plus metadata to any URL (n8n, Power Automate, Paperless-ngx,
# a custom endpoint...). Retries reuse the document id, sent as Idempotency-Key, so receivers
# can de-duplicate.

function Get-ErpWebhookHeaders {
    param([System.Collections.IDictionary] $PrinterProfile, [hashtable] $Secrets, [string] $EventName, [string] $Id)
    $headers = @{ 'X-ErpPrinter-Event' = $EventName }
    if ($Id) { $headers['Idempotency-Key'] = $Id }
    if ($Secrets.ApiKey -and $PrinterProfile.WebhookAuthHeader) { $headers[$PrinterProfile.WebhookAuthHeader] = $Secrets.ApiKey }
    $headers
}

function Get-ErpWebhookMetadata {
    param($Meta, [string] $FileName)
    [ordered]@{
        id       = [string]$Meta.id
        fileName = $FileName
        title    = [string]$Meta.title
        user     = [string]$Meta.user
        computer = [string]$Meta.computer
        printed  = [string]$Meta.printed
        jobId    = [string]$Meta.jobId
        pages    = $Meta.pages
        profile  = [string]$Meta.profile
    }
}

function Send-ErpWebhookDocument {
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
    $metadata = Get-ErpWebhookMetadata -Meta $Meta -FileName $FileName
    $headers = Get-ErpWebhookHeaders -PrinterProfile $PrinterProfile -Secrets $Secrets -EventName 'document' -Id $Meta.id
    if ($PrinterProfile.WebhookFormat -eq 'Json') {
        $body = [ordered]@{ contentType = 'application/pdf'; contentBase64 = '@@ERP_PAYLOAD@@'; metadata = $metadata }
        $json = ConvertTo-ErpJsonWithPayload -InputObject $body -FilePath $PdfPath
        $response = Invoke-ErpHttp -Method POST -Uri $PrinterProfile.ServerUrl -Headers $headers -JsonBody $json -GlobalSettings $GlobalSettings
    } else {
        $fields = [ordered]@{}
        foreach ($key in $metadata.Keys) { $fields[$key] = [string]$metadata[$key] }
        $fields['metadata'] = ConvertTo-Json $metadata -Compress
        $field = if ($PrinterProfile.WebhookFileField) { $PrinterProfile.WebhookFileField } else { 'file' }
        $content = New-ErpMultipartContent -Fields $fields -FileField $field -FilePath $PdfPath -FileName $FileName
        try {
            $response = Invoke-ErpHttp -Method POST -Uri $PrinterProfile.ServerUrl -Headers $headers -Content $content -GlobalSettings $GlobalSettings
        } finally { $content.Dispose() }
    }
    Assert-ErpHttpSuccess -Response $response -Context 'Webhook'
    if ($response.Json -and $response.Json.PSObject.Properties['id']) { return [string]$response.Json.id }
    "HTTP $($response.StatusCode)"
}

function Test-ErpWebhookConnection {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $PrinterProfile,
        [System.Collections.IDictionary] $GlobalSettings = @{},
        [Parameter(Mandatory)] [hashtable] $Secrets
    )
    $headers = Get-ErpWebhookHeaders -PrinterProfile $PrinterProfile -Secrets $Secrets -EventName 'test'
    $json = ConvertTo-Json @{ event = 'test'; computer = $env:COMPUTERNAME; profile = $PrinterProfile.Name } -Compress
    $response = Invoke-ErpHttp -Method POST -Uri $PrinterProfile.ServerUrl -Headers $headers -JsonBody $json -GlobalSettings $GlobalSettings
    Assert-ErpHttpSuccess -Response $response -Context 'Webhook test'
    "Webhook answered HTTP $($response.StatusCode)."
}
