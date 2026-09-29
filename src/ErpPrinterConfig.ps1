#Requires -Version 5.1
<#
.SYNOPSIS
    ERP Printer configuration GUI (Windows Forms, Windows PowerShell 5.1).
.DESCRIPTION
    Edits printer profiles and global settings in HKLM\SOFTWARE\ErpPrinter, stores API
    keys encrypted, tests connections, (re)creates the Windows printers and shows the
    outbox status. The form is generated from the module's settings schema, so new
    settings appear here automatically. Settings pushed by Group Policy are read-only.
.PARAMETER HideConsole
    Hide the PowerShell console window (used by the Start menu shortcut).
#>
[CmdletBinding()]
param([switch] $HideConsole)

$powershellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process -FilePath $powershellExe -Verb RunAs -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', "`"$PSCommandPath`"", '-HideConsole')
    return
}

Add-Type -AssemblyName System.Windows.Forms, System.Drawing, Microsoft.VisualBasic
Add-Type -Namespace ErpPrinterGui -Name Native -MemberDefinition @'
[DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
'@
if ($HideConsole) { $null = [ErpPrinterGui.Native]::ShowWindow([ErpPrinterGui.Native]::GetConsoleWindow(), 0) }

$ErrorActionPreference = 'Stop'
$module = Import-Module (Join-Path $PSScriptRoot 'Modules\ErpPrinter\ErpPrinter.psd1') -Force -PassThru
[Windows.Forms.Application]::EnableVisualStyles()

$script:ProfileSchema = @(Get-ErpPrinterSettingSchema -Scope Profile)
$script:GlobalSchema = @(Get-ErpPrinterSettingSchema -Scope Global)
$script:Rendering = $false
$script:Dirty = $false
$script:ToolTip = New-Object Windows.Forms.ToolTip
$script:ToolTip.AutoPopDelay = 20000

#region State --------------------------------------------------------------------------

function Get-DefaultProfileValues([string] $Name) {
    # Resolve defaults inside the module so scriptblock defaults (e.g. pipe name) work.
    & $module { param($n) Resolve-ErpSettings -Schema $script:ErpProfileSchema -Context @{ Name = $n } } $Name
}

function Import-State {
    $config = Get-ErpPrinterConfig
    $script:State = @{
        Profiles = [ordered]@{}
        Deleted  = New-Object System.Collections.Generic.List[string]
        Global   = @{ Values = $config.Global; Managed = @($config.Global.ManagedSettings) }
        Current  = $null
    }
    foreach ($p in $config.Profiles) {
        $script:State.Profiles[$p.Name] = @{
            Values        = $p
            Managed       = @($p.ManagedSettings)
            PolicyDefined = [bool]$p.PolicyDefined
            IsNew         = $false
            Secrets       = @{ ApiKey = ''; ApiSecret = '' }
        }
    }
    $script:Dirty = $false
}

function Get-ProfileForValidation([string] $Name) {
    $entry = $script:State.Profiles[$Name]
    $values = [ordered]@{}
    foreach ($key in $entry.Values.Keys) { $values[$key] = $entry.Values[$key] }
    $values['Name'] = $Name
    $values
}

#endregion
#region Form building helpers -----------------------------------------------------------

function New-Button([string] $Text, [scriptblock] $OnClick) {
    $b = New-Object Windows.Forms.Button
    $b.Text = $Text
    $b.AutoSize = $true
    $b.Padding = New-Object Windows.Forms.Padding(6, 2, 6, 2)
    # The action rides on Tag: GetNewClosure() would hide this script's functions from it.
    $b.Tag = $OnClick
    $b.Add_Click({ Invoke-Safely $this.Tag })
    $b
}

function Invoke-Safely([scriptblock] $Action) {
    $form.Cursor = [Windows.Forms.Cursors]::WaitCursor
    try { & $Action }
    catch { [Windows.Forms.MessageBox]::Show($form, $_.Exception.Message, 'ERP Printer', 'OK', 'Error') | Out-Null }
    finally { $form.Cursor = [Windows.Forms.Cursors]::Default }
}

function New-SettingsTable {
    $t = New-Object Windows.Forms.TableLayoutPanel
    $t.Dock = 'Top'
    $t.AutoSize = $true
    $t.AutoSizeMode = 'GrowAndShrink'
    $t.ColumnCount = 2
    $t.Padding = New-Object Windows.Forms.Padding(8)
    $null = $t.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle([Windows.Forms.SizeType]::Absolute, 210)))
    $null = $t.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle([Windows.Forms.SizeType]::Percent, 100)))
    $t
}

