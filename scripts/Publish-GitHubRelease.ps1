[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Tag,

    [ValidateNotNullOrEmpty()]
    [string]$Repository = "",

    [ValidateNotNullOrEmpty()]
    [string]$PublishedPath = "",

    [ValidateNotNullOrEmpty()]
    [string]$Configuration = "Release",

    [ValidateNotNullOrEmpty()]
    [string]$Runtime = "win-x64",

    [switch]$Draft,

    [switch]$Prerelease,

    [switch]$NoUpload
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "VoicemeeterDelay.csproj"
if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Project file was not found at $projectPath"
}

$safeTag = $Tag -replace '[^A-Za-z0-9._-]', '-'
$PublishedPath = $PublishedPath.Trim("`"", "'")
$artifactRoot = Join-Path $repoRoot "artifacts"
$releaseRoot = Join-Path $artifactRoot "github-release\$safeTag"
$buildDir = Join-Path $releaseRoot "build"
$publishDir = Join-Path $releaseRoot "publish"

$resolvedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
$resolvedReleaseRoot = [System.IO.Path]::GetFullPath($releaseRoot)
if (-not $resolvedReleaseRoot.StartsWith($resolvedArtifactRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Release output path is outside the expected artifacts folder."
}

if (Test-Path -LiteralPath $releaseRoot) {
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

if ($PublishedPath) {
    if ([System.IO.Path]::IsPathRooted($PublishedPath)) {
        $sourcePublishDir = [System.IO.Path]::GetFullPath($PublishedPath)
    }
    else {
        $sourcePublishDir = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $PublishedPath))
    }

    if (-not (Test-Path -LiteralPath $sourcePublishDir)) {
        throw "Published path was not found at $sourcePublishDir"
    }

    Write-Host "Packaging existing Visual Studio publish folder for $Tag..."
    Copy-Item -Path (Join-Path $sourcePublishDir "*") -Destination $publishDir -Recurse -Force
}
else {
    Write-Host "Publishing VoicemeeterDelay $Tag..."
    dotnet restore $projectPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed."
    }

    dotnet publish $projectPath `
        -c $Configuration `
        -r $Runtime `
        -p:PublishProfile=win-x64-single-file `
        -p:SelfContained=false `
        -p:OutputPath="$buildDir\" `
        -o "$publishDir"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed."
    }
}

$exePath = Join-Path $publishDir "VoicemeeterDelay.exe"
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Published EXE was not found at $exePath"
}

$releaseExe = Join-Path $releaseRoot "VoicemeeterDelay-$safeTag-$Runtime.exe"
$releaseZip = Join-Path $releaseRoot "VoicemeeterDelay-$safeTag-$Runtime.zip"

Copy-Item -LiteralPath $exePath -Destination $releaseExe -Force
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $releaseZip -Force

Write-Host "Built release assets:"
Write-Host "  EXE: $releaseExe"
Write-Host "  ZIP: $releaseZip"

if ($NoUpload) {
    Write-Host "NoUpload was set, so GitHub release upload was skipped."
    return
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI was not found. Install gh or run with -NoUpload."
}

gh auth status
if ($LASTEXITCODE -ne 0) {
    throw "GitHub CLI is not logged in. Run: gh auth login"
}

$repoArgs = @()
if (-not $Repository -and (Get-Command git -ErrorAction SilentlyContinue)) {
    $origin = (git -C $repoRoot remote get-url origin 2>$null)
    if ($LASTEXITCODE -eq 0 -and $origin) {
        if ($origin -match 'github\.com[:/](?<owner>[^/]+)/(?<repo>[^/.]+)(\.git)?$') {
            $Repository = "$($Matches.owner)/$($Matches.repo)"
        }
    }
}

if ($Repository) {
    Write-Host "Using GitHub repository $Repository"
    $repoArgs += @("--repo", $Repository)
}
else {
    Write-Warning "No GitHub repository was provided or detected. GitHub CLI will try to infer the repo from the current directory."
}

$target = $null
if (Get-Command git -ErrorAction SilentlyContinue) {
    $target = (git -C $repoRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) {
        $target = $null
    }
}

$viewArgs = @("release", "view", $Tag)
$viewArgs += $repoArgs
gh @viewArgs *> $null
$releaseExists = $LASTEXITCODE -eq 0
$assets = @($releaseExe, $releaseZip)

if ($releaseExists) {
    Write-Host "Release $Tag exists. Uploading assets with --clobber..."
    $uploadArgs = @("release", "upload", $Tag)
    $uploadArgs += $assets
    $uploadArgs += $repoArgs
    $uploadArgs += "--clobber"
    gh @uploadArgs
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub release asset upload failed."
    }

    return
}

Write-Host "Creating GitHub release $Tag..."
$createArgs = @(
    "release",
    "create",
    $Tag
)
$createArgs += $assets
$createArgs += @(
    "--title",
    $Tag,
    "--notes",
    "VoicemeeterDelay release $Tag"
)

if ($target) {
    $createArgs += @("--target", $target)
}

if ($Repository) {
    $createArgs += @("--repo", $Repository)
}

if ($Draft) {
    $createArgs += "--draft"
}

if ($Prerelease) {
    $createArgs += "--prerelease"
}

gh @createArgs
if ($LASTEXITCODE -ne 0) {
    throw "GitHub release creation failed."
}
