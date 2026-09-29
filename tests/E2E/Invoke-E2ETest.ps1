<#
.SYNOPSIS
    End-to-end test on a disposable Windows machine (the CI runner): installs the MSI with a
    webhook profile, prints a page, waits for the PDF to arrive at a local webhook, checks
    status and retries, then uninstalls and verifies cleanup.
.NOTES
    Must run elevated in Windows PowerShell 5.1. Leaves diagnostics in -ArtifactDir.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $MsiPath,
    [string] $ArtifactDir = (Join-Path $PSScriptRoot '..\..\out\e2e'),
    [int] $TimeoutSeconds = 120
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null
$ArtifactDir = (Resolve-Path $ArtifactDir).Path
$MsiPath = (Resolve-Path $MsiPath).Path
$profileName = 'E2E Printer'
$printerName = 'E2E Printer'
$installDir = Join-Path $env:ProgramFiles 'ERP Printer'
$dataRoot = Join-Path $env:ProgramData 'ErpPrinter'

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw "ASSERTION FAILED: $Message" }
    Write-Output "  ok  $Message"
}

function Wait-Until([scriptblock] $Condition, [int] $Seconds, [string] $What) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return }
        Start-Sleep -Seconds 2
    }
    throw "Timed out after $Seconds s waiting for: $What"
}