function Add-TableRow($Table, $Left, $Right) {
    $row = $Table.RowCount
    $Table.RowCount = $row + 1
    $null = $Table.RowStyles.Add((New-Object Windows.Forms.RowStyle([Windows.Forms.SizeType]::AutoSize)))
    $Table.Controls.Add($Left, 0, $row)
    if ($Right) { $Table.Controls.Add($Right, 1, $row) } else { $Table.SetColumnSpan($Left, 2) }
}

function Set-CueBanner($TextBox, [string] $Text) {
    $null = [ErpPrinterGui.Native]::SendMessage($TextBox.Handle, 0x1501, [IntPtr]1, $Text)
}

function Add-SettingRows {
    <#
    Renders one label + editor per setting into $Table and returns name -> control.
    $SecretState: hashtable of secret name -> $true if a value is already stored.
    #>
    param($Table, [object[]] $Schema, [System.Collections.IDictionary] $Values, [string[]] $Managed, [hashtable] $SecretState = @{})
    $script:Rendering = $true
    $Table.SuspendLayout()
    $Table.Controls.Clear()
    $Table.RowStyles.Clear()
    $Table.RowCount = 0
    $controls = @{}
    $group = $null
    foreach ($s in $Schema) {
        if ($s.Group -ne $group) {
            $group = $s.Group
            $header = New-Object Windows.Forms.Label
            $header.Text = $group
            $header.AutoSize = $true
            $header.Font = New-Object Drawing.Font($form.Font, [Drawing.FontStyle]::Bold)
            $header.Margin = New-Object Windows.Forms.Padding(0, 12, 0, 4)
            Add-TableRow $Table $header $null
        }
        $isManaged = $Managed -contains $s.Name
        $label = New-Object Windows.Forms.Label
        $label.Text = $s.Label + $(if ($isManaged) { ' (policy)' } else { '' })
        $label.AutoSize = $true
        $label.Anchor = 'Left'
        $label.Margin = New-Object Windows.Forms.Padding(3, 7, 3, 3)

        switch ($s.Type) {
            'Bool' {
                $c = New-Object Windows.Forms.CheckBox
                $c.AutoSize = $true
                $c.Checked = [bool]$Values[$s.Name]
                $c.Add_CheckedChanged({ if (-not $script:Rendering) { $script:Dirty = $true } })
            }
            'Int' {
                $c = New-Object Windows.Forms.NumericUpDown
                $c.Minimum = $s.Min
                $c.Maximum = [Math]::Min([decimal]$s.Max, [decimal]2147483647)
                $c.Width = 120
                $c.Value = [decimal]$Values[$s.Name]
                $c.Add_ValueChanged({ if (-not $script:Rendering) { $script:Dirty = $true } })
            }
            'Choice' {
                $c = New-Object Windows.Forms.ComboBox
                $c.DropDownStyle = 'DropDownList'
                $c.Width = 240
                foreach ($choice in $s.Choices) { $null = $c.Items.Add($choice) }
                $c.SelectedItem = [string]$Values[$s.Name]
                $c.Add_SelectedIndexChanged({ if (-not $script:Rendering) { $script:Dirty = $true } })
            }
            'Secret' {
                $c = New-Object Windows.Forms.TextBox
                $c.UseSystemPasswordChar = $true
                $c.Anchor = 'Left, Right'
                $c.Text = [string]$Values[$s.Name]
                $c.AccessibleDescription = if ($SecretState[$s.Name]) { 'Stored - leave empty to keep' } else { 'Not set' }
                $c.Add_HandleCreated({ Set-CueBanner $this $this.AccessibleDescription })
                $c.Add_TextChanged({ if (-not $script:Rendering) { $script:Dirty = $true } })
            }
            default {
                $c = New-Object Windows.Forms.TextBox
                $c.Anchor = 'Left, Right'
                $c.Text = [string]$Values[$s.Name]
                $c.Add_TextChanged({ if (-not $script:Rendering) { $script:Dirty = $true } })
            }
        }
        $c.Enabled = -not $isManaged
        $c.Tag = $s
        if ($s.Help) {
            $script:ToolTip.SetToolTip($label, $s.Help)
            $script:ToolTip.SetToolTip($c, $s.Help)
        }
        Add-TableRow $Table $label $c
        $controls[$s.Name] = $c
    }
    $Table.ResumeLayout()
    $script:Rendering = $false
    $controls
}

