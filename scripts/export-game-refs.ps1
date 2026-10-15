<#
.SYNOPSIS
    Copies the game assemblies the mod compiles against into a folder.

.DESCRIPTION
    The release workflow cannot download Cairn, so it checks out these references from
    a private repository (CairnMP/cairnmp-game-refs) instead. They are generated from
    the game and must never be published: keep that repository private.

    Only the assemblies named by CairnMultiplayerMod.csproj are copied. Run this again
    after a game update, then commit and push the result to the private repository.

.EXAMPLE
    ./scripts/export-game-refs.ps1 -GameDirectory "D:\SteamLibrary\steamapps\common\Cairn" -Destination ..\cairnmp-game-refs
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$GameDirectory,
    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = "Stop"

$Root   = (Resolve-Path "$PSScriptRoot/..").Path
# Installs made before the move back to MelonLoader keep them under CairnLoader\.
$Source = @("MelonLoader", "CairnLoader") |
    ForEach-Object { Join-Path $GameDirectory "$_\Il2CppAssemblies" } |
    Where-Object { Test-Path (Join-Path $_ "UnityEngine.CoreModule.dll") } |
    Select-Object -First 1
if (-not $Source) {
    throw "No generated assemblies under $GameDirectory. Launch Cairn once with the loader installed."
}

[xml]$Project = Get-Content -Raw (Join-Path $Root "CairnMultiplayerMod/CairnMultiplayerMod.csproj")
$Names = @($Project.Project.ItemGroup.Reference.HintPath |
    Where-Object { $_ -like '$(Il2CppAssembliesDir)\*' } |
    ForEach-Object { Split-Path $_ -Leaf })
if ($Names.Count -eq 0) { throw "CairnMultiplayerMod.csproj names no game assembly." }

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
# Stale references would keep compiling code the game no longer supports.
Get-ChildItem -Path $Destination -Filter *.dll -File | Remove-Item -Force
foreach ($Name in $Names) {
    Copy-Item -LiteralPath (Join-Path $Source $Name) -Destination $Destination
}

Write-Host "  $($Names.Count) assemblies copied to $((Resolve-Path $Destination).Path)" -ForegroundColor Green
