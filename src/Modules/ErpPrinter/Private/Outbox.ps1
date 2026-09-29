# Outbox layout, per profile:
#   <DataRoot>\spool\<profile>\outbox\<id>.pdf + <id>.json   waiting for delivery
#   <DataRoot>\spool\<profile>\failed\...                   gave up (permanent error or MaxAttempts)
#   <DataRoot>\spool\<profile>\sent\...                     delivered (only if KeepSentDays > 0)
#
# The listener writes the PDF first and the JSON sidecar last, each via a .tmp rename,
# so the presence of <id>.json means <id>.pdf is complete.

function ConvertTo-ErpDateTime {
    <# PowerShell 7's ConvertFrom-Json already yields DateTime; 5.1 yields ISO strings. #>
    [CmdletBinding()]
    [OutputType([DateTime])]
    param([Parameter(Mandatory)] $Value)
    if ($Value -is [DateTime]) { return $Value }
    if ($Value -is [DateTimeOffset]) { return $Value.LocalDateTime }
    [DateTime]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
}

function Get-ErpProfilePaths {
    [CmdletBinding()]
    [OutputType([hashtable])]
    param([Parameter(Mandatory)] [string] $DataRoot, [Parameter(Mandatory)] [string] $ProfileName)
    $root = Join-Path (Join-Path $DataRoot 'spool') (ConvertTo-ErpSafeName $ProfileName)
    @{
        Root   = $root
        Outbox = Join-Path $root 'outbox'
        Failed = Join-Path $root 'failed'
        Sent   = Join-Path $root 'sent'
    }
}

function Initialize-ErpProfilePaths {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [hashtable] $Paths)
    foreach ($key in 'Outbox', 'Failed', 'Sent') {
        if (-not (Test-Path -LiteralPath $Paths[$key])) { New-Item -ItemType Directory -Path $Paths[$key] -Force | Out-Null }
    }
}

function Write-ErpFileAtomic {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Path, [Parameter(Mandatory)] [string] $Content)
    $tmp = "$Path.tmp"
    [IO.File]::WriteAllText($tmp, $Content, (New-Object Text.UTF8Encoding $false))
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
    Move-Item -LiteralPath $tmp -Destination $Path
}

function New-ErpOutboxMeta {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)] [string] $Id,
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $ProfileName,
        [string] $User = '',
        [string] $Title = '',
        [string] $JobId = '',
        [int] $Pages = 0,
        [long] $SizeBytes = 0,
        [DateTime] $Printed = (Get-Date)
    )
    [pscustomobject][ordered]@{
        id          = $Id
        profile     = $ProfileName
        user        = $User
        title       = $Title
        computer    = $env:COMPUTERNAME
        jobId       = $JobId
        pages       = $Pages
        sizeBytes   = $SizeBytes
        printed     = $Printed.ToString('o')
        attempts    = 0
        lastAttempt = $null
        nextAttempt = $null
        lastError   = $null
        remoteId    = $null
        sentAt      = $null
    }
}

function Complete-ErpOutboxMeta {
    <# Adds any missing sidecar fields so older or hand-written sidecars behave like new ones. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Meta, [Parameter(Mandatory)] [string] $Id)
    $template = New-ErpOutboxMeta -Id $Id -ProfileName ''
    foreach ($property in $template.PSObject.Properties) {
        if (-not $Meta.PSObject.Properties[$property.Name]) {
            $value = if ($property.Name -in 'computer', 'printed') { $null } else { $property.Value }
            $Meta | Add-Member -NotePropertyName $property.Name -NotePropertyValue $value
        }
    }
    if (-not $Meta.id) { $Meta.id = $Id }
    $Meta
}

function Save-ErpOutboxMeta {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Folder, [Parameter(Mandatory)] $Meta)
    Write-ErpFileAtomic -Path (Join-Path $Folder "$($Meta.id).json") -Content ($Meta | ConvertTo-Json -Depth 5)
}