function Read-SettingControls([hashtable] $Controls, [System.Collections.IDictionary] $Values, [hashtable] $Secrets) {
    foreach ($name in $Controls.Keys) {
        $c = $Controls[$name]
        switch ($c.Tag.Type) {
            'Bool' { $Values[$name] = $c.Checked }
            'Int' { $Values[$name] = [int]$c.Value }
            'Choice' { if ($c.SelectedItem) { $Values[$name] = [string]$c.SelectedItem } }
            'Secret' { if ($Secrets) { $Secrets[$name] = $c.Text } }
            default { $Values[$name] = $c.Text.Trim() }
        }
    }
}

#endregion
#region Printers tab --------------------------------------------------------------------

function Save-CurrentProfileEdits {
    $name = $script:State.Current
    if (-not $name -or -not $script:State.Profiles.Contains($name) -or -not $script:ProfileControls) { return }
    $entry = $script:State.Profiles[$name]
    Read-SettingControls $script:ProfileControls $entry.Values $entry.Secrets
}

function Show-Profile([string] $Name) {
    $script:State.Current = $Name
    $profileHeader.Text = ''
    if (-not $Name) {
        $profileTable.Controls.Clear()
        $script:ProfileControls = @{}
        $profileButtons.Enabled = $false
        return
    }
    $entry = $script:State.Profiles[$Name]
    $backend = $entry.Values.Backend
    $schema = $script:ProfileSchema | Where-Object { $_.Backends.Count -eq 0 -or $_.Backends -contains $backend }
    $secretState = @{
        ApiKey    = Test-ErpPrinterSecret -ProfileName $Name -Name ApiKey
        ApiSecret = Test-ErpPrinterSecret -ProfileName $Name -Name ApiSecret
    }
    $values = [ordered]@{}
    foreach ($key in $entry.Values.Keys) { $values[$key] = $entry.Values[$key] }
    foreach ($key in $entry.Secrets.Keys) { $values[$key] = $entry.Secrets[$key] }
    $script:ProfileControls = Add-SettingRows $profileTable $schema $values $entry.Managed $secretState
    $script:ProfileControls['Backend'].Add_SelectedIndexChanged({
            if ($script:Rendering) { return }
            Save-CurrentProfileEdits
            Show-Profile $script:State.Current
        })
    $profileHeader.Text = if ($entry.PolicyDefined) { "'$Name' is defined by Group Policy." } elseif ($entry.IsNew) { "'$Name' is new and not saved yet." } else { '' }
    $removeButton.Enabled = -not $entry.PolicyDefined
    $profileButtons.Enabled = $true
}

function Update-ProfileList([string] $Select) {
    $script:Rendering = $true
    $profileList.Items.Clear()
    foreach ($name in $script:State.Profiles.Keys) { $null = $profileList.Items.Add($name) }
    $script:Rendering = $false
    if ($Select -and $profileList.Items.Contains($Select)) { $profileList.SelectedItem = $Select }
    elseif ($profileList.Items.Count) { $profileList.SelectedIndex = 0 }
    else { Show-Profile $null }
}

function Add-Profile {
    $suggested = if ($script:State.Profiles.Count) { 'Send to ERP ' + ($script:State.Profiles.Count + 1) } else { 'Send to ERP' }
    $name = [Microsoft.VisualBasic.Interaction]::InputBox('Name of the new printer (shown to users in the print dialog):', 'Add printer', $suggested).Trim()
    if (-not $name) { return }
    if ($script:State.Profiles.Contains($name)) { throw "A printer named '$name' already exists." }
    if ($name -notmatch '^[\w][\w .()&+-]{0,63}$') { throw 'Use up to 64 letters, digits, spaces and . ( ) & + - _' }
    Save-CurrentProfileEdits
    $values = Get-DefaultProfileValues $name
    $script:State.Profiles[$name] = @{ Values = $values; Managed = @(); PolicyDefined = $false; IsNew = $true; Secrets = @{ ApiKey = ''; ApiSecret = '' } }
    $null = $script:State.Deleted.Remove($name)
    $script:Dirty = $true
    Update-ProfileList $name
}

