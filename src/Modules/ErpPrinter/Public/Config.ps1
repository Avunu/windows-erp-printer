# Configuration lives in the registry:
#
#   HKLM\SOFTWARE\ErpPrinter                     global settings (GUI, MSI, scripts)
#   HKLM\SOFTWARE\ErpPrinter\Printers\<name>     one key per printer profile
#   HKLM\SOFTWARE\ErpPrinter\Secrets\<name>      DPAPI blobs, SYSTEM + Administrators only
#   HKLM\SOFTWARE\Policies\ErpPrinter[\Printers\<name>]
#                                                 same layout, pushed by GPO/Intune; wins over the above
#
# Effective value = policy ?? machine ?? schema default.

function Resolve-ErpSettings {
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param(
        [Parameter(Mandatory)] [object[]] $Schema,
        [hashtable] $Machine = @{},
        [hashtable] $Policy = @{},
        [hashtable] $Context = @{}
    )
    $result = [ordered]@{}
    $managed = New-Object System.Collections.Generic.List[string]
    foreach ($setting in $Schema) {
        if ($setting.Type -eq 'Secret') { continue }
        $value = Get-ErpSettingDefault -Setting $setting -Context $Context
        foreach ($layer in @(@{ Source = $Machine; Managed = $false }, @{ Source = $Policy; Managed = $true })) {
            if ($layer.Source.ContainsKey($setting.Name)) {
                try {
                    $value = ConvertTo-ErpSettingValue -Setting $setting -Value $layer.Source[$setting.Name]
                    if ($layer.Managed) { $managed.Add($setting.Name) }
                } catch {
                    Write-ErpLog -Level Warning -Message "Ignoring invalid registry value: $($_.Exception.Message)"
                }
            }
        }
        $result[$setting.Name] = $value
    }
    $result['ManagedSettings'] = $managed.ToArray()
    $result
}

function Get-ErpPrinterProfile {
    <#
    .SYNOPSIS
        Returns the effective settings of one printer profile, or of all profiles.
    #>
    [CmdletBinding()]
    param([string] $Name)
    $names = @(Get-ErpRegistrySubKeyNames -Path "$script:ErpRegistryRoot\Printers") +
             @(Get-ErpRegistrySubKeyNames -Path "$script:ErpPolicyRoot\Printers") |
             Sort-Object -Unique
    if ($Name) { $names = @($names | Where-Object { $_ -eq $Name }) }
    foreach ($profileName in $names) {
        $machine = Get-ErpRegistryValues -Path "$script:ErpRegistryRoot\Printers\$profileName"
        $policy = Get-ErpRegistryValues -Path "$script:ErpPolicyRoot\Printers\$profileName"
        $settings = Resolve-ErpSettings -Schema $script:ErpProfileSchema -Machine $machine -Policy $policy -Context @{ Name = $profileName }
        $settings.Insert(0, 'Name', $profileName)
        $settings['PolicyDefined'] = -not (Test-ErpRegistryKeyInList -Name $profileName -Path "$script:ErpRegistryRoot\Printers")
        $settings
    }
}

function Test-ErpRegistryKeyInList {
    param([string] $Name, [string] $Path)
    @(Get-ErpRegistrySubKeyNames -Path $Path) -contains $Name
}

function Get-ErpPrinterConfig {
    <#
    .SYNOPSIS
        Returns the effective configuration: Global settings plus all printer profiles.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param()
    $globalSettings = Resolve-ErpSettings -Schema $script:ErpGlobalSchema `
        -Machine (Get-ErpRegistryValues -Path $script:ErpRegistryRoot) `
        -Policy (Get-ErpRegistryValues -Path $script:ErpPolicyRoot)
    [pscustomobject]@{
        Global   = $globalSettings
        Profiles = @(Get-ErpPrinterProfile)
    }
}

function Get-ErpSettingDefinition {
    param([Parameter(Mandatory)] [string] $Name, [ValidateSet('Global', 'Profile')] [string] $Scope)
    $schema = if ($Scope -eq 'Global') { $script:ErpGlobalSchema } else { $script:ErpProfileSchema }
    $setting = $schema | Where-Object { $_.Name -eq $Name } | Select-Object -First 1
    if (-not $setting) { throw "Unknown $($Scope.ToLower()) setting '$Name'." }
    $setting
}

