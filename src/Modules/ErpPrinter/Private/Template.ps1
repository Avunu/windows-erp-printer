function Get-ErpMetaValue {
    <# Reads a sidecar property, tolerating sidecars from older versions or written by hand. #>
    param($Meta, [string] $Name)
    if ($null -ne $Meta -and $Meta.PSObject.Properties[$Name]) { return $Meta.$Name }
    $null
}

function Get-ErpTemplateTokens {
    [CmdletBinding()]
    [OutputType([hashtable])]
    param([Parameter(Mandatory)] $Meta)
    $user = [string](Get-ErpMetaValue $Meta 'user')
    $domain = ''
    $username = $user
    if ($user -match '^(?<d>[^\\]+)\\(?<u>.+)$') { $domain = $Matches.d; $username = $Matches.u }
    elseif ($user -match '^(?<u>[^@]+)@(?<d>.+)$') { $domain = $Matches.d; $username = $Matches.u }
    $printed = Get-Date
    $printedValue = Get-ErpMetaValue $Meta 'printed'
    if ($printedValue) { $printed = ConvertTo-ErpDateTime $printedValue }
    @{
        title    = (Get-ErpCleanTitle ([string](Get-ErpMetaValue $Meta 'title')))
        user     = $user
        username = $username
        domain   = $domain
        computer = [string](Get-ErpMetaValue $Meta 'computer')
        date     = $printed.ToString('yyyy-MM-dd')
        time     = $printed.ToString('HHmmss')
        datetime = $printed.ToString('yyyy-MM-dd HHmmss')
        id       = [string](Get-ErpMetaValue $Meta 'id')
        jobid    = [string](Get-ErpMetaValue $Meta 'jobId')
        profile  = [string](Get-ErpMetaValue $Meta 'profile')
    }
}

function Get-ErpCleanTitle {
    <# Strips application prefixes and source-file extensions from spooler document names. #>
    [CmdletBinding()]
    [OutputType([string])]
    param([AllowEmptyString()] [string] $Title)
    $t = "$Title".Trim()
    $t = $t -replace '^Microsoft (Word|Excel|PowerPoint|Outlook) - ', ''
    $t = $t -replace '\.(docx?|docm|xlsx?|xlsm|pptx?|txt|rtf|odt|ods|odp|pdf|html?|xps|oxps|png|jpe?g|tiff?|msg|eml)$', ''
    if (-not $t) { $t = 'Untitled' }
    $t
}

function Expand-ErpTemplate {
    <#
    .SYNOPSIS
        Replaces {token} placeholders. Unknown tokens are left as they are.
    .PARAMETER JsonEscape
        Escape values for embedding inside a JSON string literal.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Used inside the MatchEvaluator closure.')]
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Template,
        [Parameter(Mandatory)] [hashtable] $Tokens,
        [switch] $JsonEscape
    )
    [regex]::Replace($Template, '\{(\w+)\}', {
            param($m)
            $key = $m.Groups[1].Value.ToLowerInvariant()
            if (-not $Tokens.ContainsKey($key)) { return $m.Value }
            $v = [string]$Tokens[$key]
            if ($JsonEscape) {
                $quoted = ConvertTo-Json $v -Compress
                $v = $quoted.Substring(1, $quoted.Length - 2)
            }
            $v
        }.GetNewClosure())
}

function Get-ErpDocumentFileName {
    <# Builds a safe "<name>.pdf" from the profile's FileNameTemplate. #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)] [string] $Template, [Parameter(Mandatory)] $Meta)
    $name = Expand-ErpTemplate -Template $Template -Tokens (Get-ErpTemplateTokens $Meta)
    $name = ($name -replace '[\\/:*?"<>|\x00-\x1f]', '_').Trim().TrimEnd('.')
    $name = $name -replace '\.pdf$', ''
    if ($name.Length -gt 150) { $name = $name.Substring(0, 150).Trim() }
    if (-not $name) { $name = 'Untitled' }
    "$name.pdf"
}