function Remove-Profile {
    $name = $script:State.Current
    if (-not $name) { return }
    $answer = [Windows.Forms.MessageBox]::Show($form, "Remove the printer '$name'? Its stored API key is deleted as well. Documents still in its outbox stay on disk.", 'Remove printer', 'YesNo', 'Warning')
    if ($answer -ne 'Yes') { return }
    if (-not $script:State.Profiles[$name].IsNew) { $script:State.Deleted.Add($name) }
    $script:State.Profiles.Remove($name)
    $script:State.Current = $null
    $script:Dirty = $true
    Update-ProfileList
}

function Test-CurrentProfile {
    Save-CurrentProfileEdits
    $name = $script:State.Current
    if (-not $name) { return }
    $result = Test-ErpPrinterConnection -PrinterProfile (Get-ProfileForValidation $name) -Secrets $script:State.Profiles[$name].Secrets
    $icon = if ($result.Success) { 'Information' } else { 'Error' }
    [Windows.Forms.MessageBox]::Show($form, $result.Message, "Test connection - $name", 'OK', $icon) | Out-Null
}

function Send-TestPage {
    Save-CurrentProfileEdits
    $name = $script:State.Current
    if (-not $name) { return }
    if ($script:Dirty) {
        $answer = [Windows.Forms.MessageBox]::Show($form, 'Save and apply your changes first?', 'Print test page', 'YesNo', 'Question')
        if ($answer -ne 'Yes') { return }
        Save-All
        if (-not $script:LastSaveOk) { return }
    }
    $printerName = $script:State.Profiles[$name].Values.PrinterName
    $script:TestPageText = @(
        'ERP Printer test page', '',
        "Printer:  $printerName", "Profile:  $name", "Computer: $env:COMPUTERNAME",
        "User:     $env:USERDOMAIN\$env:USERNAME", "Time:     $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')", '',
        'If this page arrives in your ERP, printing works end to end.'
    ) -join [Environment]::NewLine
    $doc = New-Object Drawing.Printing.PrintDocument
    $doc.DocumentName = "ERP Printer test page - $name"
    $doc.PrinterSettings.PrinterName = $printerName
    if (-not $doc.PrinterSettings.IsValid) { throw "Printer '$printerName' does not exist. Save and apply first." }
    $doc.PrintController = New-Object Drawing.Printing.StandardPrintController
    $doc.Add_PrintPage({
            # In WinForms/Drawing event handlers $_ is the EventArgs.
            $font = New-Object Drawing.Font('Segoe UI', 14)
            $_.Graphics.DrawString($script:TestPageText, $font, [Drawing.Brushes]::Black, 72, 72)
            $font.Dispose()
            $_.HasMorePages = $false
        })
    $doc.Print()
    $doc.Dispose()
    [Windows.Forms.MessageBox]::Show($form, "Test page sent to '$printerName'. Check the Status tab or your ERP in a few seconds.", 'Print test page', 'OK', 'Information') | Out-Null
}

#endregion
#region Save / status -------------------------------------------------------------------

