<#
.SYNOPSIS
    Checks commit messages against Conventional Commits, which release-please relies on to
    choose the next version and write CHANGELOG.md.
.DESCRIPTION
    One definition for both callers: the local commit-msg hook (.githooks/commit-msg, -Path)
    and CI (-From/-To over a pull request's commits). Rules:

      - Header is "type(optional scope)!: subject" with a known type, at most 100 characters.
      - The subject and any BREAKING CHANGE note contain no HTML-like tags. release-please
        round-trips the release PR body through an HTML parser; a raw tag there (for example
        the word "picture" in angle brackets) can silently drop the release. Write it in prose.

    Merge, revert, fixup!/squash!/amend! commits are skipped, as commitlint does.
.EXAMPLE
    ./build/Test-CommitMessage.ps1 -Message 'feat(odoo): map users by UPN'
.EXAMPLE
    ./build/Test-CommitMessage.ps1 -From origin/main -To HEAD
#>
[CmdletBinding(DefaultParameterSetName = 'Message')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Message')] [string] $Message,
    [Parameter(Mandatory, ParameterSetName = 'Path')] [string] $Path,
    [Parameter(Mandatory, ParameterSetName = 'Range')] [string] $From,
    [Parameter(ParameterSetName = 'Range')] [string] $To = 'HEAD'
)
$ErrorActionPreference = 'Stop'

$types = 'build', 'chore', 'ci', 'docs', 'feat', 'fix', 'perf', 'refactor', 'revert', 'style', 'test'
$headerPattern = '^(?<type>[a-z]+)(\((?<scope>[^()\r\n]+)\))?(?<breaking>!)?: (?<subject>\S.*)$'
$ignorePattern = '^(Merge |Revert "|(fixup|squash|amend)! |Initial commit$)'
$tagPattern = '</?[A-Za-z][A-Za-z0-9-]*(\s[^<>]*)?>|<!--'

function Get-CommitProblem([string] $Text) {
    $lines = @(($Text -replace "`r`n", "`n").Split("`n") | Where-Object { $_ -notmatch '^#' })
    while ($lines.Count -and -not $lines[0].Trim()) { $lines = @($lines | Select-Object -Skip 1) }
    if (-not $lines.Count) { return 'The commit message is empty.' }
    $header = $lines[0].TrimEnd()
    if ($header -match $ignorePattern) { return }

    if ($header -cnotmatch $headerPattern) {
        return "Header '$header' is not a conventional commit. Use 'type(scope): subject', e.g. 'fix(uploader): retry on HTTP 429'. Types: $($types -join ', ')."
    }
    $type = $Matches.type
    $subject = $Matches.subject
    if ($types -cnotcontains $type) { "Unknown type '$type'. Use one of: $($types -join ', ')." }
    if ($header.Length -gt 100) { "Header is $($header.Length) characters; keep it to 100." }
    if ($subject -cmatch '^[A-Z0-9 _-]+$' -and $subject -cmatch '[A-Z]{2}') { 'Subject must not be all upper case.' }

    $tags = [regex]::Matches($header, $tagPattern) | ForEach-Object Value
    if ($tags) { "Subject contains $(@($tags) -join ', '). Angle-bracket tags can make release-please skip the release; say it in prose." }
    for ($i = 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch '^BREAKING[ -]CHANGE:') { continue }
        $note = New-Object System.Collections.Generic.List[string]
        for ($j = $i; $j -lt $lines.Count -and $lines[$j].Trim(); $j++) { $note.Add($lines[$j]) }
        $tags = [regex]::Matches(($note -join "`n"), $tagPattern) | ForEach-Object Value
        if ($tags) { "BREAKING CHANGE note contains $(@($tags) -join ', '). Angle-bracket tags can make release-please skip the release; say it in prose." }
        $i = $j
    }
}

$commits = switch ($PSCmdlet.ParameterSetName) {
    'Message' { @{ Name = 'message'; Text = $Message } }
    'Path' { @{ Name = 'message'; Text = [IO.File]::ReadAllText((Resolve-Path $Path).Path) } }
    'Range' {
        $shas = @(git rev-list --no-merges --reverse "$From..$To")
        if ($LASTEXITCODE) { throw "git rev-list $From..$To failed." }
        foreach ($sha in $shas) {
            @{ Name = $sha.Substring(0, 7); Text = (git log -1 --format=%B $sha) -join "`n" }
        }
    }
}

$failed = 0
foreach ($commit in @($commits)) {
    $problems = @(Get-CommitProblem $commit.Text)
    if ($problems.Count) {
        $failed++
        $subject = ($commit.Text -split "`r?`n")[0]
        Write-Output "x $($commit.Name): $subject"
        foreach ($problem in $problems) { Write-Output "    $problem" }
    }
}
if ($failed) {
    Write-Output ''
    Write-Output "$failed commit message(s) need fixing. See docs/development.md#commit-messages."
    exit 1
}
if ($PSCmdlet.ParameterSetName -eq 'Range') { Write-Output "All $(@($commits).Count) commit message(s) follow Conventional Commits." }