function Set-ErpPrinterSetting {
    <#
    .SYNOPSIS
        Writes a global or per-profile setting to HKLM\SOFTWARE\ErpPrinter.
    .DESCRIPTION
        Values equal to the default are removed rather than stored, so defaults can change
        in later versions. Use -Clear to remove an explicit value. Policy values still win.
    .EXAMPLE
        Set-ErpPrinterSetting -ProfileName 'Send to Odoo' -Name OdooFolderId -Value 42
    #>
    [CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'Set')]
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory, ParameterSetName = 'Set')] [AllowEmptyString()] [AllowNull()] $Value,
        [Parameter(Mandatory, ParameterSetName = 'Clear')] [switch] $Clear,
        [string] $ProfileName
    )
    $scope = if ($ProfileName) { 'Profile' } else { 'Global' }
    $setting = Get-ErpSettingDefinition -Name $Name -Scope $scope
    if ($setting.Type -eq 'Secret') { throw "'$Name' is a secret. Use Set-ErpPrinterSecret." }
    $path = $script:ErpRegistryRoot
    if ($ProfileName) {
        Assert-ErpProfileName $ProfileName
        $path = "$script:ErpRegistryRoot\Printers\$ProfileName"
    }
    if ($Clear) {
        if ($PSCmdlet.ShouldProcess("$path\$Name", 'Clear setting')) { Remove-ErpRegistryValue -Path $path -Name $Name }
        return
    }
    $typed = ConvertTo-ErpSettingValue -Setting $setting -Value $Value
    if (-not $PSCmdlet.ShouldProcess("$path\$Name", "Set to '$typed'")) { return }
    $default = Get-ErpSettingDefault -Setting $setting -Context @{ Name = $ProfileName }
    if ($typed -eq $default -and $setting.Name -ne 'Backend') {
        # Keep the profile key itself so an all-defaults profile still exists.
        if ($ProfileName) { Initialize-ErpRegistryKey -Path $path }
        Remove-ErpRegistryValue -Path $path -Name $Name
        return
    }
    switch ($setting.Type) {
        'Bool' { Set-ErpRegistryValue -Path $path -Name $Name -Value ([int]$typed) -Kind DWord }
        'Int'  { Set-ErpRegistryValue -Path $path -Name $Name -Value $typed -Kind DWord }
        default { Set-ErpRegistryValue -Path $path -Name $Name -Value $typed -Kind String }
    }
}

function New-ErpPrinterProfile {
    <#
    .SYNOPSIS
        Creates a printer profile. Run Sync-ErpPrinterQueue afterwards to create the Windows printer.
    .EXAMPLE
        New-ErpPrinterProfile -Name 'Send to Odoo' -Settings @{ Backend = 'OdooJson2'; ServerUrl = 'https://acme.odoo.com'; OdooFolderId = 7 }
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string] $Name,
        [hashtable] $Settings = @{},
        [switch] $Force
    )
    Assert-ErpProfileName $Name
    $path = "$script:ErpRegistryRoot\Printers\$Name"
    if ((Test-ErpRegistryKey -Path $path) -and -not $Force) { throw "Printer profile '$Name' already exists." }
    if (-not $PSCmdlet.ShouldProcess($Name, 'Create printer profile')) { return }
    Initialize-ErpRegistryKey -Path $path
    if (-not $Settings.ContainsKey('Backend')) { $Settings['Backend'] = 'OdooJson2' }
    foreach ($key in $Settings.Keys) {
        $setting = Get-ErpSettingDefinition -Name $key -Scope Profile
        if ($setting.Type -eq 'Secret') {
            if ($Settings[$key]) { Set-ErpPrinterSecret -ProfileName $Name -Name $key -Value $Settings[$key] }
        } else {
            Set-ErpPrinterSetting -ProfileName $Name -Name $key -Value $Settings[$key]
        }
    }
}

function Remove-ErpPrinterProfile {
    <#
    .SYNOPSIS
        Deletes a printer profile and its secrets. Run Sync-ErpPrinterQueue afterwards to remove the printer.
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param([Parameter(Mandatory, ValueFromPipelineByPropertyName)] [string] $Name)
    process {
        if ($PSCmdlet.ShouldProcess($Name, 'Remove printer profile')) {
            Remove-ErpRegistryKey -Path "$script:ErpRegistryRoot\Printers\$Name"
            Remove-ErpRegistryKey -Path (Get-ErpSecretPath $Name)
        }
    }
}

