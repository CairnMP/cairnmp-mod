# scripts/package-mod.ps1 — Build du mod CairnMultiplayer et packaging en ZIP :
#   ../CairnMP-packages/cairnmp-mod-{version}.zip  — mod DLLs, hors du depot Git
param(
    [string]$OutputDirectory,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$Root = (Resolve-Path "$PSScriptRoot/..").Path
Set-Location $Root

$versions    = Get-Content "versions.json" | ConvertFrom-Json
$ModVersion  = $versions.mod
$Dist        = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path (Split-Path -Parent $Root) "CairnMP-packages"
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $Root $OutputDirectory))
}

$ConfigurationSuffix = if ($Configuration -eq "Release") { "" } else { "-$($Configuration.ToLowerInvariant())" }
$ModZipName = "cairnmp-mod-$ModVersion$ConfigurationSuffix.zip"

Write-Host ""
Write-Host "  CairnMP Mod Package" -ForegroundColor Cyan
Write-Host "  mod v$ModVersion"
Write-Host ""

if (-not $SkipBuild) {
    # Vérification préalable : les assemblies Il2Cpp doivent exister
    $GameRefsDir = Join-Path $Root "game-refs/Il2CppAssemblies"
    $GameDir     = "$env:LOCALAPPDATA\CairnMultiplayerData\game\CairnLoader\Il2CppAssemblies"

    $Il2CppFound = (Test-Path "$GameRefsDir\UnityEngine.CoreModule.dll") -or
                   (Test-Path "$GameDir\UnityEngine.CoreModule.dll")
    if (-not $Il2CppFound) {
        throw "Missing Il2Cpp assemblies; run scripts/generate-il2cpp-refs.ps1"
    }

    # Vérification préalable : les DLLs MelonLoader doivent exister
    $MelonRefsDir  = Join-Path $Root "game-refs/MelonLoader"
    $MelonLoaderOk = ((Test-Path "$MelonRefsDir\MelonLoader.dll") -and
                      (Test-Path "$MelonRefsDir\Il2CppInterop.Runtime.dll") -and
                      (Test-Path "$MelonRefsDir\0Harmony.dll")) -or
                     ($env:MELON_LOADER_NET6_DIR -and
                      (Test-Path "$env:MELON_LOADER_NET6_DIR\MelonLoader.dll"))
    if (-not $MelonLoaderOk) {
        throw "Missing MelonLoader DLLs; provide game-refs/MelonLoader or MELON_LOADER_NET6_DIR"
    }
}

# 1. Synchronisation des versions
$StepCount = if ($SkipBuild) { 2 } else { 3 }
Write-Host "[1/$StepCount] Checking versions..." -ForegroundColor DarkGray
node scripts/sync-versions.js --check
if ($LASTEXITCODE -ne 0)
{ throw "Version sync failed"
}
Write-Host ""

# 2. Build du mod, sauf quand le script est appele apres un build de solution
if (-not $SkipBuild) {
    Write-Host "[2/3] Building and testing..." -ForegroundColor DarkGray
    dotnet restore CairnMultiplayer.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed" }
    dotnet test CairnMultiplayer.sln -c $Configuration --no-restore -p:DeployMod=false `
        -p:PackageOnSolutionBuild=false --maxcpucount:1 --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Mod build or tests failed" }
    Write-Host ""
}

# 3. Packaging
$PackageStep = if ($SkipBuild) { 2 } else { 3 }
Write-Host "[$PackageStep/$StepCount] Packaging..." -ForegroundColor DarkGray
New-Item -ItemType Directory -Force -Path $Dist | Out-Null

$PayloadMod = Join-Path $Dist ("payload-mod-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path "$PayloadMod/mods" | Out-Null

$ModBin = "CairnMultiplayerMod/bin/$Configuration/net6.0"
Copy-Item "$ModBin/CairnMultiplayerMod.dll"    "$PayloadMod/mods/"
Copy-Item "$ModBin/CairnMultiplayerShared.dll" "$PayloadMod/mods/"
Copy-Item "$ModBin/Concentus.dll" "$PayloadMod/mods/"
Copy-Item "$ModBin/NAudio.Core.dll" "$PayloadMod/mods/"
Copy-Item "$ModBin/NAudio.Wasapi.dll" "$PayloadMod/mods/"
Copy-Item "THIRD-PARTY-NOTICES.txt" "$PayloadMod/mods/"
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

Write-Host "  $ModZipPath" -ForegroundColor Green
Write-Host "  Size:   $ModSize bytes"
Write-Host "  SHA256: $ModSHA256"
Write-Host ""
