<#
.SYNOPSIS
    Starts a second Steam login and the cloned Cairn folder on this PC.

.DESCRIPTION
    Your normal Steam stays signed into the main account. This script starts
    Steam as the local Windows user cairnmp2, with its own session name, then
    starts the cloned game as that same user. The first run asks for that
    Windows password and stores it for later runs. Sign the second Steam window
    into an account that owns its own copy of Cairn.

    Start the game from this script. The Play button in the second Steam window
    would launch the shared library copy and collide with client 1.

    A second Steam left open from an earlier run is reused. Two of them, or
    helpers whose Steam has already exited, are closed first. The normal Steam
    window is left alone.

    This does not start client 1. Use scripts/start-local-client.ps1 for that.

.EXAMPLE
    ./scripts/start-second-client.ps1
#>
[CmdletBinding()]
param(
    [string]$GameDirectory = $(if ($env:CAIRN_DIR_2) { $env:CAIRN_DIR_2 } else { 'C:\Users\Public\CairnMP\instance2' }),
    [string]$WindowsUser = 'cairnmp2',
    [string]$IpcName,
    [int]$Width = 960,
    [int]$Height = 540
)

$ErrorActionPreference = 'Stop'

if (-not $IpcName) { $IpcName = $WindowsUser }
if ($WindowsUser -notmatch '^[A-Za-z0-9._-]+$') {
    throw 'Windows user name must be letters, digits, dot, underscore, or hyphen.'
}
if ($IpcName -notmatch '^[A-Za-z0-9._-]+$') {
    throw 'Steam session name must be letters, digits, dot, underscore, or hyphen.'
}

if (-not (Get-LocalUser -Name $WindowsUser -ErrorAction SilentlyContinue)) {
    throw @"
Windows user '$WindowsUser' does not exist.
Create a local account with a password (Settings, Accounts, Other users, no Microsoft account),
run ./scripts/clone-cairn.ps1 so that user can write the game folder, then run this script again.
"@
}

$steamExe = "${env:ProgramFiles(x86)}\Steam\steam.exe"
if (-not (Test-Path -LiteralPath $steamExe)) {
    throw "Steam was not found at $steamExe."
}

if (-not (Test-Path -LiteralPath (Join-Path $GameDirectory 'Cairn.exe'))) {
    throw "Cairn.exe was not found in $GameDirectory. Run ./scripts/clone-cairn.ps1 first."
}

$gameRoot = (Resolve-Path -LiteralPath $GameDirectory).Path
$launcherDir = 'C:\Users\Public\CairnMP'
$steamData = Join-Path $launcherDir 'steam2'
New-Item -ItemType Directory -Force -Path $launcherDir, $steamData, (Join-Path $gameRoot 'Logs') | Out-Null

function Get-SteamClientProcesses {
    @(Get-CimInstance Win32_Process -Filter "Name = 'steam.exe' OR Name = 'steamwebhelper.exe'")
}