function Save-All {
    # Result goes to $script:LastSaveOk; function output is unreliable with WinForms calls.
    $script:LastSaveOk = $false
    Save-CurrentProfileEdits
    Read-SettingControls $script:GlobalControls $script:State.Global.Values $null

    $all = @(foreach ($name in $script:State.Profiles.Keys) { Get-ProfileForValidation $name })
    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($name in $script:State.Profiles.Keys) {
        $candidate = Get-ProfileForValidation $name
        if (-not $candidate.Enabled) { continue }
        foreach ($p in Test-ErpPrinterProfile -PrinterProfile $candidate -Secrets $script:State.Profiles[$name].Secrets -AllProfiles $all) {
            $problems.Add("${name}: $p")
        }
    }
    if ($problems.Count) {
        [Windows.Forms.MessageBox]::Show($form, "Please fix these first:`n`n" + ($problems -join "`n"), 'Cannot save', 'OK', 'Warning') | Out-Null
        return
    }

    foreach ($name in $script:State.Deleted) { Remove-ErpPrinterProfile -Name $name -Confirm:$false }
    $script:State.Deleted.Clear()
    foreach ($name in $script:State.Profiles.Keys) {
        $entry = $script:State.Profiles[$name]
        if ($entry.IsNew) { New-ErpPrinterProfile -Name $name -Settings @{ Backend = $entry.Values.Backend } -Force }
        $defaults = Get-DefaultProfileValues $name
        foreach ($s in $script:ProfileSchema) {
            if ($entry.Managed -contains $s.Name) { continue }
            if ($s.Type -eq 'Secret') {
                if ($entry.Secrets[$s.Name]) { Set-ErpPrinterSecret -ProfileName $name -Name $s.Name -Value $entry.Secrets[$s.Name] }
            } elseif ($entry.PolicyDefined -and $entry.Values[$s.Name] -eq $defaults[$s.Name]) {
                # Writing defaults would create a local key and turn a policy-defined profile into a local one.
                continue
            } else {
                Set-ErpPrinterSetting -ProfileName $name -Name $s.Name -Value $entry.Values[$s.Name]
            }
        }
    }
    foreach ($s in $script:GlobalSchema) {
        if ($script:State.Global.Managed -contains $s.Name) { continue }
        Set-ErpPrinterSetting -Name $s.Name -Value $script:State.Global.Values[$s.Name]
    }

    $warning = $null
    try { Sync-ErpPrinterQueue -NoRestart -Confirm:$false } catch { $warning = $_.Exception.Message }
    Restart-ErpPrinterService -Confirm:$false

    $current = $script:State.Current
    Import-State
    Update-ProfileList $current
    Show-GlobalSettings
    Update-Status
    if ($warning) { [Windows.Forms.MessageBox]::Show($form, "Settings were saved, but:`n`n$warning", 'ERP Printer', 'OK', 'Warning') | Out-Null }
    else { $statusLabel.Text = "Saved and applied at $(Get-Date -Format T)." }
    $script:LastSaveOk = $true
}

function Show-GlobalSettings {
    $secretState = @{}
    $script:GlobalControls = Add-SettingRows $globalTable $script:GlobalSchema $script:State.Global.Values $script:State.Global.Managed $secretState
}

function Update-Status {
    $status = Get-ErpPrinterStatus
    $serviceLabel.Text = "Version $($status.Version)    Listener: $($status.Listener)    Uploader: $($status.Uploader)    Updater: $($status.Updater)"
    $statusList.BeginUpdate()
    $statusList.Items.Clear()
    foreach ($p in $status.Profiles) {
        $item = New-Object Windows.Forms.ListViewItem($p.Profile)
        foreach ($text in @($p.Printer, $(if ($p.PrinterOk) { 'OK' } elseif ($p.Enabled) { 'Missing' } else { 'Disabled' }), $p.Pending, $p.Failed, $p.Sent, [string]$p.LastError)) {
            $null = $item.SubItems.Add([string]$text)
        }
        $item.Tag = $p
        if ($p.Failed -gt 0 -or ($p.Enabled -and -not $p.PrinterOk)) { $item.ForeColor = [Drawing.Color]::Firebrick }
        $null = $statusList.Items.Add($item)
    }
    $statusList.EndUpdate()
}

#endregion
#region Layout --------------------------------------------------------------------------

$form = New-Object Windows.Forms.Form
$form.Text = "ERP Printer configuration ($(Get-ErpPrinterVersion))"
$form.Size = New-Object Drawing.Size(940, 700)
$form.MinimumSize = New-Object Drawing.Size(760, 540)
$form.StartPosition = 'CenterScreen'
$form.Font = New-Object Drawing.Font('Segoe UI', 9)
$iconPath = Join-Path $PSScriptRoot 'erp-printer.ico'
if (Test-Path $iconPath) { $form.Icon = New-Object Drawing.Icon($iconPath) }

$tabs = New-Object Windows.Forms.TabControl
$tabs.Dock = 'Fill'
$printersTab = New-Object Windows.Forms.TabPage 'Printers'
$settingsTab = New-Object Windows.Forms.TabPage 'Settings'
$statusTab = New-Object Windows.Forms.TabPage 'Status'
$tabs.TabPages.AddRange(@($printersTab, $settingsTab, $statusTab))

