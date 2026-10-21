# Explains what the spooler did with a queued print job. A job that is stuck in the queue
# tells you nothing by itself; the PrintService Operational log and the spool shadow file say
# which stage it reached (spooled, printing, print processor, rendering, written to the port)
# and in what format the application sent it.

function Get-ErpSpoolerStageVerdict {
    <#
    Says how far a print job got inside the spooler, from the PrintService Operational event
    ids it produced: 800 spooled, 801 printing started, 842 print processor finished (with a
    Win32 code), 805 rendering, 307 written to the port. Pure, so the decision table is testable.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([int[]] $EventIds = @(), [string] $ProcessorCode = '')
    $make = {
        param($Stage, $Failed, $Summary, $Hint)
        [pscustomobject]@{ Stage = $Stage; Failed = $Failed; Summary = $Summary; Hint = $Hint }
    }
    $badCode = $ProcessorCode -and $ProcessorCode -notmatch '^0x0+$'
    if ($EventIds -contains 307) {
        return & $make 'Printed' $false 'The spooler finished this job and wrote it to the port.' 'If it did not arrive, look for "The spooler connected to pipe" in the listener log.'
    }
    if ($badCode) {
        return & $make 'ProcessorFailed' $true "The print processor failed with code $ProcessorCode before anything was written to the port." 'The document could not be converted for the driver. Print the same document to the built-in "Microsoft Print to PDF" printer; if that fails too, the cause is the document or Windows, not ERP Printer.'
    }
    if ($EventIds -contains 805) {
        return & $make 'Rendered' $true 'The spooler rendered the job but never completed writing it to the port.' 'This points at the pipe: check that the Listener is running and hosting it, the spooler Admin log, and the listener log for a connection.'
    }
    if ($EventIds -contains 801) {
        $summary = "The spooler started printing but its print processor never finished (there is no 'sent through the print processor' event) and nothing reached the port. The document failed while the driver was converting it to PDF, before the ERP Printer pipe was involved."
        $hint = "Print the same document to the built-in 'Microsoft Print to PDF' printer. If that fails too, the cause is the document or Windows. If only this queue fails, try the application's system print dialog instead of its own print preview (in Edge, Ctrl+Shift+P). Then delete the failed job: it blocks every job behind it."
        return & $make 'Printing' $true $summary $hint
    }
    if ($EventIds -contains 800) {
        return & $make 'Spooled' $true 'The spooler received the job but never started printing it.' 'The queue is blocked by an earlier job, paused, offline, or outside its availability hours.'
    }
    & $make 'Unknown' $false 'The spooler log has no events for this job.' 'The PrintService Operational log may be disabled or may have rolled over.'
}

function Get-ErpSpoolFileFormat {
    <# The content type in a job's spool shadow file (for example TYPE_XPS_OPEN or TYPE_EMF), or $null. Needs administrator rights. #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)] [int] $JobId)
    try {
        $path = Join-Path $env:SystemRoot ('System32\spool\PRINTERS\{0:D5}.SHD' -f $JobId)
        $stream = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
        try {
            $bytes = New-Object byte[] ([int][Math]::Min($stream.Length, 16384))
            $null = $stream.Read($bytes, 0, $bytes.Length)
        } finally { $stream.Dispose() }
        $found = [regex]::Match([Text.Encoding]::Unicode.GetString($bytes), 'TYPE_[A-Z0-9_]+')
        if ($found.Success) { $found.Value }
    } catch {
        Write-Verbose "Could not read the spool file header: $_"
    }
}

function Get-ErpJobAnalysis {
    <# Describes what the spooler did with one queued job: format, timeline and the stage where it stopped. #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([Parameter(Mandatory)] $Job)
    $id = [int]$Job.Id
    $since = if ($Job.SubmittedTime) { ([DateTime]$Job.SubmittedTime).AddMinutes(-1) } else { (Get-Date).AddDays(-1) }
    # Every one of these messages starts with, or contains first, the job number.
    $events = @(Get-ErpPrintServiceEvents -Log Operational -Since $since -Id 800, 801, 805, 842, 307 -Max 300 |
            Where-Object { "$($_.Message)" -match '\d+' -and [int]$Matches[0] -eq $id } | Sort-Object TimeCreated)
    $code = ''
    foreach ($ev in $events | Where-Object { $_.Id -eq 842 }) {
        $codes = [regex]::Matches([string]$ev.Message, '0x[0-9A-Fa-f]+')
        if ($codes.Count) { $code = $codes[$codes.Count - 1].Value }
    }
    $verdict = Get-ErpSpoolerStageVerdict -EventIds @($events | ForEach-Object { $_.Id }) -ProcessorCode $code

    $details = New-Object System.Collections.Generic.List[string]
    $details.Add("job $id '$($Job.DocumentName)' ($([Math]::Round([double]$Job.Size / 1KB)) KB, $($Job.TotalPages) page(s), status $($Job.JobStatus))")
    $cim = Get-CimInstance -ClassName Win32_PrintJob -ErrorAction SilentlyContinue | Where-Object { $_.JobId -eq $id } | Select-Object -First 1
    if ($cim) { $details.Add("datatype $($cim.Datatype)") }
    $format = Get-ErpSpoolFileFormat -JobId $id
    if ($format) { $details.Add("spool file $format") }

    $names = @{ 800 = 'spooled'; 801 = 'printing started'; 805 = 'rendering'; 842 = "print processor finished ($code)"; 307 = 'written to port' }
    $timeline = if ($events.Count) { ($events | ForEach-Object { '{0:HH:mm:ss} {1}' -f $_.TimeCreated, $names[[int]$_.Id] }) -join ' -> ' } else { 'no spooler events' }
    [pscustomobject]@{
        Stage  = $verdict.Stage
        Failed = $verdict.Failed
        Detail = ($details -join '; ') + [Environment]::NewLine + "Spooler timeline: $timeline" + [Environment]::NewLine + $verdict.Summary
        Hint   = $verdict.Hint
    }
}