function Invoke-Msi([string[]] $Arguments, [string] $LogName) {
    $log = Join-Path $ArtifactDir $LogName
    $process = Start-Process msiexec.exe -ArgumentList ($Arguments + @('/qn', '/norestart', '/l*v', "`"$log`"")) -Wait -PassThru
    if ($process.ExitCode -notin 0, 3010) { throw "msiexec $($Arguments -join ' ') failed with $($process.ExitCode); see $log" }
}

# --- Webhook receiver ----------------------------------------------------------------
$tcp = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
$tcp.Start(); $port = $tcp.LocalEndpoint.Port; $tcp.Stop()
$received = [hashtable]::Synchronized(@{ Requests = [Collections.ArrayList]::Synchronized((New-Object Collections.ArrayList)); Status = 200 })
$listener = New-Object Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$port/")
$listener.Start()
$receiver = [powershell]::Create()
$null = $receiver.AddScript({
        param($listener, $received)
        while ($listener.IsListening) {
            try { $context = $listener.GetContext() } catch { break }
            $buffer = New-Object IO.MemoryStream
            $context.Request.InputStream.CopyTo($buffer)
            $null = $received.Requests.Add([pscustomobject]@{
                    Event = $context.Request.Headers['X-ErpPrinter-Event']
                    Key   = $context.Request.Headers['Idempotency-Key']
                    Body  = [Text.Encoding]::GetEncoding('ISO-8859-1').GetString($buffer.ToArray())
                })
            $context.Response.StatusCode = $received.Status
            $context.Response.Close()
        }
    }).AddArgument($listener).AddArgument($received)
$receiverHandle = $receiver.BeginInvoke()

try {
    Write-Output '== Install'
    Invoke-Msi @('/i', "`"$MsiPath`"", "PROFILE_NAME=`"$profileName`"", 'BACKEND=Webhook', "SERVER_URL=http://127.0.0.1:$port/hook",
        'API_KEY=e2e-secret', 'AUTO_UPDATE=0', 'LAUNCHCONFIG=0') 'install.log'
    Assert-True (Test-Path (Join-Path $installDir 'ErpPrinter.ps1')) 'files are installed'
    Import-Module (Join-Path $installDir 'Modules\ErpPrinter\ErpPrinter.psd1') -Force

    $printer = Get-Printer -Name $printerName -ErrorAction SilentlyContinue
    Assert-True ($null -ne $printer) "printer '$printerName' exists"
    Assert-True ($printer.PortName -eq '\\.\pipe\ErpPrinter-E2E_Printer') "printer uses the pipe port ($($printer.PortName))"
    Assert-True ($printer.DriverName -eq 'Microsoft Print To PDF') 'printer uses the Print to PDF driver'
    foreach ($task in 'Listener', 'Uploader', 'Updater') {
        Assert-True ($null -ne (Get-ScheduledTask -TaskPath '\ERP Printer\' -TaskName $task -ErrorAction SilentlyContinue)) "task $task is registered"
    }
    Wait-Until { (Get-ScheduledTask -TaskPath '\ERP Printer\' -TaskName 'Listener').State -eq 'Running' } 30 'listener task running'
    Assert-True (Test-ErpPrinterSecret -ProfileName $profileName -Name ApiKey) 'API key is stored encrypted'
    $connection = Test-ErpPrinterConnection -ProfileName $profileName
    Assert-True $connection.Success "connection test succeeds ($($connection.Message))"

    Write-Output '== Print'
    $received.Requests.Clear()
    Add-Type -AssemblyName System.Drawing
    $doc = New-Object Drawing.Printing.PrintDocument
    $doc.DocumentName = 'E2E Invoice 42'
    $doc.PrinterSettings.PrinterName = $printerName
    $doc.PrintController = New-Object Drawing.Printing.StandardPrintController
    $doc.Add_PrintPage({ $_.Graphics.DrawString('ERP Printer end-to-end test', (New-Object Drawing.Font('Arial', 20)), [Drawing.Brushes]::Black, 50, 50) })
    $doc.Print()
    Wait-Until { @($received.Requests | Where-Object Event -eq 'document').Count -ge 1 } $TimeoutSeconds 'the document to reach the webhook'
    $request = @($received.Requests | Where-Object Event -eq 'document')[0]
    Assert-True ($request.Body -match '%PDF-') 'webhook received a PDF'
    Assert-True ($request.Body -match 'E2E Invoice 42') 'metadata carries the document title'
    Assert-True ($request.Body -match [regex]::Escape($env:USERNAME)) 'metadata carries the printing user'
    Assert-True ([bool]$request.Key) 'request has an idempotency key'

    Write-Output '== Retry path'
    $received.Status = 500
    $received.Requests.Clear()
    $doc.DocumentName = 'E2E Retry'
    $doc.Print()
    Wait-Until { @(Get-ErpPrinterStatus).Profiles[0].Pending -ge 1 -and $received.Requests.Count -ge 1 } $TimeoutSeconds 'a failed attempt to be queued for retry'
    $received.Status = 200
    Invoke-ErpPrinterUpload -Force | Out-Null
    Wait-Until { (Get-ErpPrinterStatus).Profiles[0].Pending -eq 0 } 30 'the retried document to be delivered'
    Assert-True ((@($received.Requests | Where-Object { $_.Body -match 'E2E Retry' })).Count -ge 2) 'document was retried after a server error'

    Write-Output '== Status'
    & (Join-Path $installDir 'ErpPrinter.ps1') -Command Status | Tee-Object -FilePath (Join-Path $ArtifactDir 'status.txt')

    Write-Output '== Uninstall'
    Invoke-Msi @('/x', "`"$MsiPath`"") 'uninstall.log'
    Assert-True ($null -eq (Get-Printer -Name $printerName -ErrorAction SilentlyContinue)) 'printer removed'
    Assert-True ($null -eq (Get-ScheduledTask -TaskPath '\ERP Printer\' -ErrorAction SilentlyContinue)) 'tasks removed'
    Assert-True (-not (Test-Path (Join-Path $installDir 'ErpPrinter.ps1'))) 'files removed'
    Assert-True (Test-Path 'HKLM:\SOFTWARE\ErpPrinter\Printers') 'configuration kept without PURGE=1'
    Write-Output 'E2E PASSED'
} finally {
    if (Test-Path (Join-Path $dataRoot 'logs')) { Copy-Item (Join-Path $dataRoot 'logs\*') $ArtifactDir -ErrorAction SilentlyContinue }
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ErpPrinter' } -MaxEvents 200 -ErrorAction SilentlyContinue |
        Format-List TimeCreated, LevelDisplayName, Message | Out-File (Join-Path $ArtifactDir 'eventlog.txt')
    $listener.Stop()
    $listener.Close()
    try { $null = $receiver.EndInvoke($receiverHandle) } catch { Write-Verbose "receiver: $_" }
    $receiver.Dispose()
}
