@{
    Severity     = @('Error', 'Warning')
    ExcludeRules = @(
        # Private helpers such as Get-ErpOutboxItems read better with plural nouns.
        'PSUseSingularNouns'
        # New-* helpers build in-memory objects and Start-* run long-lived service loops;
        # -WhatIf is supported where state actually changes.
        'PSUseShouldProcessForStateChangingFunctions'
    )
    Rules        = @{
        # The product runs on Windows PowerShell 5.1; development and CI also use PowerShell 7.
        PSUseCompatibleSyntax   = @{
            Enable         = $true
            TargetVersions = @('5.1', '7.0')
        }
        PSUseCompatibleCommands = @{
            Enable         = $true
            TargetProfiles = @(
                'win-48_x64_10.0.17763.0_5.1.17763.316_x64_4.0.30319.42000_framework'
            )
        }
    }
}
