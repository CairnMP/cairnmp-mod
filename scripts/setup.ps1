# scripts/setup.ps1 — Configuration one-shot après clone du repo mod.
# À exécuter depuis la racine du repo : pwsh scripts/setup.ps1

$ErrorActionPreference = "Stop"
$Root = Resolve-Path "$PSScriptRoot/.."
Set-Location $Root

Write-Host ""
Write-Host "  CairnMP mod — repo setup" -ForegroundColor Cyan
Write-Host ""

# Hooks git versionnés (.githooks/) → activés via core.hooksPath.
Write-Host "[1/1] Configuring git hooks path..." -ForegroundColor DarkGray
git config core.hooksPath .githooks
if ($LASTEXITCODE -ne 0) { throw "Failed to set core.hooksPath" }
Write-Host "  hooks path set to .githooks/" -ForegroundColor Green

# Permission d'exécution sur le hook (no-op sous Windows, utile sous WSL/Linux).
$Hook = Join-Path $Root ".githooks/pre-push"
if ((Test-Path $Hook) -and ($IsLinux -or $IsMacOS)) {
    chmod +x $Hook
}

Write-Host ""
Write-Host "Setup complete." -ForegroundColor Green
Write-Host "  - 'git push' will now run scripts/check.sh first."
Write-Host "  - Bypass with 'git push --no-verify'."
Write-Host "  - Run checks manually: 'bash scripts/check.sh'"
Write-Host ""
