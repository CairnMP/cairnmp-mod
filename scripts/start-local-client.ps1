<#
.SYNOPSIS
    Starts the usual Cairn folder as client 1 for a same-PC multiplayer test.

.DESCRIPTION
    Opens a small window, writes the Unity log inside that game folder, and
    tells the mod to keep simulating when the window is in the background.
    Steam must already be open and signed into the account that owns this copy.
    This script does not start a second Steam client.

.EXAMPLE
    ./scripts/start-local-client.ps1
#>
[CmdletBinding()]
param(
    [string]$GameDirectory,
    [int]$Width = 960,
    [int]$Height = 540
)

$ErrorActionPreference = 'Stop'

if (-not $GameDirectory) { $GameDirectory = $env:CAIRN_DIR }
if (-not $GameDirectory) { $GameDirectory = Join-Path $env:LOCALAPPDATA 'CairnMultiplayerData\game' }

$exe = Join-Path $GameDirectory 'Cairn.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Cairn.exe was not found in $GameDirectory."
}

$gameRoot = (Resolve-Path -LiteralPath $GameDirectory).Path

$logDir = Join-Path $gameRoot 'Logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logFile = Join-Path $logDir 'Player.log'

$env:CAIRNMP_RUN_IN_BACKGROUND = '1'
$env:SteamAppId = '1588550'

Write-Host "Starting client 1 from $gameRoot."
Write-Host "Unity log: $logFile"
Start-Process -FilePath $exe -WorkingDirectory $gameRoot -ArgumentList @(
    '--melonloader.debug',
    '-screen-fullscreen', '0',
    '-screen-width', "$Width",
    '-screen-height', "$Height",
    '-logFile', $logFile
)
