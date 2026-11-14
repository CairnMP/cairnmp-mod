<#
.SYNOPSIS
    Starts the two Goldberg copies of Cairn made by setup-goldberg.ps1.

.DESCRIPTION
    Copies the source game's Mods folder into each client first, so a Rider
    deploy reaches both. Then starts each client in a small window that keeps
    simulating in the background. Steam does not need to be running. Each
    client writes its Unity log to its own Logs\Player.log.

    A client that is already running is left alone. The environment variables
    the clients need are set for them only, not left in this terminal.

.EXAMPLE
    ./scripts/start-goldberg.ps1

.EXAMPLE
    ./scripts/start-goldberg.ps1 -Clients 2
#>
[CmdletBinding()]
param(
    [ValidateSet(1, 2)]
    [int[]]$Clients = @(1, 2),
    [string]$Root = (Join-Path $env:LOCALAPPDATA 'CairnMultiplayerData\goldberg'),
    [int]$Width = 960,
    [int]$Height = 540
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$Root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Root).TrimEnd('\')
$sourceFile = Join-Path $Root 'source.txt'
if (-not (Test-Path -LiteralPath $sourceFile)) {
    throw "No Goldberg clients were found in $Root. Run ./scripts/setup-goldberg.ps1 first."
}
$sourcePath = (Get-Content -LiteralPath $sourceFile -Raw -Encoding UTF8).Trim()
$sourceMods = Join-Path $sourcePath 'Mods'
if (-not (Test-Path -LiteralPath $sourceMods)) {
    Write-Warning "$sourceMods was not found. The clients start with the Mods they already have."
}

$launchEnvironment = @{
    CAIRNMP_RUN_IN_BACKGROUND = '1'
    SteamAppId = '1588550'
    # Emulated Steam IDs must stay off the public lobby list. Nothing listens
    # on this port, so each heartbeat fails at once and is only logged.
    CAIRNMP_API_URL = 'http://127.0.0.1:9'
}
$savedEnvironment = @{}
foreach ($name in $launchEnvironment.Keys) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $launchEnvironment[$name], 'Process')
}

try {
    $first = $true
    foreach ($number in ($Clients | Select-Object -Unique)) {
        $destination = Join-Path $Root "client$number"
        $exe = Join-Path $destination 'Cairn.exe'
        if (-not (Test-Path -LiteralPath $exe)) {
            throw "Client $number is missing from $destination. Run ./scripts/setup-goldberg.ps1 first."
        }

        $running = Get-Process -Name 'Cairn' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase) }
        if ($running) {
            Write-Host "Client $number is already running (pid $($running[0].Id)). Leaving it alone."
            continue
        }

        if (Test-Path -LiteralPath $sourceMods) {
            & robocopy.exe $sourceMods (Join-Path $destination 'Mods') /MIR /COPY:DAT /R:2 /W:2 /NFL /NDL /NP /NJH /NJS | Out-Null
            if ($LASTEXITCODE -ge 8) {
                throw "Could not copy Mods into client $number (robocopy exit $LASTEXITCODE)."
            }
        }

        # Let the first window bind the emulator's port before the second starts.
        if (-not $first) { Start-Sleep -Seconds 3 }
        $first = $false

        $logDir = Join-Path $destination 'Logs'
        New-Item -ItemType Directory -Force -Path $logDir | Out-Null
        $logFile = Join-Path $logDir 'Player.log'
        Write-Host "Starting client $number from $destination. Unity log: $logFile"
        # Start-Process joins these with spaces, so the log path needs its own quotes.
        Start-Process -FilePath $exe -WorkingDirectory $destination -ArgumentList @(
            '--melonloader.debug',
            '-screen-fullscreen', '0',
            '-screen-width', "$Width",
            '-screen-height', "$Height",
            '-logFile', "`"$logFile`""
        )
    }
}
finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
}

Write-Host 'Host in one window and join from the other. Mute voice.'
