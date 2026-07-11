# scripts/release.ps1 - Bump la version du mod, sync, commit, tag et push.
#
# Usage:
#   .\scripts\release.ps1 -Bump patch
#   .\scripts\release.ps1 -Bump minor
#   .\scripts\release.ps1 -Version 0.2.0
#   .\scripts\release.ps1 -Bump patch -DryRun
#
# Le tag poussé (v*) déclenche le workflow GitHub Actions release.yml
# qui build et publie la release.

param(
    [ValidateSet("major", "minor", "patch")]
    [string]$Bump = "patch",

    [string]$Version = "",

    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path "$PSScriptRoot/.."
Set-Location $Root

# Garde-fou git : working tree propre
$status = git status --porcelain
if ($status) {
    Write-Host "ERROR: working tree not clean. Commit or stash first." -ForegroundColor Red
    git status --short
    exit 1
}

$branch = (git rev-parse --abbrev-ref HEAD).Trim()

# Lecture / bump de versions.json
$versionsPath = Join-Path $Root "versions.json"
$versions = Get-Content $versionsPath -Raw | ConvertFrom-Json
$current = $versions.mod

if ($Version) {
    $newVersion = $Version
} else {
    $parts = $current -split "\."
    if ($parts.Count -ne 3) {
        Write-Host "ERROR: cannot parse version '$current' (expected MAJOR.MINOR.PATCH)" -ForegroundColor Red
        exit 1
    }
    $major = [int]$parts[0]
    $minor = [int]$parts[1]
    $patch = [int]$parts[2]

    switch ($Bump) {
        "major" { $major++; $minor = 0; $patch = 0 }
        "minor" { $minor++; $patch = 0 }
        "patch" { $patch++ }
    }
    $newVersion = "$major.$minor.$patch"
}

Write-Host ""
Write-Host "  mod : $current -> $newVersion" -ForegroundColor Cyan
Write-Host ""

$versions.mod = $newVersion
$json = ($versions | ConvertTo-Json -Depth 4) + "`n"
[System.IO.File]::WriteAllText($versionsPath, $json, (New-Object System.Text.UTF8Encoding $false))

Write-Host "Running sync-versions.js..." -ForegroundColor DarkGray
node scripts/sync-versions.js
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: sync-versions.js failed." -ForegroundColor Red
    exit 1
}

$tag = "v$newVersion"

$existing = git tag --list $tag
if ($existing) {
    Write-Host "ERROR: tag '$tag' already exists." -ForegroundColor Red
    exit 1
}

if ($DryRun) {
    Write-Host ""
    Write-Host "Dry run - would do:" -ForegroundColor Yellow
    Write-Host "  git add -A"
    Write-Host "  git commit -m 'chore(mod): release v$newVersion'"
    Write-Host "  git tag $tag"
    Write-Host "  git push origin $branch --follow-tags"
    Write-Host ""
    Write-Host "Re-run without -DryRun to execute." -ForegroundColor DarkGray
    exit 0
}

git add -A
git commit -m "chore(mod): release v$newVersion"
if ($LASTEXITCODE -ne 0) { exit 1 }

git tag -a $tag -m "release $tag"
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host ""
Write-Host "Pushing branch..." -ForegroundColor DarkGray
git push origin $branch
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: branch push failed. Delete tag locally with:" -ForegroundColor Red
    Write-Host "  git tag -d $tag" -ForegroundColor DarkGray
    exit 1
}

Write-Host "Pushing tag $tag..." -ForegroundColor DarkGray
git push origin $tag
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: tag push failed. Delete locally with:" -ForegroundColor Red
    Write-Host "  git tag -d $tag" -ForegroundColor DarkGray
    exit 1
}

Write-Host ""
Write-Host "  Released $tag" -ForegroundColor Green
Write-Host "  GitHub Actions will build + publish in a few minutes." -ForegroundColor DarkGray
Write-Host ""
