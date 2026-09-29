# Shared by the OdooJson2 and OdooJsonRpc backends.

$script:ErpUserCache = @{}

function Get-ErpOdooVals {
    <# Builds the create() values. 'datas' holds the payload placeholder that is replaced with base64 at send time. #>
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $PrinterProfile,
        [Parameter(Mandatory)] $Meta,
        [Parameter(Mandatory)] [string] $FileName,
        [int] $OwnerId = 0
    )
    $vals = [ordered]@{ name = $FileName; datas = '@@ERP_PAYLOAD@@' }
    switch ($PrinterProfile.OdooModel) {
        'ir.attachment' {
            $vals['mimetype'] = 'application/pdf'
            if ($PrinterProfile.OdooResModel -and $PrinterProfile.OdooResId) {
                $vals['res_model'] = $PrinterProfile.OdooResModel
                $vals['res_id'] = [int]$PrinterProfile.OdooResId
            }
            $vals['description'] = "Printed by $($Meta.user) on $($Meta.computer)"
        }
        default {
            if ($PrinterProfile.OdooFolderId -gt 0) { $vals['folder_id'] = [int]$PrinterProfile.OdooFolderId }
            if ($OwnerId -gt 0) { $vals['owner_id'] = $OwnerId }
        }
    }
    if ($PrinterProfile.OdooExtraFields) {
        $expanded = Expand-ErpTemplate -Template $PrinterProfile.OdooExtraFields -Tokens (Get-ErpTemplateTokens $Meta) -JsonEscape
        try { $extra = ConvertFrom-Json $expanded -ErrorAction Stop }
        catch { throw (New-ErpException "Extra fields are not valid JSON: $($_.Exception.Message)" -Permanent) }
        foreach ($property in $extra.PSObject.Properties) { $vals[$property.Name] = $property.Value }
    }
    $vals
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
    if (-not $PrinterProfile.UserMapping -or $PrinterProfile.OdooModel -ne 'documents.document' -or -not $Meta.user) { return 0 }
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
