# The settings schema is the single source of truth for configuration: the registry
# layer, validation, the configuration GUI and the ADMX policy templates all follow it.

$script:ErpBackendNames = @('OdooJson2', 'OdooJsonRpc', 'ERPNext', 'Webhook')
$script:ErpOdooBackends = @('OdooJson2', 'OdooJsonRpc')

function New-ErpSettingDefinition {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [ValidateSet('String', 'Int', 'Bool', 'Choice', 'Secret')] [string] $Type,
        [Parameter(Mandatory)] [string] $Label,
        [Parameter(Mandatory)] [string] $Group,
        [object] $Default = $null,
        [string] $Help = '',
        [string[]] $Backends = @(),
        [string[]] $Choices = @(),
        [int] $Min = 0,
        [int] $Max = [int]::MaxValue,
        [string] $DefaultText,
        [switch] $Required
    )
    [pscustomobject]@{
        Name     = $Name
        Type     = $Type
        Label    = $Label
        Group    = $Group
        Default  = $Default
        Help     = $Help
        Backends = $Backends
        Choices  = $Choices
        Min      = $Min
        Max         = $Max
        DefaultText = $DefaultText
        Required    = [bool]$Required
    }
}

$script:ErpGlobalSchema = @(
    New-ErpSettingDefinition -Name DataRoot -Type String -Group 'Storage' -Label 'Data folder' `
        -Default { Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'ErpPrinter' } -DefaultText '%ProgramData%\ErpPrinter' `
        -Help 'Outbox, failed/sent archives, logs and downloaded updates are stored here. Locked to SYSTEM and Administrators.'
    New-ErpSettingDefinition -Name PollIntervalSeconds -Type Int -Group 'Delivery' -Label 'Poll interval (seconds)' -Default 15 -Min 2 -Max 3600 `
        -Help 'How often the uploader re-scans the outbox. New print jobs wake it immediately.'
    New-ErpSettingDefinition -Name MaxAttempts -Type Int -Group 'Delivery' -Label 'Max delivery attempts' -Default 0 -Min 0 -Max 100000 `
        -Help 'After this many failed attempts a document is moved to the failed folder. 0 retries forever.'
    New-ErpSettingDefinition -Name RetryMaxBackoffMinutes -Type Int -Group 'Delivery' -Label 'Max retry backoff (minutes)' -Default 60 -Min 1 -Max 1440 `
        -Help 'Retries back off exponentially from 30 seconds up to this ceiling.'
    New-ErpSettingDefinition -Name MaxDocumentSizeMB -Type Int -Group 'Delivery' -Label 'Max document size (MB)' -Default 64 -Min 1 -Max 2048 `
        -Help 'Larger documents are moved to the failed folder instead of being uploaded.'
    New-ErpSettingDefinition -Name HttpTimeoutSeconds -Type Int -Group 'Delivery' -Label 'HTTP timeout (seconds)' -Default 120 -Min 5 -Max 3600
    New-ErpSettingDefinition -Name ProxyUrl -Type String -Group 'Delivery' -Label 'Proxy URL' -Default '' `
        -Help 'Optional, e.g. http://proxy:8080. The service runs as SYSTEM and does not see per-user proxy settings.'
    New-ErpSettingDefinition -Name KeepSentDays -Type Int -Group 'Storage' -Label 'Keep sent documents (days)' -Default 0 -Min 0 -Max 3650 `
        -Help '0 deletes documents as soon as they are delivered.'
    New-ErpSettingDefinition -Name LogLevel -Type Choice -Group 'Storage' -Label 'Log level' -Default 'Information' `
        -Choices @('Error', 'Warning', 'Information', 'Verbose')
    New-ErpSettingDefinition -Name LogRetentionDays -Type Int -Group 'Storage' -Label 'Keep logs (days)' -Default 14 -Min 1 -Max 3650
    New-ErpSettingDefinition -Name AutoUpdate -Type Bool -Group 'Updates' -Label 'Install updates automatically' -Default $true
    New-ErpSettingDefinition -Name UpdateManifestUrl -Type String -Group 'Updates' -Label 'Update manifest URL' `
        -Default { $script:ErpBuildInfo.UpdateManifestUrl } -DefaultText 'latest.json of the latest release of this project' `
        -Help 'HTTPS URL or UNC path of latest.json. Defaults to the latest GitHub release of this project.'
    New-ErpSettingDefinition -Name RequireSignedUpdates -Type Bool -Group 'Updates' -Label 'Require Authenticode-signed updates' -Default $false
    New-ErpSettingDefinition -Name UpdateSignerThumbprint -Type String -Group 'Updates' -Label 'Update signer thumbprint' -Default '' `
        -Help 'If set, update packages must be signed by this certificate.'
)