function Set-ErpPrinterSecret {
    <#
    .SYNOPSIS
        Stores an API key or secret for a profile, encrypted with machine-scope DPAPI.
    .EXAMPLE
        Set-ErpPrinterSecret -ProfileName 'Send to Odoo' -Name ApiKey -Value (Read-Host -AsSecureString)
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string] $ProfileName,
        [Parameter(Mandatory)] [ValidateSet('ApiKey', 'ApiSecret')] [string] $Name,
        [Parameter(Mandatory)] [AllowEmptyString()] $Value
    )
    Assert-ErpProfileName $ProfileName
    $plain = if ($Value -is [Security.SecureString]) { ConvertFrom-ErpSecureString $Value } else { [string]$Value }
    if (-not $PSCmdlet.ShouldProcess("$ProfileName/$Name", 'Store secret')) { return }
    Initialize-ErpSecretsKey
    $path = Get-ErpSecretPath $ProfileName
    if (-not $plain) {
        Remove-ErpRegistryValue -Path $path -Name $Name
        return
    }
    Set-ErpRegistryValue -Path $path -Name $Name -Value (Protect-ErpSecret -PlainText $plain) -Kind Binary
}

function Test-ErpPrinterSecret {
    <# Returns $true if a secret is stored for the profile (without decrypting it). #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)] [string] $ProfileName,
        [Parameter(Mandatory)] [ValidateSet('ApiKey', 'ApiSecret')] [string] $Name
    )
    $values = Get-ErpRegistryValues -Path (Get-ErpSecretPath $ProfileName)
    [bool]($values.ContainsKey($Name) -and $values[$Name])
}

function Test-ErpPrinterProfile {
    <#
    .SYNOPSIS
        Validates a profile. Returns a list of problems; an empty list means it is usable.
    .PARAMETER Secrets
        Secrets to validate instead of the stored ones (used by the GUI before saving).
    .PARAMETER AllProfiles
        Other profiles, to detect clashing printer and pipe names.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'Secrets', Justification = 'Used by the $hasSecret scriptblock.')]
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $PrinterProfile,
        [hashtable] $Secrets,
        [object[]] $AllProfiles = @()
    )
    $problems = New-Object System.Collections.Generic.List[string]
    $backend = $PrinterProfile.Backend
    if ($script:ErpBackendNames -notcontains $backend) { $problems.Add("Unknown backend '$backend'.") }
    $uri = $null
    if (-not $PrinterProfile.ServerUrl) {
        $problems.Add('Server URL is required.')
    } elseif (-not [Uri]::TryCreate($PrinterProfile.ServerUrl, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -notin 'http', 'https') {
        $problems.Add("Server URL '$($PrinterProfile.ServerUrl)' is not an http(s) URL.")
    }
    if (-not $PrinterProfile.PrinterName) { $problems.Add('Printer name is required.') }
    if ($PrinterProfile.PipeName -notmatch '^[A-Za-z0-9._-]{1,200}$') { $problems.Add('Pipe name may only contain letters, digits, dot, dash and underscore.') }
    $hasSecret = {
        param($secretName)
        if ($Secrets -and $Secrets.ContainsKey($secretName) -and $Secrets[$secretName]) { return $true }
        Test-ErpPrinterSecret -ProfileName $PrinterProfile.Name -Name $secretName
    }
    if ($backend -ne 'Webhook' -and -not (& $hasSecret 'ApiKey')) { $problems.Add('An API key is required.') }
    if ($backend -eq 'ERPNext' -and -not (& $hasSecret 'ApiSecret')) { $problems.Add('ERPNext needs an API secret as well as an API key.') }
    if ($backend -eq 'OdooJsonRpc' -and -not $PrinterProfile.Username) { $problems.Add('Odoo JSON-RPC needs the login of the API user.') }
    if ($script:ErpOdooBackends -contains $backend) {
        if ($PrinterProfile.OdooModel -eq 'ir.attachment' -and [bool]$PrinterProfile.OdooResModel -ne [bool]$PrinterProfile.OdooResId) {
            $problems.Add('Set both "Attach to model" and "Attach to record ID", or neither.')
        }
        if ($PrinterProfile.OdooExtraFields) {
            try { $null = ConvertFrom-Json $PrinterProfile.OdooExtraFields -ErrorAction Stop }
            catch { $problems.Add('Extra fields must be a JSON object.') }
        }
    }
    if ($backend -eq 'ERPNext' -and [bool]$PrinterProfile.ERPNextDoctype -ne [bool]$PrinterProfile.ERPNextDocname) {
        $problems.Add('Set both "Attach to DocType" and "Attach to document", or neither.')
    }
    foreach ($other in $AllProfiles) {
        if ($other.Name -eq $PrinterProfile.Name) { continue }
        if ($other.PrinterName -eq $PrinterProfile.PrinterName) { $problems.Add("Printer name '$($PrinterProfile.PrinterName)' is also used by profile '$($other.Name)'.") }
        if ($other.PipeName -eq $PrinterProfile.PipeName) { $problems.Add("Pipe name '$($PrinterProfile.PipeName)' is also used by profile '$($other.Name)'.") }
    }
    $problems.ToArray()
}
