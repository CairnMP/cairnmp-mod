# scripts/package-mod.ps1 — Build du mod CairnMultiplayer et packaging en ZIP :
#   dist/cairnmp-mod-{version}.zip  — mod DLLs
$ErrorActionPreference = "Stop"

$Root = Resolve-Path "$PSScriptRoot/.."
Set-Location $Root

$versions    = Get-Content "versions.json" | ConvertFrom-Json
$ModVersion  = $versions.mod
$Dist        = Join-Path $Root "dist"

$ModZipName = "cairnmp-mod-$ModVersion.zip"

Write-Host ""
Write-Host "  CairnMP Mod Package" -ForegroundColor Cyan
Write-Host "  mod v$ModVersion"
Write-Host ""

# Vérification préalable : les assemblies Il2Cpp doivent exister
$GameRefsDir = Join-Path $Root "game-refs/Il2CppAssemblies"
$GameDir     = "$env:LOCALAPPDATA\CairnMultiplayerData\game\CairnLoader\Il2CppAssemblies"

$Il2CppFound = $false
if (Test-Path "$GameRefsDir\UnityEngine.CoreModule.dll")
{
    $Il2CppFound = $true
    Write-Host "  Il2Cpp assemblies: game-refs/ (local, not redistributed)" -ForegroundColor DarkGray
} elseif (Test-Path "$GameDir\UnityEngine.CoreModule.dll")
{
    $Il2CppFound = $true
    Write-Host "  Il2Cpp assemblies: CairnMultiplayerData (local)" -ForegroundColor DarkGray
}

if (-not $Il2CppFound)
{
    Write-Host ""
    Write-Host "  ERROR: Il2Cpp assemblies not found." -ForegroundColor Red
    Write-Host "  Searched:" -ForegroundColor Red
    Write-Host "    1. $GameRefsDir" -ForegroundColor Red
    Write-Host "    2. $GameDir" -ForegroundColor Red
    Write-Host ""
    Write-Host "  To generate them, run:" -ForegroundColor Yellow
    Write-Host "    pwsh scripts/generate-il2cpp-refs.ps1" -ForegroundColor Yellow
    Write-Host ""
    throw "Missing Il2Cpp assemblies"
}

# Vérification préalable : les DLLs MelonLoader doivent exister
$MelonRefsDir  = Join-Path $Root "game-refs/MelonLoader"
$MelonLoaderOk = (Test-Path "$MelonRefsDir\MelonLoader.dll") -and
                 (Test-Path "$MelonRefsDir\Il2CppInterop.Runtime.dll") -and
                 (Test-Path "$MelonRefsDir\0Harmony.dll")

if ($MelonLoaderOk)
{
    Write-Host "  MelonLoader DLLs:  game-refs/MelonLoader/ (local, not redistributed)" -ForegroundColor DarkGray
} elseif ($env:MELON_LOADER_NET6_DIR -and (Test-Path "$env:MELON_LOADER_NET6_DIR\MelonLoader.dll"))
{
    Write-Host "  MelonLoader DLLs:  $env:MELON_LOADER_NET6_DIR (env override)" -ForegroundColor DarkGray
} else
{
    Write-Host ""
    Write-Host "  ERROR: MelonLoader DLLs not found." -ForegroundColor Red
    Write-Host "  Expected in: $MelonRefsDir" -ForegroundColor Red
    Write-Host "    - MelonLoader.dll" -ForegroundColor Red
    Write-Host "    - Il2CppInterop.Runtime.dll" -ForegroundColor Red
    Write-Host "    - 0Harmony.dll" -ForegroundColor Red
    Write-Host ""
    Write-Host "  Set MELON_LOADER_NET6_DIR env var to override the location." -ForegroundColor Yellow
    Write-Host ""
    throw "Missing MelonLoader DLLs"
}
Write-Host ""

# 1. Synchronisation des versions
Write-Host "[1/3] Checking versions..." -ForegroundColor DarkGray
node scripts/sync-versions.js --check
if ($LASTEXITCODE -ne 0)
{ throw "Version sync failed"
}
Write-Host ""

# 2. Build du mod
Write-Host "[2/3] Building and testing..." -ForegroundColor DarkGray
dotnet restore CairnMultiplayer.sln --locked-mode
if ($LASTEXITCODE -ne 0) { throw "Locked restore failed" }
dotnet test CairnMultiplayer.sln -c Release --no-restore -p:DeployMod=false --nologo -v minimal
if ($LASTEXITCODE -ne 0)
{ throw "Mod build or tests failed"
}
Write-Host ""

# 3. Packaging
Write-Host "[3/3] Packaging..." -ForegroundColor DarkGray
New-Item -ItemType Directory -Force -Path $Dist | Out-Null

$PayloadMod = Join-Path $Dist ("payload-mod-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path "$PayloadMod/mods" | Out-Null

$ModBin = "CairnMultiplayerMod/bin/Release/net6.0"
Copy-Item "$ModBin/CairnMultiplayerMod.dll"    "$PayloadMod/mods/"
Copy-Item "$ModBin/CairnMultiplayerShared.dll" "$PayloadMod/mods/"
$ModZipPath = Join-Path $Dist $ModZipName
if (Test-Path $ModZipPath)
{ Remove-Item -Force $ModZipPath
}
Compress-Archive -Path "$PayloadMod/*" -DestinationPath $ModZipPath -CompressionLevel Optimal

# Only remove the verified, unique staging directory created by this invocation.
$ResolvedPayload = (Resolve-Path -LiteralPath $PayloadMod).Path
$ResolvedDist = (Resolve-Path -LiteralPath $Dist).Path
if ([IO.Path]::GetDirectoryName($ResolvedPayload) -ne $ResolvedDist -or
    [IO.Path]::GetFileName($ResolvedPayload) -notmatch '^payload-mod-[0-9a-f]{32}$') {
    throw "Unsafe staging cleanup path: $ResolvedPayload"
}
Remove-Item -LiteralPath $ResolvedPayload -Recurse -Force

$ModSize   = (Get-Item $ModZipPath).Length
$ModSHA256 = (Get-FileHash -Algorithm SHA256 $ModZipPath).Hash.ToLower()

Write-Host "  dist/$ModZipName" -ForegroundColor Green
Write-Host "  Size:   $ModSize bytes"
Write-Host "  SHA256: $ModSHA256"
Write-Host ""
