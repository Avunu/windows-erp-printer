# Shared by the OdooJson2 and OdooJsonRpc backends.

$script:ErpUserCache = @{}

function Get-ErpOdooVals {
    <#
    Builds the create() values, the way an Odoo mail alias builds a record: the file name and
    the mapped user first, then the profile's default values on top. The file field comes last
    and holds the payload placeholder that is replaced with base64 at send time.
    #>
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $PrinterProfile,
        [Parameter(Mandatory)] $Meta,
        [Parameter(Mandatory)] [string] $FileName,
        [int] $OwnerId = 0
    )
    if (-not $PrinterProfile.OdooFileField) { throw (New-ErpException 'The profile has no File field to put the document in.' -Permanent) }
    $vals = [ordered]@{}
    if ($PrinterProfile.OdooNameField) { $vals[$PrinterProfile.OdooNameField] = $FileName }
    if ($OwnerId -gt 0 -and $PrinterProfile.OdooUserField) { $vals[$PrinterProfile.OdooUserField] = $OwnerId }
    if ($PrinterProfile.OdooDefaultValues) {
        $expanded = Expand-ErpTemplate -Template $PrinterProfile.OdooDefaultValues -Tokens (Get-ErpTemplateTokens $Meta) -JsonEscape
        try { $defaults = ConvertFrom-ErpJsonObject $expanded }
        catch { throw (New-ErpException "Default values are not a valid JSON object: $($_.Exception.Message)" -Permanent) }
        foreach ($property in $defaults.PSObject.Properties) { $vals[$property.Name] = $property.Value }
    }
    $vals[$PrinterProfile.OdooFileField] = '@@ERP_PAYLOAD@@'
    $vals
}

function Get-ErpOdooFieldCheckMessage {
    <#
    For Test connection: confirms the model has every field the profile writes and that the file
    field is binary, so a typo shows up now and not on the first printed document.
    $FieldsGet is a backend-specific scriptblock: param($model, $fieldNames) -> field definitions by name.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $PrinterProfile,
        [Parameter(Mandatory)] [scriptblock] $FieldsGet
    )
    $names = New-Object System.Collections.Generic.List[string]
    $names.Add($PrinterProfile.OdooFileField)
    if ($PrinterProfile.OdooNameField) { $names.Add($PrinterProfile.OdooNameField) }
    if ($PrinterProfile.UserMapping -and $PrinterProfile.OdooUserField) { $names.Add($PrinterProfile.OdooUserField) }
    if ($PrinterProfile.OdooDefaultValues) {
        foreach ($property in (ConvertFrom-ErpJsonObject $PrinterProfile.OdooDefaultValues).PSObject.Properties) { $names.Add($property.Name) }
    }
    $names = @($names | Select-Object -Unique)
    $known = & $FieldsGet $PrinterProfile.OdooModel $names
    $missing = @($names | Where-Object { -not $known -or -not $known.PSObject.Properties[$_] })
    if ($missing.Count) {
        throw (New-ErpException "Model $($PrinterProfile.OdooModel) has no field named $($missing -join ', ').")
    }
    $fileType = $known.PSObject.Properties[$PrinterProfile.OdooFileField].Value.type
    if ($fileType -ne 'binary') {
        throw (New-ErpException "File field $($PrinterProfile.OdooFileField) is of type $fileType; it must be a binary field.")
    }
    " Fields found: $($names -join ', ')."
}

function Get-ErpUserIdentityCandidates {
    <#
    Returns the strings an Odoo login or email might match for a Windows user:
    the bare user name, <user>@<UserEmailDomain>, and the AD mail and UPN when available.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param([AllowEmptyString()] [string] $User, [string] $EmailDomain)
    if (-not $User) { return @() }
    $sam = $User
    if ($User -match '^[^\\]+\\(?<u>.+)$') { $sam = $Matches.u }
    $candidates = New-Object System.Collections.Generic.List[string]
    $candidates.Add($User)
    $candidates.Add($sam)
    if ($EmailDomain) { $candidates.Add("$sam@$($EmailDomain.TrimStart('@'))") }
    foreach ($value in Get-ErpDirectoryIdentity -SamAccountName $sam) { $candidates.Add($value) }
    @($candidates | Where-Object { $_ } | ForEach-Object { $_.ToLowerInvariant() } | Select-Object -Unique)
}

function Get-ErpDirectoryIdentity {
    <# Looks up mail and userPrincipalName in Active Directory; returns nothing off-domain. #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param([Parameter(Mandatory)] [string] $SamAccountName)
    if (-not $script:ErpIsWindows) { return @() }
    try {
        $escaped = [regex]::Replace($SamAccountName, '[\\*()\x00]', { param($m) '\{0:x2}' -f [int][char]$m.Value })
        $searcher = New-Object System.DirectoryServices.DirectorySearcher
        $searcher.Filter = "(&(objectCategory=person)(sAMAccountName=$escaped))"
        $searcher.ClientTimeout = [TimeSpan]::FromSeconds(5)
        $null = $searcher.PropertiesToLoad.Add('mail')
        $null = $searcher.PropertiesToLoad.Add('userprincipalname')
        $result = $searcher.FindOne()
        if (-not $result) { return @() }
        @($result.Properties['mail']) + @($result.Properties['userprincipalname']) | ForEach-Object { [string]$_ }
    } catch {
        Write-ErpLog -Level Verbose -Message "Directory lookup for '$SamAccountName' skipped: $($_.Exception.Message)"
        @()
    }
}

function Resolve-ErpOdooOwnerId {
    <#
    Maps the printing Windows user to a res.users id, caching hits for a day and misses
    for an hour. $SearchUser is a backend-specific scriptblock: param($domain) -> id or $null.
    #>
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $PrinterProfile,
        [Parameter(Mandatory)] $Meta,
        [Parameter(Mandatory)] [scriptblock] $SearchUser
    )
    if (-not $PrinterProfile.UserMapping -or -not $PrinterProfile.OdooUserField -or -not $Meta.user) { return 0 }
    $cacheKey = "$($PrinterProfile.Name)|$($Meta.user)".ToLowerInvariant()
    $cached = $script:ErpUserCache[$cacheKey]
    if ($cached -and $cached.Expires -gt (Get-Date)) { return $cached.Id }

    # @() keeps a single candidate as a list; Odoo's 'in' operator needs one.
    $candidates = @(Get-ErpUserIdentityCandidates -User $Meta.user -EmailDomain $PrinterProfile.UserEmailDomain)
    $domain = @('|', @('login', 'in', $candidates), @('email', 'in', $candidates))
    $id = 0
    try {
        $found = & $SearchUser $domain
        if ($found) { $id = [int]$found }
    } catch {
        Write-ErpLog -Level Warning -Message "User lookup for '$($Meta.user)' failed: $($_.Exception.Message)"
        return 0
    }
    $ttl = if ($id) { 24 } else { 1 }
    $script:ErpUserCache[$cacheKey] = @{ Id = $id; Expires = (Get-Date).AddHours($ttl) }
    if (-not $id) { Write-ErpLog -Level Information -Message "No Odoo user matches '$($Meta.user)' (tried: $($candidates -join ', '))." }
    $id
}
