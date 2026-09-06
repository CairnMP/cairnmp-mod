# scripts/generate-il2cpp-refs.ps1
# Genere les assemblies Il2Cpp d'interop MelonLoader en installant CairnLoader
# dans le jeu et en le lancant une seule fois.
# Les assemblies sont copiees dans game-refs/Il2CppAssemblies/ pour le build.
#
# REQUIERT un build de CairnLoader (qui vit dans le repo cairnmp-launcher).
# Par defaut on cherche le repo launcher en frere de celui-ci ; surcharger via :
#     $env:CAIRNLOADER_OUTPUT_DIR = "D:\path\to\launcher\cairnloader\Output\Release\win-x64"
$ErrorActionPreference = "Stop"

$Root = Resolve-Path "$PSScriptRoot/.."
$GameDir = "$env:LOCALAPPDATA\CairnMultiplayerData\game"

# CairnLoader vit dans le repo cairnmp-launcher (frere par defaut).
if ($env:CAIRNLOADER_OUTPUT_DIR) {
    $CairnLoaderSrc = $env:CAIRNLOADER_OUTPUT_DIR
} else {
    $CairnLoaderSrc = Join-Path $Root "..\cairnmp-launcher\launcher\cairnloader\Output\Release\win-x64"
}
$OutputDir = Join-Path $Root "game-refs\Il2CppAssemblies"

Write-Host ""
Write-Host "  CairnMP - Il2Cpp Assembly Generator" -ForegroundColor Cyan
Write-Host ""

# Verifier que le jeu est copie
if (-not (Test-Path "$GameDir\Cairn.exe")) {
    Write-Host "  ERROR: Game not found at $GameDir" -ForegroundColor Red
    Write-Host "  Run the launcher first to copy the game files." -ForegroundColor Yellow
    throw "Game directory not found"
}

# Verifier que CairnLoader est builde
if (-not (Test-Path "$CairnLoaderSrc\version.dll")) {
    Write-Host "  ERROR: CairnLoader not built at $CairnLoaderSrc" -ForegroundColor Red
    Write-Host "  Build it in the cairnmp-launcher repo:" -ForegroundColor Yellow
    Write-Host "    dotnet build launcher/cairnloader/MelonLoader.sln -c Release -p:Platform=x64" -ForegroundColor Yellow
    Write-Host "  Or set CAIRNLOADER_OUTPUT_DIR to its output directory." -ForegroundColor Yellow
    throw "CairnLoader build output not found"
}

# Installer CairnLoader dans le jeu
Write-Host "  [1/4] Installing CairnLoader into game directory..." -ForegroundColor DarkGray
Copy-Item -Force "$CairnLoaderSrc\version.dll" "$GameDir\version.dll"
if (Test-Path "$GameDir\dobby.dll") { Remove-Item -Force "$GameDir\dobby.dll" }
if (Test-Path "$CairnLoaderSrc\dobby.dll") {
    Copy-Item -Force "$CairnLoaderSrc\dobby.dll" "$GameDir\dobby.dll"
}
$LoaderDir = Join-Path $GameDir "CairnLoader"
if (Test-Path $LoaderDir) { Remove-Item -Recurse -Force $LoaderDir }
Copy-Item -Recurse -Force "$CairnLoaderSrc\CairnLoader" "$LoaderDir"
Write-Host "  Done." -ForegroundColor Green

# Lancer le jeu et attendre la generation des assemblies
Write-Host "  [2/4] Launching Cairn to generate Il2Cpp assemblies..." -ForegroundColor DarkGray
Write-Host "         (the game will open briefly, then be closed automatically)" -ForegroundColor DarkGray

$Il2CppDir = Join-Path $LoaderDir "Il2CppAssemblies"
$proc = Start-Process -FilePath "$GameDir\Cairn.exe" -PassThru

# Attendre que les assemblies apparaissent (timeout 5 min)
$timeout = 300
$elapsed = 0
while (-not (Test-Path "$Il2CppDir\UnityEngine.CoreModule.dll")) {
    Start-Sleep -Seconds 2
    $elapsed += 2
    if ($elapsed -ge $timeout) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        throw "Timeout: Il2Cpp assemblies not generated after ${timeout}s"
    }
    if ($proc.HasExited) {
        if (-not (Test-Path "$Il2CppDir\UnityEngine.CoreModule.dll")) {
            throw "Game exited before generating Il2Cpp assemblies"
        }
    }
    Write-Host "." -NoNewline
}
Write-Host ""
Write-Host "  Assemblies generated!" -ForegroundColor Green

# Fermer le jeu
Write-Host "  [3/4] Closing game..." -ForegroundColor DarkGray
if (-not $proc.HasExited) {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

# Copier les assemblies dans game-refs
Write-Host "  [4/4] Copying assemblies to game-refs/..." -ForegroundColor DarkGray
if (Test-Path $OutputDir) { Remove-Item -Recurse -Force $OutputDir }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
Copy-Item -Force "$Il2CppDir\*.dll" $OutputDir

$count = (Get-ChildItem "$OutputDir\*.dll").Count
Write-Host ""
Write-Host "  Done! $count assemblies copied to game-refs/Il2CppAssemblies/" -ForegroundColor Green
Write-Host "  These proprietary references stay local: do not commit them." -ForegroundColor DarkGray
Write-Host "  You can now build the solution or run package-mod.ps1." -ForegroundColor Green
Write-Host ""
