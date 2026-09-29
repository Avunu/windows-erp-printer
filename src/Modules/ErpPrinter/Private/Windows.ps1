# Windows plumbing: elevation checks, the named pipe server, spooler job lookup,
# the PDF driver, local ports and printer queues.

$script:ErpPdfDriver = 'Microsoft Print To PDF'
$script:ErpPortsKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Ports'

function Test-ErpAdministrator {
    [CmdletBinding()]
    [OutputType([bool])]
    param()
    if (-not $script:ErpIsWindows) { return $false }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($identity.User.Value -eq 'S-1-5-18') { return $true }
    (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-ErpAdministrator {
    if (-not (Test-ErpAdministrator)) { throw 'This operation needs an elevated (Administrator) PowerShell session.' }
}

function New-ErpPipeServer {
    <#
    Creates an inbound, asynchronous pipe server instance. The spooler may open the port as
    SYSTEM or while impersonating the printing user, so authenticated users get write access.
    Accounts are given by SID so this works on localized Windows.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $PipeName)
    $security = New-Object System.IO.Pipes.PipeSecurity
    foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {
        $security.AddAccessRule((New-Object System.IO.Pipes.PipeAccessRule(
                    (New-Object Security.Principal.SecurityIdentifier $sid), [System.IO.Pipes.PipeAccessRights]::FullControl, 'Allow')))
    }
    $security.AddAccessRule((New-Object System.IO.Pipes.PipeAccessRule(
                (New-Object Security.Principal.SecurityIdentifier 'S-1-5-11'), [System.IO.Pipes.PipeAccessRights]'ReadWrite, Synchronize', 'Allow')))

    $direction = [System.IO.Pipes.PipeDirection]::In
    $mode = [System.IO.Pipes.PipeTransmissionMode]::Byte
    $options = [System.IO.Pipes.PipeOptions]::Asynchronous
    if ($PSVersionTable.PSEdition -eq 'Core') {
        # .NET (Core) moved the ACL-taking constructor to NamedPipeServerStreamAcl.
        return [System.IO.Pipes.NamedPipeServerStreamAcl]::Create($PipeName, $direction, 1, $mode, $options, 65536, 65536, $security)
    }
    New-Object System.IO.Pipes.NamedPipeServerStream($PipeName, $direction, 1, $mode, $options, 65536, 65536, $security)
}

function Get-ErpActivePrintJob {
    <#
    Returns the job the spooler is currently sending to the port. A queue processes one job
    at a time, so the job in "Printing" state is the one on the pipe. Falls back to the oldest job.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $PrinterName)
    try {
        $jobs = @(Get-PrintJob -PrinterName $PrinterName -ErrorAction Stop | Sort-Object SubmittedTime)
    } catch {
        Write-ErpLog -Level Warning -Message "Could not query print jobs for '$PrinterName': $($_.Exception.Message)"
        return $null
    }
    $job = $jobs | Where-Object { "$($_.JobStatus)" -match 'Printing' } | Select-Object -First 1
    if (-not $job) { $job = $jobs | Select-Object -First 1 }
    $job
}

function Get-ErpPortName {
    param([Parameter(Mandatory)] [string] $PipeName)
    "\\.\pipe\$PipeName"
}

function Install-ErpPdfDriver {
    [CmdletBinding(SupportsShouldProcess)]
    param()
    if (Get-PrinterDriver | Where-Object { $_.Name -eq $script:ErpPdfDriver }) { return }
    if (-not $PSCmdlet.ShouldProcess($script:ErpPdfDriver, 'Install printer driver')) { return }
    try {
        Add-PrinterDriver -Name $script:ErpPdfDriver -ErrorAction Stop
    } catch {
        Write-ErpLog -Level Warning -Message "Add-PrinterDriver failed ($($_.Exception.Message)); enabling the Print to PDF Windows feature."
        Enable-WindowsOptionalFeature -Online -FeatureName 'Printing-PrintToPDFServices-Features' -All -NoRestart | Out-Null
    }
    if (-not (Get-PrinterDriver | Where-Object { $_.Name -eq $script:ErpPdfDriver })) {
        throw "The '$script:ErpPdfDriver' driver is not available on this machine."
    }
}

function Add-ErpPrinterPort {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)] [string] $PortName)
    if (Get-PrinterPort | Where-Object { $_.Name -eq $PortName }) { return }
    if (-not $PSCmdlet.ShouldProcess($PortName, 'Add local printer port')) { return }
    try {
        Add-PrinterPort -Name $PortName -ErrorAction Stop
    } catch {
        # Some builds refuse pipe names through the API; the Local Port monitor reads this key on start.
        Write-ErpLog -Level Warning -Message "Add-PrinterPort refused '$PortName' ($($_.Exception.Message)); registering it in the Ports key and restarting the spooler."
        New-ItemProperty -LiteralPath $script:ErpPortsKey -Name $PortName -Value '' -PropertyType String -Force | Out-Null
        Restart-Service -Name Spooler -Force
        Start-Sleep -Seconds 3
        if (-not (Get-PrinterPort | Where-Object { $_.Name -eq $PortName })) { throw "Could not create printer port '$PortName'." }
    }
}

function Remove-ErpPrinterPort {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)] [string] $PortName)
    if (Get-Printer | Where-Object { $_.PortName -eq $PortName }) { return }
    if (-not $PSCmdlet.ShouldProcess($PortName, 'Remove printer port')) { return }
    try { Remove-PrinterPort -Name $PortName -ErrorAction Stop }
    catch {
        Remove-ItemProperty -LiteralPath $script:ErpPortsKey -Name $PortName -ErrorAction SilentlyContinue
        Write-ErpLog -Level Warning -Message "Removed port '$PortName' from the registry; it disappears after the next spooler restart."
    }
}

function Set-ErpPrinterQueue {
    <# Creates or repairs one printer queue so it uses the PDF driver on the given port. #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)] [string] $PrinterName, [Parameter(Mandatory)] [string] $PortName, [string] $ProfileName)
    Add-ErpPrinterPort -PortName $PortName
    $comment = "Managed by $script:ErpProductName (profile '$ProfileName')"
    $printer = Get-Printer | Where-Object { $_.Name -eq $PrinterName }
    if (-not $printer) {
        if ($PSCmdlet.ShouldProcess($PrinterName, 'Add printer')) {
            Add-Printer -Name $PrinterName -DriverName $script:ErpPdfDriver -PortName $PortName -Comment $comment
            Write-ErpLog -Message "Created printer '$PrinterName' on $PortName."
        }
        return
    }
    if (($printer.PortName -ne $PortName -or $printer.DriverName -ne $script:ErpPdfDriver) -and $PSCmdlet.ShouldProcess($PrinterName, 'Repair printer')) {
        Set-Printer -Name $PrinterName -DriverName $script:ErpPdfDriver -PortName $PortName -Comment $comment
        Write-ErpLog -Message "Updated printer '$PrinterName' to use $PortName."
    }
}
