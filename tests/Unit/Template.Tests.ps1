BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
    Import-ErpTestModule
}

Describe 'Get-ErpCleanTitle' {
    It 'turns "<Title>" into "<Expected>"' -TestCases @(
        @{ Title = 'Microsoft Word - Invoice 1042.docx'; Expected = 'Invoice 1042' }
        @{ Title = 'Quote.xlsx'; Expected = 'Quote' }
        @{ Title = 'Report v1.2'; Expected = 'Report v1.2' }
        @{ Title = 'https://example.com/page'; Expected = 'https://example.com/page' }
        @{ Title = '   '; Expected = 'Untitled' }
        @{ Title = ''; Expected = 'Untitled' }
    ) {
        param($Title, $Expected)
        InModuleScope ErpPrinter -Parameters @{ T = $Title } { param($T) Get-ErpCleanTitle $T } | Should -Be $Expected
    }
}

Describe 'Expand-ErpTemplate' {
    BeforeAll {
        $script:Meta = [pscustomobject]@{
            id = 'abc123'; profile = 'Send to Odoo'; user = 'CORP\jdoe'; title = 'Microsoft Word - Offer "Q3".docx'
            computer = 'PC-01'; jobId = '17'; printed = '2026-03-04T05:06:07.0000000+01:00'
        }
    }

    It 'expands all tokens' {
        InModuleScope ErpPrinter -Parameters @{ Meta = $script:Meta } {
            param($Meta)
            $tokens = Get-ErpTemplateTokens $Meta
            Expand-ErpTemplate -Template '{title}|{user}|{username}|{domain}|{computer}|{jobid}|{id}|{profile}' -Tokens $tokens
        } | Should -Be 'Offer "Q3"|CORP\jdoe|jdoe|CORP|PC-01|17|abc123|Send to Odoo'
    }

    It 'formats dates from the print time' {
        InModuleScope ErpPrinter -Parameters @{ Meta = $script:Meta } {
            param($Meta)
            $tokens = Get-ErpTemplateTokens $Meta
            $tokens.date | Should -Match '^\d{4}-\d{2}-\d{2}$'
            $tokens.time | Should -Match '^\d{6}$'
        }
    }

    It 'splits UPN-style user names' {
        InModuleScope ErpPrinter {
            $tokens = Get-ErpTemplateTokens ([pscustomobject]@{ user = 'jdoe@corp.example'; title = 'x'; printed = $null })
            $tokens.username | Should -Be 'jdoe'
            $tokens.domain | Should -Be 'corp.example'
        }
    }

    It 'leaves unknown tokens alone and is case-insensitive' {
        InModuleScope ErpPrinter { Expand-ErpTemplate -Template '{TITLE}-{nope}' -Tokens @{ title = 'A' } } | Should -Be 'A-{nope}'
    }

    It 'escapes values for JSON' {
        $json = InModuleScope ErpPrinter -Parameters @{ Meta = $script:Meta } {
            param($Meta)
            Expand-ErpTemplate -Template '{"description": "{title} by {user}"}' -Tokens (Get-ErpTemplateTokens $Meta) -JsonEscape
        }
        (ConvertFrom-Json $json).description | Should -Be 'Offer "Q3" by CORP\jdoe'
    }
}

Describe 'Get-ErpDocumentFileName' {
    It 'builds a safe PDF file name' {
        InModuleScope ErpPrinter {
            $meta = [pscustomobject]@{ title = 'a/b:c*?"<>|.pdf'; user = 'u'; printed = $null }
            Get-ErpDocumentFileName -Template '{title}' -Meta $meta
        } | Should -Be 'a_b_c______.pdf'
    }

    It 'truncates long names and never ends up empty' {
        InModuleScope ErpPrinter {
            $long = Get-ErpDocumentFileName -Template ('x' * 400) -Meta ([pscustomobject]@{ title = ''; printed = $null })
            $long.Length | Should -Be 154
            Get-ErpDocumentFileName -Template '...' -Meta ([pscustomobject]@{ title = ''; printed = $null }) | Should -Be 'Untitled.pdf'
        }
    }

    It 'supports combined templates' {
        InModuleScope ErpPrinter {
            $meta = [pscustomobject]@{ title = 'Invoice'; user = 'CORP\jdoe'; printed = '2026-01-02T03:04:05Z' }
            Get-ErpDocumentFileName -Template '{username} - {title}' -Meta $meta
        } | Should -Be 'jdoe - Invoice.pdf'
    }
}
