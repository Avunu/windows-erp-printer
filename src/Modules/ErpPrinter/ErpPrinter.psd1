@{
    RootModule           = 'ErpPrinter.psm1'
    ModuleVersion        = '0.0.0' # x-release-please-version
    GUID                 = '6f0f4c1e-6a7b-4f3e-9d51-2f3b7f1c9a10'
    Author               = 'ERP Printer contributors'
    CompanyName          = 'ERP Printer'
    Copyright            = '(c) 2026 Kevin Shenk. MIT License.'
    Description          = 'Virtual "Print to ERP" printers for Windows: a named-pipe port on the Microsoft Print to PDF driver, a local outbox and uploaders for Odoo, ERPNext and generic webhooks.'
    PowerShellVersion    = '5.1'
    CompatiblePSEditions = @('Desktop', 'Core')
    FunctionsToExport    = @(
        'Get-ErpPrinterSettingSchema'
        'Get-ErpPrinterConfig'
        'Get-ErpPrinterProfile'
        'New-ErpPrinterProfile'
        'Remove-ErpPrinterProfile'
        'Set-ErpPrinterSetting'
        'Set-ErpPrinterSecret'
        'Test-ErpPrinterSecret'
        'Test-ErpPrinterProfile'
        'Test-ErpPrinterConnection'
        'Install-ErpPrinter'
        'Uninstall-ErpPrinter'
        'Sync-ErpPrinterQueue'
        'Start-ErpPrinterListener'
        'Start-ErpPrinterUploader'
        'Invoke-ErpPrinterUpload'
        'Get-ErpPrinterStatus'
        'Restore-ErpPrinterFailedDocument'
        'Invoke-ErpPrinterUpdate'
        'Get-ErpPrinterVersion'
        'Restart-ErpPrinterService'
    )
    CmdletsToExport      = @()
    VariablesToExport    = @()
    AliasesToExport      = @()
    PrivateData          = @{
        PSData = @{
            Tags       = @('Printing', 'Odoo', 'ERPNext', 'Frappe', 'PDF', 'Windows')
            LicenseUri = 'https://opensource.org/licenses/MIT'
        }
    }
}