$script:ErpProfileSchema = @(
    New-ErpSettingDefinition -Name Enabled -Type Bool -Group 'Printer' -Label 'Enabled' -Default $true
    New-ErpSettingDefinition -Name PrinterName -Type String -Group 'Printer' -Label 'Printer name' `
        -Default { param($ctx) $ctx.Name } -Help 'Name of the Windows printer users will see.'
    New-ErpSettingDefinition -Name PipeName -Type String -Group 'Printer' -Label 'Pipe name' `
        -Default { param($ctx) 'ErpPrinter-' + (ConvertTo-ErpSafeName $ctx.Name) } `
        -Help 'The printer port is \\.\pipe\<name>. Change only if it collides with something else.'
    New-ErpSettingDefinition -Name FileNameTemplate -Type String -Group 'Printer' -Label 'File name template' -Default '{title}' `
        -Help 'Tokens: {title} {user} {username} {domain} {computer} {date} {time} {datetime} {id} {jobid} {profile}.'
    New-ErpSettingDefinition -Name Backend -Type Choice -Group 'Connection' -Label 'Backend' -Default 'OdooJson2' -Choices $script:ErpBackendNames `
        -Help 'OdooJson2: Odoo 19+ JSON-2 API. OdooJsonRpc: Odoo 14-18 /jsonrpc. ERPNext: Frappe upload_file. Webhook: POST to any URL.'
    New-ErpSettingDefinition -Name ServerUrl -Type String -Group 'Connection' -Label 'Server URL' -Default '' -Required `
        -Help 'Base URL of the ERP (https://example.odoo.com) or, for Webhook, the full endpoint URL.'
    New-ErpSettingDefinition -Name Database -Type String -Group 'Connection' -Label 'Database' -Default '' -Backends $script:ErpOdooBackends `
        -Help 'Needed when the Odoo server hosts several databases.'
    New-ErpSettingDefinition -Name Username -Type String -Group 'Connection' -Label 'Login' -Default '' -Backends @('OdooJsonRpc') -Required `
        -Help 'Login of the Odoo integration user that owns the API key.'
    New-ErpSettingDefinition -Name ApiKey -Type Secret -Group 'Connection' -Label 'API key' -Default '' `
        -Help 'Stored encrypted (DPAPI, machine scope) in a registry key only SYSTEM and Administrators can read. For Webhook this is the auth header value.'
    New-ErpSettingDefinition -Name ApiSecret -Type Secret -Group 'Connection' -Label 'API secret' -Default '' -Backends @('ERPNext') -Required
    New-ErpSettingDefinition -Name OdooModel -Type Choice -Group 'Destination' -Label 'Odoo model' -Default 'documents.document' `
        -Choices @('documents.document', 'ir.attachment') -Backends $script:ErpOdooBackends `
        -Help 'documents.document needs the Documents app (Enterprise). ir.attachment works on Community.'
    New-ErpSettingDefinition -Name OdooFolderId -Type Int -Group 'Destination' -Label 'Documents folder ID' -Default 0 -Backends $script:ErpOdooBackends `
        -Help 'Target folder for documents.document. 0 leaves it unset.'
    New-ErpSettingDefinition -Name OdooResModel -Type String -Group 'Destination' -Label 'Attach to model' -Default '' -Backends $script:ErpOdooBackends `
        -Help 'For ir.attachment: model of the record to attach to, e.g. res.partner.'
    New-ErpSettingDefinition -Name OdooResId -Type Int -Group 'Destination' -Label 'Attach to record ID' -Default 0 -Backends $script:ErpOdooBackends
    New-ErpSettingDefinition -Name OdooExtraFields -Type String -Group 'Destination' -Label 'Extra fields (JSON)' -Default '' -Backends $script:ErpOdooBackends `
        -Help 'JSON object merged into the created record. Template tokens are expanded, e.g. {"description": "Printed by {user}"}.'
    New-ErpSettingDefinition -Name UserMapping -Type Bool -Group 'Destination' -Label 'Map Windows users to Odoo users' -Default $false -Backends $script:ErpOdooBackends `
        -Help 'Looks up the printing user in res.users (login or email) and sets owner_id on documents.document.'
    New-ErpSettingDefinition -Name UserEmailDomain -Type String -Group 'Destination' -Label 'User email domain' -Default '' -Backends $script:ErpOdooBackends `
        -Help 'Optional. Also tries <username>@<domain> when mapping users. Active Directory mail/UPN are tried automatically.'
    New-ErpSettingDefinition -Name ERPNextFolder -Type String -Group 'Destination' -Label 'File folder' -Default 'Home' -Backends @('ERPNext')
    New-ErpSettingDefinition -Name ERPNextDoctype -Type String -Group 'Destination' -Label 'Attach to DocType' -Default '' -Backends @('ERPNext')
    New-ErpSettingDefinition -Name ERPNextDocname -Type String -Group 'Destination' -Label 'Attach to document' -Default '' -Backends @('ERPNext')
    New-ErpSettingDefinition -Name ERPNextIsPrivate -Type Bool -Group 'Destination' -Label 'Private file' -Default $true -Backends @('ERPNext')
    New-ErpSettingDefinition -Name WebhookFormat -Type Choice -Group 'Destination' -Label 'Payload format' -Default 'Multipart' `
        -Choices @('Multipart', 'Json') -Backends @('Webhook') `
        -Help 'Multipart: file plus metadata form fields. Json: base64 content plus metadata.'
    New-ErpSettingDefinition -Name WebhookFileField -Type String -Group 'Destination' -Label 'File field name' -Default 'file' -Backends @('Webhook')
    New-ErpSettingDefinition -Name WebhookAuthHeader -Type String -Group 'Destination' -Label 'Auth header name' -Default 'Authorization' -Backends @('Webhook') `
        -Help 'The API key is sent as the value of this header, if one is set.'
)

function Get-ErpSettingDefault {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Setting, [hashtable] $Context = @{})
    if ($Setting.Default -is [scriptblock]) { return (& $Setting.Default $Context) }
    $Setting.Default
}

function ConvertTo-ErpSettingValue {
    <# Coerces a raw registry/user value to the setting's type, or throws if it is invalid. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Setting, [AllowNull()] $Value)
    switch ($Setting.Type) {
        'Bool' {
            if ($Value -is [bool]) { return $Value }
            if ($null -eq $Value -or "$Value" -eq '') { return $false }
            if ("$Value" -match '^(1|true|yes|on)$') { return $true }
            if ("$Value" -match '^(0|false|no|off)$') { return $false }
            throw "Setting '$($Setting.Name)' expects a boolean, got '$Value'."
        }
        'Int' {
            $n = 0
            if (-not [int]::TryParse("$Value", [ref]$n)) { throw "Setting '$($Setting.Name)' expects a whole number, got '$Value'." }
            if ($n -lt $Setting.Min -or $n -gt $Setting.Max) { throw "Setting '$($Setting.Name)' must be between $($Setting.Min) and $($Setting.Max)." }
            return $n
        }
        'Choice' {
            $match = $Setting.Choices | Where-Object { $_ -eq "$Value" } | Select-Object -First 1
            if (-not $match) { throw "Setting '$($Setting.Name)' must be one of: $($Setting.Choices -join ', ')." }
            return $match
        }
        default { if ($null -eq $Value) { return '' } return "$Value" }
    }
}

function ConvertTo-ErpSafeName {
    <# Makes a string safe for use in file, folder and pipe names. #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Name)
    $safe = ($Name -replace '[^A-Za-z0-9._-]+', '_').Trim('_', '.')
    if (-not $safe) { $safe = 'default' }
    $safe
}

function Assert-ErpProfileName {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Name)
    if ($Name -notmatch '^[\w][\w .()&+-]{0,63}$') {
        throw "Invalid printer profile name '$Name'. Use up to 64 letters, digits, spaces and . ( ) & + - _"
    }
}