# Printers tab: list on the left, generated settings on the right.
$split = New-Object Windows.Forms.SplitContainer
$split.Dock = 'Fill'
$split.FixedPanel = 'Panel1'
$printersTab.Controls.Add($split)

$profileList = New-Object Windows.Forms.ListBox
$profileList.Dock = 'Fill'
$profileList.IntegralHeight = $false
$profileList.Add_SelectedIndexChanged({
        if ($script:Rendering) { return }
        Invoke-Safely {
            Save-CurrentProfileEdits
            Show-Profile ([string]$profileList.SelectedItem)
        }
    })
$listButtons = New-Object Windows.Forms.FlowLayoutPanel
$listButtons.Dock = 'Bottom'
$listButtons.AutoSize = $true
$listButtons.Controls.Add((New-Button 'Add...' { Add-Profile }))
$removeButton = New-Button 'Remove' { Remove-Profile }
$listButtons.Controls.Add($removeButton)
$split.Panel1.Controls.Add($profileList)
$split.Panel1.Controls.Add($listButtons)

$profileScroll = New-Object Windows.Forms.Panel
$profileScroll.Dock = 'Fill'
$profileScroll.AutoScroll = $true
$profileTable = New-SettingsTable
$profileScroll.Controls.Add($profileTable)
$profileHeader = New-Object Windows.Forms.Label
$profileHeader.Dock = 'Top'
$profileHeader.AutoSize = $true
$profileHeader.ForeColor = [Drawing.Color]::DimGray
$profileHeader.Padding = New-Object Windows.Forms.Padding(10, 6, 0, 0)
$profileButtons = New-Object Windows.Forms.FlowLayoutPanel
$profileButtons.Dock = 'Bottom'
$profileButtons.AutoSize = $true
$profileButtons.Controls.Add((New-Button 'Test connection' { Test-CurrentProfile }))
$profileButtons.Controls.Add((New-Button 'Print test page' { Send-TestPage }))
$split.Panel2.Controls.Add($profileScroll)
$split.Panel2.Controls.Add($profileHeader)
$split.Panel2.Controls.Add($profileButtons)

# Settings tab
$globalScroll = New-Object Windows.Forms.Panel
$globalScroll.Dock = 'Fill'
$globalScroll.AutoScroll = $true
$globalTable = New-SettingsTable
$globalScroll.Controls.Add($globalTable)
$globalButtons = New-Object Windows.Forms.FlowLayoutPanel
$globalButtons.Dock = 'Bottom'
$globalButtons.AutoSize = $true
$globalButtons.Controls.Add((New-Button 'Check for updates' {
            $check = Invoke-ErpPrinterUpdate -CheckOnly
            if (-not $check.UpdateAvailable) {
                [Windows.Forms.MessageBox]::Show($form, $check.Message, 'Updates', 'OK', 'Information') | Out-Null
                return
            }
            $answer = [Windows.Forms.MessageBox]::Show($form, "Version $($check.AvailableVersion) is available (installed: $($check.CurrentVersion)). Install it now?", 'Updates', 'YesNo', 'Question')
            if ($answer -eq 'Yes') {
                $result = Invoke-ErpPrinterUpdate -Force -Confirm:$false
                [Windows.Forms.MessageBox]::Show($form, "$($result.Message)`n`nReopen this window to use the new version.", 'Updates', 'OK', 'Information') | Out-Null
            }
        }))
$settingsTab.Controls.Add($globalScroll)
$settingsTab.Controls.Add($globalButtons)

# Status tab
$serviceLabel = New-Object Windows.Forms.Label
$serviceLabel.Dock = 'Top'
$serviceLabel.AutoSize = $true
$serviceLabel.Padding = New-Object Windows.Forms.Padding(8)
$statusList = New-Object Windows.Forms.ListView
$statusList.Dock = 'Fill'
$statusList.View = 'Details'
$statusList.FullRowSelect = $true
$statusList.MultiSelect = $false
foreach ($column in @(@('Printer profile', 150), @('Windows printer', 150), @('Queue', 70), @('Pending', 65), @('Failed', 60), @('Sent', 55), @('Last error', 360))) {
    $null = $statusList.Columns.Add($column[0], $column[1])
}
$statusButtons = New-Object Windows.Forms.FlowLayoutPanel
$statusButtons.Dock = 'Bottom'
$statusButtons.AutoSize = $true
$statusButtons.Controls.Add((New-Button 'Refresh' { Update-Status }))
$statusButtons.Controls.Add((New-Button 'Send now' {
            $null = Invoke-ErpPrinterUpload -Force
            Update-Status
        }))
