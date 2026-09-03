<#
.SYNOPSIS
    Publish a new UAssetTool release via GitHub Actions.
    Bumps the version in UAssetTool.csproj, tags the commit and pushes to trigger the workflow.

.PARAMETER Version
    Version to release, e.g. '1.5.9'. The 'v' prefix is added automatically for the tag.

.PARAMETER Message
    Optional tag message. Defaults to "Release vX.Y.Z".

.EXAMPLE
    .\publish.ps1 1.5.9
    .\publish.ps1 1.6.0 -Message "Added StaticMesh support"
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Version,

    [string]$Message
)

$ErrorActionPreference = 'Stop'
$tag = "v$Version"

if (-not $Message) {
    $Message = "Release $tag"
}

# The workflow strips the 'v' and feeds this straight to -p:Version, which rejects anything
# that is not a version number.
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    Write-Host "ERROR: Version '$Version' is not in X.Y.Z form." -ForegroundColor Red
    exit 1
}

# Verify we're in a git repo
if (-not (Test-Path .git)) {
    Write-Host "ERROR: Not in a git repository root." -ForegroundColor Red
    exit 1
}

# Checked before anything is modified so a duplicate tag cannot leave a bumped csproj behind.
$existingTag = git tag -l $tag
if ($existingTag) {
    Write-Host "ERROR: Tag '$tag' already exists." -ForegroundColor Red
    exit 1
}

$csprojPath = Join-Path $PSScriptRoot 'src\UAssetTool\UAssetTool.csproj'
if (-not (Test-Path $csprojPath)) {
    Write-Host "ERROR: Could not find $csprojPath" -ForegroundColor Red
    exit 1
}

$originalCsproj = [System.IO.File]::ReadAllText($csprojPath)
$versionPattern = [regex]'(?<open><Version>)(?<ver>[^<]*)(?<close></Version>)'
$versionMatch = $versionPattern.Match($originalCsproj)
if (-not $versionMatch.Success) {
    Write-Host "ERROR: No <Version> element in UAssetTool.csproj." -ForegroundColor Red
    exit 1
}

$currentVersion = $versionMatch.Groups['ver'].Value
if ($currentVersion -eq $Version) {
    Write-Host "Version already $Version, leaving csproj alone." -ForegroundColor DarkGray
} else {
    $replacement = '${open}' + $Version + '${close}'
    $updated = $versionPattern.Replace($originalCsproj, $replacement, 1)
    # UTF8Encoding($false) keeps the file BOM-free, as MSBuild wrote it.
    [System.IO.File]::WriteAllText($csprojPath, $updated, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Bumped version: $currentVersion -> $Version" -ForegroundColor Cyan
}

# Check for uncommitted changes (the bump above is one of them)
$status = git status --porcelain
if ($status) {
    Write-Host "`nUncommitted changes:" -ForegroundColor Yellow
    git status --short
    $confirm = Read-Host "Commit all changes before tagging? (y/n)"
    if ($confirm -eq 'y') {
        git add -A
        git commit -m $Message
    } else {
        # Put the version back so declining leaves the tree as it was found.
        if ($currentVersion -ne $Version) {
            [System.IO.File]::WriteAllText($csprojPath, $originalCsproj, (New-Object System.Text.UTF8Encoding($false)))
            Write-Host "Reverted version bump." -ForegroundColor DarkGray
        }
        Write-Host "Aborted. Commit or stash your changes first." -ForegroundColor Red
        exit 1
    }
}

# Push commits first
Write-Host "`nPushing commits..." -ForegroundColor Cyan
git push
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Failed to push commits." -ForegroundColor Red
    exit 1
}

# Create and push tag
Write-Host "Creating tag: $tag" -ForegroundColor Cyan
git tag -a $tag -m $Message
git push origin $tag
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Failed to push tag." -ForegroundColor Red
    exit 1
}

Write-Host "`nDone! Tag '$tag' pushed." -ForegroundColor Green
Write-Host "GitHub Actions will now build and create the release." -ForegroundColor Green
Write-Host "Track progress at: https://github.com/XzantGaming/UassetToolRivals/actions" -ForegroundColor Cyan
