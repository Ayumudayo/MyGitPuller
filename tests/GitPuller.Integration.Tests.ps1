param(
    [switch]$KeepTemp
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'GitPuller.csproj'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("MyGitPullerTests-" + [System.Guid]::NewGuid().ToString('N'))

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [string]$WorkingDirectory = (Get-Location).Path
    )

    Push-Location -LiteralPath $WorkingDirectory
    try {
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
        Pop-Location
    }

    $text = ($output | Out-String).Trim()
    if ($exitCode -ne 0) {
        throw "Command failed ($exitCode): $FilePath $($Arguments -join ' ')`n$text"
    }

    return $text
}

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Equal {
    param(
        [string]$Expected,
        [string]$Actual,
        [string]$Message
    )

    if ($Expected.Trim() -ne $Actual.Trim()) {
        throw "$Message`nExpected: $Expected`nActual:   $Actual"
    }
}

try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null

    $gitLfsAvailable = $false
    try {
        Invoke-Native 'git' @('lfs', 'version') $repoRoot | Out-Null
        $gitLfsAvailable = $true
    }
    catch {
        Write-Host 'Skipping Git LFS integration assertions because git lfs is not installed.'
    }

    $appOut = Join-Path $testRoot 'app'
    Invoke-Native 'dotnet' @('publish', $projectPath, '-c', 'Debug', '-o', $appOut, '--nologo') $repoRoot | Out-Null

    $gitPullerExe = Join-Path $appOut 'GitPuller.exe'
    Assert-True (Test-Path -LiteralPath $gitPullerExe) "Published GitPuller.exe was not found: $gitPullerExe"

    $caseRoot = Join-Path $testRoot 'all-branches'
    $remotePath = Join-Path $caseRoot 'remote.git'
    $seedPath = Join-Path $caseRoot 'seed'
    $scanRoot = Join-Path $caseRoot 'scan'
    $repoPath = Join-Path $scanRoot 'repo'

    New-Item -ItemType Directory -Path $caseRoot, $scanRoot | Out-Null

    Invoke-Native 'git' @('init', '--bare', $remotePath) $caseRoot | Out-Null
    Invoke-Native 'git' @('clone', $remotePath, $seedPath) $caseRoot | Out-Null
    Invoke-Native 'git' @('config', 'user.name', 'Test User') $seedPath | Out-Null
    Invoke-Native 'git' @('config', 'user.email', 'test@example.invalid') $seedPath | Out-Null
    Invoke-Native 'git' @('checkout', '-b', 'main') $seedPath | Out-Null

    Set-Content -LiteralPath (Join-Path $seedPath 'main.txt') -Value 'main' -Encoding UTF8
    Invoke-Native 'git' @('add', 'main.txt') $seedPath | Out-Null
    Invoke-Native 'git' @('commit', '-m', 'main commit') $seedPath | Out-Null
    Invoke-Native 'git' @('push', '-u', 'origin', 'main') $seedPath | Out-Null
    Invoke-Native 'git' @('symbolic-ref', 'HEAD', 'refs/heads/main') $remotePath | Out-Null

    Invoke-Native 'git' @('checkout', '-b', 'feature/alpha') $seedPath | Out-Null
    Set-Content -LiteralPath (Join-Path $seedPath 'feature.txt') -Value 'feature' -Encoding UTF8
    Invoke-Native 'git' @('add', 'feature.txt') $seedPath | Out-Null
    Invoke-Native 'git' @('commit', '-m', 'feature commit') $seedPath | Out-Null
    Invoke-Native 'git' @('push', '-u', 'origin', 'feature/alpha') $seedPath | Out-Null

    Invoke-Native 'git' @('clone', '--branch', 'main', $remotePath, $repoPath) $scanRoot | Out-Null
    Invoke-Native 'git' @('tag', 'v-delete-me') $seedPath | Out-Null
    Invoke-Native 'git' @('push', 'origin', 'v-delete-me') $seedPath | Out-Null

    foreach ($n in 1..5) {
        Invoke-Native 'git' @('checkout', '-B', "batch/$n", 'main') $seedPath | Out-Null
        Set-Content -LiteralPath (Join-Path $seedPath "batch-$n.txt") -Value "batch $n" -Encoding UTF8
        Invoke-Native 'git' @('add', "batch-$n.txt") $seedPath | Out-Null
        Invoke-Native 'git' @('commit', '-m', "batch branch $n") $seedPath | Out-Null
        Invoke-Native 'git' @('push', '-u', 'origin', "batch/$n") $seedPath | Out-Null
    }

    Invoke-Native 'git' @('checkout', '-B', 'topic.with-dots', 'main') $seedPath | Out-Null
    Set-Content -LiteralPath (Join-Path $seedPath 'topic.txt') -Value 'topic branch' -Encoding UTF8
    Invoke-Native 'git' @('add', 'topic.txt') $seedPath | Out-Null
    Invoke-Native 'git' @('commit', '-m', 'topic branch') $seedPath | Out-Null
    Invoke-Native 'git' @('push', '-u', 'origin', 'topic.with-dots') $seedPath | Out-Null

    $initialBranchList = Invoke-Native 'git' @('-C', $repoPath, 'branch', '--format=%(refname:short)') $scanRoot
    Assert-True ($initialBranchList -notmatch 'feature/alpha') 'Test setup unexpectedly created the feature branch locally.'

    Invoke-Native $gitPullerExe @('--root', $scanRoot, '--rescan', '-w', '1', '-t', '120', '--verbose-report') $scanRoot | Out-Null

    $remoteFeatureSha = Invoke-Native 'git' @('-C', $repoPath, 'rev-parse', 'refs/remotes/origin/feature/alpha') $scanRoot
    $localFeatureSha = Invoke-Native 'git' @('-C', $repoPath, 'rev-parse', 'refs/heads/feature/alpha') $scanRoot
    Assert-Equal $remoteFeatureSha $localFeatureSha 'Feature branch was not mirrored into a local branch.'
    $remoteTopicSha = Invoke-Native 'git' @('-C', $repoPath, 'rev-parse', 'refs/remotes/origin/topic.with-dots') $scanRoot
    $localTopicSha = Invoke-Native 'git' @('-C', $repoPath, 'rev-parse', 'refs/heads/topic.with-dots') $scanRoot
    Assert-Equal $remoteTopicSha $localTopicSha 'Branch with dot characters was not mirrored correctly.'
    $report = Get-Content -Raw -LiteralPath (Join-Path $scanRoot 'git_update_report.md')
    $updateRefLines = [regex]::Matches($report, 'git update-ref').Count
    Assert-True ($updateRefLines -le 2) 'Branch sync still runs update-ref once per branch instead of batching.'

    Invoke-Native 'git' @('-C', $repoPath, 'tag', '-d', 'v-delete-me') $scanRoot | Out-Null
    Invoke-Native 'git' @('push', 'origin', ':refs/tags/v-delete-me') $seedPath | Out-Null
    Invoke-Native $gitPullerExe @('--root', $scanRoot, '--rescan', '-w', '1', '-t', '120') $scanRoot | Out-Null
    $deletedTag = & git -C $repoPath rev-parse --verify --quiet refs/tags/v-delete-me
    Assert-True ($LASTEXITCODE -ne 0) 'Deleted remote tag was not pruned locally.'

    Invoke-Native 'git' @('checkout', 'feature/alpha') $seedPath | Out-Null
    Set-Content -LiteralPath (Join-Path $seedPath 'feature.txt') -Value 'remote feature update' -Encoding UTF8
    Invoke-Native 'git' @('add', 'feature.txt') $seedPath | Out-Null
    Invoke-Native 'git' @('commit', '-m', 'remote feature update') $seedPath | Out-Null
    Invoke-Native 'git' @('push', 'origin', 'feature/alpha') $seedPath | Out-Null

    Invoke-Native 'git' @('-C', $repoPath, 'config', 'user.name', 'Local Test User') $scanRoot | Out-Null
    Invoke-Native 'git' @('-C', $repoPath, 'config', 'user.email', 'local-test@example.invalid') $scanRoot | Out-Null
    Invoke-Native 'git' @('-C', $repoPath, 'checkout', 'feature/alpha') $scanRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $repoPath 'feature.txt') -Value 'local divergent update' -Encoding UTF8
    Invoke-Native 'git' @('-C', $repoPath, 'add', 'feature.txt') $scanRoot | Out-Null
    Invoke-Native 'git' @('-C', $repoPath, 'commit', '-m', 'local divergent update') $scanRoot | Out-Null
    Invoke-Native 'git' @('-C', $repoPath, 'checkout', '-b', 'local-only') $scanRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $repoPath 'local-only.txt') -Value 'local only' -Encoding UTF8
    Invoke-Native 'git' @('-C', $repoPath, 'add', 'local-only.txt') $scanRoot | Out-Null
    Invoke-Native 'git' @('-C', $repoPath, 'commit', '-m', 'local-only branch') $scanRoot | Out-Null

    Invoke-Native 'git' @('checkout', 'main') $seedPath | Out-Null
    Set-Content -LiteralPath (Join-Path $seedPath 'main.txt') -Value 'remote main update' -Encoding UTF8
    Invoke-Native 'git' @('add', 'main.txt') $seedPath | Out-Null
    Invoke-Native 'git' @('commit', '-m', 'remote main update') $seedPath | Out-Null
    Invoke-Native 'git' @('push', 'origin', 'main') $seedPath | Out-Null

    if ($gitLfsAvailable) {
        Invoke-Native 'git' @('checkout', 'main') $seedPath | Out-Null
        Invoke-Native 'git' @('lfs', 'install', '--local') $seedPath | Out-Null
        Invoke-Native 'git' @('lfs', 'track', '*.bin') $seedPath | Out-Null
        Set-Content -LiteralPath (Join-Path $seedPath 'asset.bin') -Value 'large-ish content' -Encoding UTF8
        Invoke-Native 'git' @('add', '.gitattributes', 'asset.bin') $seedPath | Out-Null
        Invoke-Native 'git' @('commit', '-m', 'add lfs asset') $seedPath | Out-Null
        Invoke-Native 'git' @('push', 'origin', 'main') $seedPath | Out-Null
    }

    Invoke-Native 'git' @('-C', $repoPath, 'checkout', 'main') $scanRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $repoPath 'main.txt') -Value 'dirty local main' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $repoPath 'untracked.tmp') -Value 'delete me' -Encoding UTF8

    $staleIndexLock = Join-Path $repoPath '.git\index.lock'
    Set-Content -LiteralPath $staleIndexLock -Value 'stale lock' -Encoding UTF8
    (Get-Item -LiteralPath $staleIndexLock).LastWriteTimeUtc = [DateTime]::UtcNow.AddHours(-2)

    Invoke-Native $gitPullerExe @('--root', $scanRoot, '--rescan', '-w', '1', '-t', '120') $scanRoot | Out-Null

    $remoteFeatureSha = Invoke-Native 'git' @('-C', $repoPath, 'rev-parse', 'refs/remotes/origin/feature/alpha') $scanRoot
    $localFeatureSha = Invoke-Native 'git' @('-C', $repoPath, 'rev-parse', 'refs/heads/feature/alpha') $scanRoot
    Assert-Equal $remoteFeatureSha $localFeatureSha 'Diverged local feature branch was not force-synced to the remote branch.'
    $localOnlyExists = & git -C $repoPath rev-parse --verify --quiet refs/heads/local-only
    Assert-True ($LASTEXITCODE -ne 0) 'Local-only branch was not deleted during force mirror sync.'

    $mainContent = Get-Content -Raw -LiteralPath (Join-Path $repoPath 'main.txt')
    Assert-Equal 'remote main update' $mainContent 'Dirty tracked worktree content was not reset to the remote main branch.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $repoPath 'untracked.tmp'))) 'Untracked worktree file was not removed.'
    Assert-True (-not (Test-Path -LiteralPath $staleIndexLock)) 'Stale .git/index.lock was not removed.'
    if ($gitLfsAvailable) {
        Invoke-Native 'git' @('-C', $repoPath, 'lfs', 'fsck') $scanRoot | Out-Null
    }

    $freshLock = Join-Path $repoPath '.git\index.lock'
    Set-Content -LiteralPath $freshLock -Value 'fresh lock' -Encoding UTF8
    $failed = $false
    try {
        Invoke-Native $gitPullerExe @('--root', $scanRoot, '--rescan', '-w', '1', '-t', '120', '--stale-lock-minutes', '999') $scanRoot | Out-Null
    }
    catch {
        $failed = $true
    }
    Assert-True $failed 'Fresh lock should not be removed when stale-lock threshold is high.'
    Remove-Item -LiteralPath $freshLock -Force

    Invoke-Native $gitPullerExe @('--root', $scanRoot, '--rescan', '-w', '1', '-t', '120') $scanRoot | Out-Null
    Start-Sleep -Milliseconds 1100
    Invoke-Native $gitPullerExe @('--root', $scanRoot, '-w', '1', '-t', '120') $scanRoot | Out-Null
    $reports = Get-ChildItem -LiteralPath $scanRoot -Filter 'git_update_report*.md'
    Assert-True ($reports.Count -ge 2) 'Reports were overwritten instead of using per-run report paths.'

    $report = Get-Content -Raw -LiteralPath (Join-Path $scanRoot 'git_update_report.md')
    Assert-True ($report -notmatch 'Operations:') 'Default report should not include verbose operation details.'
    Invoke-Native $gitPullerExe @('--root', $scanRoot, '--rescan', '-w', '1', '-t', '120', '--verbose-report') $scanRoot | Out-Null
    $verboseReport = Get-Content -Raw -LiteralPath (Join-Path $scanRoot 'git_update_report.md')
    Assert-True ($verboseReport -match 'Operations:') 'Verbose report did not include operation details.'
    if ($gitLfsAvailable) {
        Assert-True ($verboseReport -match 'git lfs fetch --all --prune') 'Git LFS fetch was not executed for an LFS repository.'
    }

    $cachePath = Join-Path $scanRoot '.git_repo_cache.json'
    @($repoPath, $repoPath) | ConvertTo-Json | Set-Content -LiteralPath $cachePath -Encoding UTF8

    $duplicateRunOutput = Invoke-Native $gitPullerExe @('--root', $scanRoot, '-w', '2', '-t', '120') $scanRoot
    Assert-True ($duplicateRunOutput -match 'Found 1 repositories') 'Duplicate cache entries were not de-duplicated before processing.'

    Write-Host 'Integration tests passed.'
}
finally {
    if ($KeepTemp) {
        Write-Host "Kept temp directory: $testRoot"
    }
    elseif (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
