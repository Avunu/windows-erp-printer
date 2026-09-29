# Backends are discovered by naming convention. To add one, create Backends\<Name>.ps1
# with Send-Erp<Name>Document and Test-Erp<Name>Connection, and add <Name> to
# $script:ErpBackendNames (plus any settings it needs) in Schema.ps1.

function Get-ErpBackendCommand {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Backend, [Parameter(Mandatory)] [ValidateSet('Send', 'Test')] [string] $Action)
    $name = if ($Action -eq 'Send') { "Send-Erp${Backend}Document" } else { "Test-Erp${Backend}Connection" }
    $command = Get-Command -Name $name -CommandType Function -ErrorAction SilentlyContinue
    if (-not $command) { throw (New-ErpException "Backend '$Backend' is not available in this version." -Permanent) }
    $command
}

function Send-ErpDocument {
    <# Delivers one outbox item with the profile's backend. Returns the remote id. #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $PrinterProfile,
        [Parameter(Mandatory)] [System.Collections.IDictionary] $GlobalSettings,
        [Parameter(Mandatory)] [hashtable] $Item
    )
    $command = Get-ErpBackendCommand -Backend $PrinterProfile.Backend -Action Send
    $fileName = Get-ErpDocumentFileName -Template $PrinterProfile.FileNameTemplate -Meta $Item.Meta
    $secrets = Get-ErpProfileSecrets -ProfileName $PrinterProfile.Name
    & $command -PrinterProfile $PrinterProfile -GlobalSettings $GlobalSettings -Meta $Item.Meta -PdfPath $Item.PdfPath -FileName $fileName -Secrets $secrets
}