$statusButtons.Controls.Add((New-Button 'Retry failed' {
            $count = Restore-ErpPrinterFailedDocument -Confirm:$false
            Update-Status
            $statusLabel.Text = "$count document(s) moved back to the outbox."
        }))
$statusButtons.Controls.Add((New-Button 'Restart service' {
            Restart-ErpPrinterService -Confirm:$false
            Start-Sleep -Seconds 1
            Update-Status
        }))
$statusButtons.Controls.Add((New-Button 'Repair printers' {
            Sync-ErpPrinterQueue -Confirm:$false
            Update-Status
        }))
$statusButtons.Controls.Add((New-Button 'Open data folder' {
            $selected = $statusList.SelectedItems | Select-Object -First 1
            $path = if ($selected) { $selected.Tag.OutboxPath } else { $script:State.Global.Values.DataRoot }
            if (-not (Test-Path $path)) { $path = $script:State.Global.Values.DataRoot }
            Start-Process explorer.exe $path
        }))
$statusButtons.Controls.Add((New-Button 'Event log' { Start-Process eventvwr.msc '/c:Application' }))
$statusTab.Controls.Add($statusList)
$statusTab.Controls.Add($serviceLabel)
$statusTab.Controls.Add($statusButtons)

# Bottom bar
$bottom = New-Object Windows.Forms.TableLayoutPanel
$bottom.Dock = 'Bottom'
$bottom.AutoSize = $true
$bottom.ColumnCount = 3
$bottom.Padding = New-Object Windows.Forms.Padding(6)
$null = $bottom.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle([Windows.Forms.SizeType]::Percent, 100)))
$null = $bottom.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle([Windows.Forms.SizeType]::AutoSize)))
$null = $bottom.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle([Windows.Forms.SizeType]::AutoSize)))
$statusLabel = New-Object Windows.Forms.Label
$statusLabel.AutoSize = $true
$statusLabel.Anchor = 'Left'
$statusLabel.ForeColor = [Drawing.Color]::DimGray
$saveButton = New-Button 'Save && apply' { Save-All }
$closeButton = New-Button 'Close' { $form.Close() }
$bottom.Controls.Add($statusLabel, 0, 0)
$bottom.Controls.Add($saveButton, 1, 0)
$bottom.Controls.Add($closeButton, 2, 0)
$form.AcceptButton = $null

$form.Controls.Add($tabs)
$form.Controls.Add($bottom)
# SplitterDistance can only be set once the container has its real size.
$form.Add_Shown({ $split.SplitterDistance = 220 })

$refreshTimer = New-Object Windows.Forms.Timer
$refreshTimer.Interval = 5000
$refreshTimer.Add_Tick({ if ($tabs.SelectedTab -eq $statusTab) { try { Update-Status } catch { $statusLabel.Text = $_.Exception.Message } } })
$tabs.Add_SelectedIndexChanged({ if ($tabs.SelectedTab -eq $statusTab) { Invoke-Safely { Update-Status } } })

$form.Add_FormClosing({
        $e = $_
        try { Save-CurrentProfileEdits } catch { Write-Verbose "Could not collect edits: $_" }
        if ($script:Dirty) {
            $answer = [Windows.Forms.MessageBox]::Show($form, 'Save your changes before closing?', 'ERP Printer', 'YesNoCancel', 'Question')
            if ($answer -eq 'Cancel') { $e.Cancel = $true; return }
            if ($answer -eq 'Yes') {
                $script:LastSaveOk = $false
                Invoke-Safely { Save-All }
                if (-not $script:LastSaveOk) { $e.Cancel = $true }
            }
        }
    })

#endregion

Import-State
Update-ProfileList
Show-GlobalSettings
if (-not $script:State.Profiles.Count) { $statusLabel.Text = 'No printers yet. Click "Add..." to create one.' }
$refreshTimer.Start()
[void]$form.ShowDialog()
$refreshTimer.Dispose()
$form.Dispose()
