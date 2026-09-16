[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PresentMonPath,
    [Parameter(Mandatory)][int]$GameProcessId,
    [Parameter(Mandatory)][ValidateSet('vanilla', 'loader', 'solo', 'host', 'client')][string]$Configuration,
    [Parameter(Mandatory)][string]$Scenario,
    [Parameter(Mandatory)][string]$SettingsNote,
    [Parameter(Mandatory)][string]$GameBuild,
    [Parameter(Mandatory)][string]$MachineLabel,
    [ValidateSet('disabled', 'enabled-idle', 'recording')][string]$Instrumentation = 'disabled',
    [ValidateRange(1, 3)][int]$Run = 1,
    [string]$OutputDirectory = (Join-Path $env:LOCALAPPDATA 'CairnMP-Performance')
)
$ErrorActionPreference = 'Stop'
$presentMonExe = (Resolve-Path -LiteralPath $PresentMonPath).Path
$gameProcess = Get-Process -Id $GameProcessId
$gameExecutable = $gameProcess.Path
if (-not $gameExecutable) { throw 'Cannot resolve the selected game executable.' }
if ($Configuration -in @('vanilla', 'loader') -and $Instrumentation -ne 'disabled') {
    throw 'Internal CairnMP instrumentation is unavailable in vanilla/loader-only runs.'
}
$captureDirectory = Join-Path $OutputDirectory ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $captureDirectory | Out-Null
$manifestPath = Join-Path $captureDirectory 'manifest.json'
$csvPath = Join-Path $captureDirectory 'frames.csv'
$captureHotkey = if ($Instrumentation -eq 'enabled-idle') { 'CTRL+F7' } else { 'CTRL+F8' }
$arguments = @('--process_id', "$GameProcessId", '--output_file', $csvPath,
    '--v1_metrics', '--qpc_time', '--hotkey', $captureHotkey, '--delay', '30', '--timed', '180',
    '--terminate_after_timed', '--terminate_on_proc_exit', '--no_console_stats',
    '--session_name', ('CairnMP-' + [Guid]::NewGuid().ToString('N')))
$manifest = [ordered]@{
    SchemaVersion = 1
    Status = 'armed'
    CreatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Configuration = $Configuration
    ConfigurationSource = 'operator-declared; verify loader and Mods contents before starting'
    Scenario = $Scenario
    SettingsNote = $SettingsNote
    GameBuild = $GameBuild
    MachineLabel = $MachineLabel
    Instrumentation = $Instrumentation
    CaptureHotkey = $captureHotkey
    Run = $Run
    WarmupSeconds = 30
    RequestedSeconds = 180
    ProcessId = $GameProcessId
    ExecutableName = [IO.Path]::GetFileName($gameExecutable)
    ExecutableSha256 = (Get-FileHash -LiteralPath $gameExecutable -Algorithm SHA256).Hash
    PresentMonVersion = (Get-Item -LiteralPath $presentMonExe).VersionInfo.FileVersion
    PresentMonSha256 = (Get-FileHash -LiteralPath $presentMonExe -Algorithm SHA256).Hash
    OS = [Environment]::OSVersion.VersionString
    CPU = @((Get-CimInstance Win32_Processor).Name)
    GPU = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion)
    RAMBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
    QpcFrequency = [Diagnostics.Stopwatch]::Frequency
    CsvFiles = @()
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Host "Focus Cairn at the agreed route start, then press $captureHotkey once. Capture uses 30 s warmup and 180 s sampling."
Write-Host 'Do not toggle again during the timed run. No game settings, mods or installations are modified by this script.'
try {
    & $presentMonExe @arguments
    $presentMonExitCode = $LASTEXITCODE
    $manifest['PresentMonExitCode'] = $presentMonExitCode
    $manifest['CsvFiles'] = @(Get-ChildItem -LiteralPath $captureDirectory -Filter 'frames*.csv' | ForEach-Object { $_.Name })
    $manifest['Status'] = if ($presentMonExitCode -eq 0 -and $manifest['CsvFiles'].Count -gt 0) { 'captured-unverified' } else { 'failed' }
    if ($manifest['Status'] -eq 'failed') { throw "PresentMon capture failed (exit $presentMonExitCode), or produced no frames. Check the tool's console output." }
} finally {
    $manifest['FinishedUtc'] = [DateTimeOffset]::UtcNow.ToString('O')
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
}
Write-Host "Capture manifest: $manifestPath"