function Test-SecondSteamClient {
    param([string]$CommandLine)
    if ([string]::IsNullOrEmpty($CommandLine)) { return $false }
    $ipc = "-master_ipc_name_override $IpcName"
    return $CommandLine.IndexOf($ipc, [StringComparison]::OrdinalIgnoreCase) -ge 0 `
        -or $CommandLine.IndexOf($steamData, [StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Test-SecondAccountHelper {
    param([string]$CommandLine)
    if ([string]::IsNullOrEmpty($CommandLine)) { return $false }
    return $CommandLine.IndexOf("\Users\$WindowsUser\", [StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Get-HelperSteamPid {
    param([string]$CommandLine)
    if ($CommandLine -match '-steampid=(\d+)') { return [int]$Matches[1] }
    return $null
}

function Test-OrphanHelper {
    param($Process, $LiveSteamPids, $HelpersByPid)
    $ownerPid = Get-HelperSteamPid $Process.CommandLine
    if ($ownerPid) { return $LiveSteamPids -notcontains $ownerPid }
    if ($LiveSteamPids -contains $Process.ParentProcessId) { return $false }
    $parent = $HelpersByPid[$Process.ParentProcessId]
    if ($parent) {
        $parentOwner = Get-HelperSteamPid $parent.CommandLine
        if ($parentOwner -and ($LiveSteamPids -contains $parentOwner)) { return $false }
    }
    return $true
}

function Invoke-AsWindowsUser {
    param([string]$CommandPath)
    # The launcher path has no spaces, so runas receives it as one argument.
    & runas.exe /profile /savecred "/user:$WindowsUser" $CommandPath
    if ($LASTEXITCODE -ne 0) {
        throw "runas exited with code $LASTEXITCODE. The first run asks for the password of $WindowsUser."
    }
}

function Stop-LeftoverSteam {
    param($Processes)
    $ids = @($Processes | ForEach-Object ProcessId | Select-Object -Unique)
    foreach ($id in $ids) {
        Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
    }

    $deadline = (Get-Date).AddSeconds(10)
    do {
        $still = @(Get-Process -Id $ids -ErrorAction SilentlyContinue)
        if ($still.Count -eq 0) { return }
        Start-Sleep -Milliseconds 400
    } while ((Get-Date) -lt $deadline)

    $killCmd = Join-Path $launcherDir 'stop-steam2.cmd'
    $pidArgs = ($still | ForEach-Object { "/PID $($_.Id)" }) -join ' '
    @"
@echo off
taskkill /F $pidArgs
"@ | Set-Content -LiteralPath $killCmd -Encoding ascii
    Invoke-AsWindowsUser $killCmd

    Start-Sleep -Seconds 2
    $still = @(Get-Process -Id $ids -ErrorAction SilentlyContinue)
    if ($still.Count -gt 0) {
        throw "Could not close leftover second Steam (pids $($still.Id -join ', ')). Close that Steam window, then run this script again."
    }
}

$steamProcesses = @(Get-SteamClientProcesses)
$mainSteam = @($steamProcesses | Where-Object {
    $_.Name -eq 'steam.exe' -and -not (Test-SecondSteamClient $_.CommandLine)
})
if ($mainSteam.Count -eq 0) {
    throw 'Open your normal Steam and sign into the main account first, then run this script again.'
}

$liveSteamPids = @($steamProcesses | Where-Object Name -eq 'steam.exe' | ForEach-Object ProcessId)
$secondSteam = @($steamProcesses | Where-Object {
    $_.Name -eq 'steam.exe' -and (Test-SecondSteamClient $_.CommandLine)
})
$secondHelpers = @($steamProcesses | Where-Object {
    $_.Name -eq 'steamwebhelper.exe' -and (Test-SecondAccountHelper $_.CommandLine)
})
$helpersByPid = @{}
foreach ($helper in $secondHelpers) { $helpersByPid[$helper.ProcessId] = $helper }
$orphans = @($secondHelpers | Where-Object { Test-OrphanHelper $_ $liveSteamPids $helpersByPid })

$reuseSecondSteam = $secondSteam.Count -eq 1
$toClose = @()
if ($secondSteam.Count -gt 1) {
    $toClose = @($secondSteam) + @($secondHelpers)
    $reuseSecondSteam = $false
} elseif ($orphans.Count -gt 0) {
    $toClose = @($orphans)
}

if ($toClose.Count -gt 0) {
    Write-Host "Closing leftover second Steam processes: $($toClose.ProcessId -join ', ')"
    Stop-LeftoverSteam $toClose
}

$logFile = Join-Path $gameRoot 'Logs\Player.log'
$steamLauncher = Join-Path $launcherDir 'launch-steam.cmd'
$gameLauncher = Join-Path $launcherDir 'launch-instance2.cmd'

@"
@echo off
start "" "$steamExe" -master_ipc_name_override $IpcName -userdatapath "$steamData" -userchooser
"@ | Set-Content -LiteralPath $steamLauncher -Encoding ascii

@"
@echo off
setlocal
set steam_master_ipc_name_override=$IpcName
set CAIRNMP_RUN_IN_BACKGROUND=1
set SteamAppId=1588550
if not exist "$gameRoot\Logs" mkdir "$gameRoot\Logs"
start "Cairn client 2" /D "$gameRoot" "$gameRoot\Cairn.exe" --melonloader.debug -screen-fullscreen 0 -screen-width $Width -screen-height $Height -logFile "$logFile"
"@ | Set-Content -LiteralPath $gameLauncher -Encoding ascii

if ($reuseSecondSteam) {
    Write-Host "Second Steam is already running (pid $($secondSteam[0].ProcessId)). Not starting another."
} else {
    Write-Host "Starting Steam as $WindowsUser. The first run asks for that Windows password and saves it for next time."
    Write-Host "In the second Steam window, sign into the account that owns the second copy of Cairn."
    Invoke-AsWindowsUser $steamLauncher
}
Write-Host "Leave that window's Play button alone. This script starts the cloned game."

Write-Host 'When that Steam window is signed in, press Enter to start client 2.'
Read-Host | Out-Null

Invoke-AsWindowsUser $gameLauncher
Write-Host "Client 2 is starting from $gameRoot."
Write-Host "Unity log: $logFile"
Write-Host 'Host in one window and join from the other. Mute voice.'