function Get-ErpOutboxItems {
    <# Returns @{ Meta; PdfPath; JsonPath } for items in a folder, oldest first. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Folder, [switch] $DueOnly, [DateTime] $Now = (Get-Date))
    if (-not (Test-Path -LiteralPath $Folder)) { return }
    foreach ($json in Get-ChildItem -LiteralPath $Folder -Filter '*.json' | Sort-Object LastWriteTime) {
        try { $meta = Get-Content -LiteralPath $json.FullName -Raw -Encoding UTF8 | ConvertFrom-Json }
        catch {
            Write-ErpLog -Level Warning -Message "Unreadable sidecar $($json.FullName): $($_.Exception.Message)"
            continue
        }
        $meta = Complete-ErpOutboxMeta -Meta $meta -Id $json.BaseName
        if ($DueOnly -and $meta.nextAttempt -and ((ConvertTo-ErpDateTime $meta.nextAttempt) -gt $Now)) { continue }
        @{
            Meta     = $meta
            JsonPath = $json.FullName
            PdfPath  = [IO.Path]::ChangeExtension($json.FullName, '.pdf')
        }
    }
}

function Move-ErpOutboxItem {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)] [hashtable] $Item, [Parameter(Mandatory)] [string] $Destination)
    if (-not $PSCmdlet.ShouldProcess($Item.JsonPath, "Move to $Destination")) { return }
    if (-not (Test-Path -LiteralPath $Destination)) { New-Item -ItemType Directory -Path $Destination -Force | Out-Null }
    if (Test-Path -LiteralPath $Item.PdfPath) { Move-Item -LiteralPath $Item.PdfPath -Destination $Destination -Force }
    Save-ErpOutboxMeta -Folder $Destination -Meta $Item.Meta
    Remove-Item -LiteralPath $Item.JsonPath -Force -ErrorAction SilentlyContinue
}

function Remove-ErpOutboxItem {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)] [hashtable] $Item)
    if ($PSCmdlet.ShouldProcess($Item.JsonPath, 'Delete delivered document')) {
        Remove-Item -LiteralPath $Item.PdfPath, $Item.JsonPath -Force -ErrorAction SilentlyContinue
    }
}

function Get-ErpRetryDelaySeconds {
    <# Exponential backoff: 30s, 60s, 120s ... capped at MaxMinutes. #>
    [CmdletBinding()]
    [OutputType([int])]
    param([Parameter(Mandatory)] [int] $Attempt, [Parameter(Mandatory)] [int] $MaxMinutes)
    $exp = [Math]::Min([Math]::Max($Attempt - 1, 0), 20)
    [int][Math]::Min(30 * [Math]::Pow(2, $exp), $MaxMinutes * 60)
}

function Repair-ErpOrphanedPdfs {
    <#
    Adopts PDFs that have no sidecar (the listener stopped between writing the PDF and
    the JSON). Only touches files older than a few minutes so in-flight writes are safe.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)] [string] $Folder, [Parameter(Mandatory)] [string] $ProfileName)
    $cutoff = (Get-Date).AddMinutes(-5)
    foreach ($pdf in Get-ChildItem -LiteralPath $Folder -Filter '*.pdf' -ErrorAction SilentlyContinue) {
        if ($pdf.LastWriteTime -gt $cutoff) { continue }
        if (Test-Path -LiteralPath ([IO.Path]::ChangeExtension($pdf.FullName, '.json'))) { continue }
        if (-not $PSCmdlet.ShouldProcess($pdf.FullName, 'Adopt orphaned PDF')) { continue }
        $meta = New-ErpOutboxMeta -Id $pdf.BaseName -ProfileName $ProfileName -Title 'Recovered print job' -SizeBytes $pdf.Length -Printed $pdf.LastWriteTime
        Save-ErpOutboxMeta -Folder $Folder -Meta $meta
        Write-ErpLog -Level Warning -Message "Recovered orphaned print job $($pdf.Name) in profile '$ProfileName' (user and title unknown)."
    }
    # Stale temp files from an interrupted write.
    Get-ChildItem -LiteralPath $Folder -Filter '*.tmp' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -lt $cutoff.AddMinutes(-55) } |
        Remove-Item -Force -ErrorAction SilentlyContinue
}
