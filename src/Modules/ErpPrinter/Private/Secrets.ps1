# Secrets are DPAPI-encrypted with machine scope so the SYSTEM service and elevated
# administrators can both use them, and the registry key holding them is ACL'd to
# SYSTEM + Administrators. Machine-scope DPAPI alone would not stop other local
# processes, which is why the key ACL matters.

$script:ErpSecretEntropy = [Text.Encoding]::UTF8.GetBytes('ErpPrinter/v1')

function Protect-ErpSecret {
    [CmdletBinding()]
    [OutputType([byte[]])]
    param([Parameter(Mandatory)] [string] $PlainText)
    Add-Type -AssemblyName System.Security
    $bytes = [Text.Encoding]::UTF8.GetBytes($PlainText)
    [Security.Cryptography.ProtectedData]::Protect($bytes, $script:ErpSecretEntropy, [Security.Cryptography.DataProtectionScope]::LocalMachine)
}

function Unprotect-ErpSecret {
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)] [byte[]] $CipherBytes)
    Add-Type -AssemblyName System.Security
    $bytes = [Security.Cryptography.ProtectedData]::Unprotect($CipherBytes, $script:ErpSecretEntropy, [Security.Cryptography.DataProtectionScope]::LocalMachine)
    [Text.Encoding]::UTF8.GetString($bytes)
}

function Get-ErpSecretPath {
    param([Parameter(Mandatory)] [string] $ProfileName)
    "$script:ErpRegistryRoot\Secrets\$ProfileName"
}

function Get-ErpProfileSecrets {
    <# Returns @{ ApiKey = '...'; ApiSecret = '...' } in plain text for use by a backend. #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param([Parameter(Mandatory)] [string] $ProfileName)
    $result = @{ ApiKey = ''; ApiSecret = '' }
    $raw = Get-ErpRegistryValues -Path (Get-ErpSecretPath $ProfileName)
    foreach ($name in @($result.Keys)) {
        if ($raw.ContainsKey($name) -and $raw[$name]) {
            try { $result[$name] = Unprotect-ErpSecret -CipherBytes $raw[$name] }
            catch { Write-ErpLog -Level Warning -Message "Could not decrypt $name for profile '$ProfileName': $($_.Exception.Message)" }
        }
    }
    $result
}

function ConvertFrom-ErpSecureString {
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)] [Security.SecureString] $SecureString)
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}
