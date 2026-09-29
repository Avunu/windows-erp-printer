# Thin registry wrappers. Everything else goes through these so tests can mock them
# and so the rest of the module never touches the registry provider directly.

function Get-ErpRegistryValues {
    [CmdletBinding()]
    [OutputType([hashtable])]
    param([Parameter(Mandatory)] [string] $Path)
    $values = @{}
    $key = Get-Item -LiteralPath $Path -ErrorAction SilentlyContinue
    if ($key) {
        foreach ($name in $key.GetValueNames()) {
            if ($name) { $values[$name] = $key.GetValue($name) }
        }
        $key.Close()
    }
    $values
}

function Get-ErpRegistrySubKeyNames {
    [CmdletBinding()]
    [OutputType([string[]])]
    param([Parameter(Mandatory)] [string] $Path)
    $key = Get-Item -LiteralPath $Path -ErrorAction SilentlyContinue
    if (-not $key) { return @() }
    try { return @($key.GetSubKeyNames()) } finally { $key.Close() }
}

function Test-ErpRegistryKey {
    [CmdletBinding()]
    [OutputType([bool])]
    param([Parameter(Mandatory)] [string] $Path)
    Test-Path -LiteralPath $Path
}

function Initialize-ErpRegistryKey {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)] [string] $Path)
    if (-not (Test-Path -LiteralPath $Path) -and $PSCmdlet.ShouldProcess($Path, 'Create registry key')) {
        New-Item -Path $Path -Force | Out-Null
    }
}

function Set-ErpRegistryValue {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [AllowEmptyString()] [AllowNull()] $Value,
        [ValidateSet('String', 'DWord', 'Binary', 'MultiString')] [string] $Kind = 'String'
    )
    if (-not $PSCmdlet.ShouldProcess("$Path\$Name", 'Set registry value')) { return }
    if (-not (Test-Path -LiteralPath $Path)) { New-Item -Path $Path -Force | Out-Null }
    New-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -PropertyType $Kind -Force | Out-Null
}

function Remove-ErpRegistryValue {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)] [string] $Path, [Parameter(Mandatory)] [string] $Name)
    if ((Test-Path -LiteralPath $Path) -and $PSCmdlet.ShouldProcess("$Path\$Name", 'Remove registry value')) {
        Remove-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction SilentlyContinue
    }
}

function Remove-ErpRegistryKey {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)] [string] $Path)
    if ((Test-Path -LiteralPath $Path) -and $PSCmdlet.ShouldProcess($Path, 'Remove registry key')) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Initialize-ErpSecretsKey {
    <# Creates the Secrets key readable only by SYSTEM and Administrators (by SID, so it works on localized Windows). #>
    [CmdletBinding(SupportsShouldProcess)]
    param()
    $path = "$script:ErpRegistryRoot\Secrets"
    if (-not $PSCmdlet.ShouldProcess($path, 'Create and lock down secrets key')) { return }
    if (-not (Test-Path -LiteralPath $path)) { New-Item -Path $path -Force | Out-Null }
    $acl = New-Object System.Security.AccessControl.RegistrySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {
        $rule = New-Object System.Security.AccessControl.RegistryAccessRule(
            (New-Object System.Security.Principal.SecurityIdentifier $sid),
            [System.Security.AccessControl.RegistryRights]::FullControl,
            [System.Security.AccessControl.InheritanceFlags]::ContainerInherit,
            [System.Security.AccessControl.PropagationFlags]::None,
            [System.Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $path -AclObject $acl
}
