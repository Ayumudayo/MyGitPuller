#requires -Version 5.1

[CmdletBinding()]
param(
    [ValidateSet('x64', 'x86', 'arm64')]
    [string]$Arch = 'x64',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('Folder', 'SingleFile')]
    [string]$Mode = 'Folder',

    [switch]$SelfContained,

    [switch]$Clean,

    [switch]$Trim,

    [switch]$NoTrim,

    [switch]$NoCompression
)

$ErrorActionPreference = 'Stop'

$scriptRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($scriptRoot)) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}

$repoRoot = Split-Path -Parent $scriptRoot
$projectPath = Join-Path $repoRoot 'GitPuller.WinUI\GitPuller.WinUI.csproj'
$runtimeIdentifier = "win-$Arch"
$configurationSuffix = if ($Configuration -eq 'Release') { '' } else { "-$($Configuration.ToLowerInvariant())" }
$singleFile = $Mode -eq 'SingleFile'
$selfContainedPublish = $singleFile -or $SelfContained.IsPresent
$outputPrefix = if ($singleFile) {
    'single-file'
} elseif ($selfContainedPublish) {
    'self-contained'
} else {
    'framework-dependent'
}
$publishPath = [System.IO.Path]::GetFullPath(
    (Join-Path $repoRoot "artifacts\publish\GitPuller.WinUI\$outputPrefix$configurationSuffix-$runtimeIdentifier"))
$publishDirProperty = $publishPath.TrimEnd([char[]]@('\', '/')) + '/'

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "WinUI project not found: $projectPath"
}

if ($Trim.IsPresent -and $NoTrim.IsPresent) {
    throw 'Use either -Trim or -NoTrim, not both.'
}

if ($Trim.IsPresent -and -not $selfContainedPublish) {
    throw '-Trim requires -SelfContained or -Mode SingleFile.'
}

if ($Clean -and (Test-Path -LiteralPath $publishPath)) {
    Remove-Item -LiteralPath $publishPath -Recurse -Force
}

New-Item -ItemType Directory -Path $publishPath -Force | Out-Null

$trimEnabled = $Configuration -eq 'Release' -and $Trim.IsPresent
$publishArguments = @(
    'publish', $projectPath,
    '-c', $Configuration,
    '-f', 'net8.0-windows10.0.26100.0',
    '-r', $runtimeIdentifier,
    '-p:PublishProfile=',
    '-p:WindowsPackageType=None',
    "-p:WindowsAppSDKSelfContained=$($selfContainedPublish.ToString().ToLowerInvariant())",
    "-p:SelfContained=$($selfContainedPublish.ToString().ToLowerInvariant())",
    "-p:PublishSingleFile=$($singleFile.ToString().ToLowerInvariant())",
    '-p:PublishReadyToRun=false',
    "-p:PublishTrimmed=$($trimEnabled.ToString().ToLowerInvariant())",
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    "-p:PublishDir=$publishDirProperty"
)

if ($trimEnabled) {
    $publishArguments += '-p:TrimMode=partial'
}

if ($singleFile) {
    $publishArguments += '-p:IncludeAllContentForSelfExtract=true'
}

if ($singleFile -and -not $NoCompression.IsPresent) {
    $publishArguments += '-p:EnableCompressionInSingleFile=true'
}

Write-Host "Publishing GitPuller.WinUI ($Configuration, $runtimeIdentifier, $outputPrefix)..." -ForegroundColor Cyan
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$publishFiles = Get-ChildItem -LiteralPath $publishPath -File -Recurse
$totalBytes = ($publishFiles | Measure-Object -Property Length -Sum).Sum
if ($null -eq $totalBytes) {
    $totalBytes = 0
}

$totalMegabytes = [math]::Round($totalBytes / 1MB, 2)
$mainExecutable = Join-Path $publishPath 'GitPuller.WinUI.exe'

Write-Host "Published to: $publishPath" -ForegroundColor Green
Write-Host "Files: $($publishFiles.Count)"
Write-Host "Size: $totalMegabytes MB"

if (Test-Path -LiteralPath $mainExecutable -PathType Leaf) {
    Write-Host "Executable: $mainExecutable"
}
