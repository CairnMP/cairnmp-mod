<#
.SYNOPSIS
    Makes two Goldberg (gbe_fork) copies of Cairn for same-PC multiplayer tests.

.DESCRIPTION
    For CairnMP developers who own Cairn on Steam. Copies the modded game
    folder twice and replaces steam_api64.dll in each copy with the gbe_fork
    Steam emulator. Each copy gets its own emulated Steam name and ID, and the
    two find each other over 127.0.0.1. One purchase is enough, and Steam does
    not need to be running.

    The script stops unless Cairn is installed through Steam on this PC. It
    never changes the source folder. Files under Cairn_Data are hard links when
    the volume allows it, so the copies take little extra space.

    Run it again after a game, MelonLoader or launcher update. Each copy keeps
    its UserData, Logs and emulator saves. Start both windows with
    ./scripts/start-goldberg.ps1.

.EXAMPLE
    ./scripts/setup-goldberg.ps1

.EXAMPLE
    ./scripts/setup-goldberg.ps1 -Source 'D:\Games\Cairn'
#>
[CmdletBinding()]
param(
    [string]$Source,
    [string]$Root = (Join-Path $env:LOCALAPPDATA 'CairnMultiplayerData\goldberg')
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$ProgressPreference = 'SilentlyContinue'

# gbe_fork is the maintained Goldberg fork. The original Goldberg stops at
# Steamworks SDK 1.57 and lacks SteamInternal_SteamAPI_Init, which the mod calls.
# Change these together, here and in setup-goldberg.sh.
$GbeRelease = 'release-2026_09_27'
$GbeArchive = 'emu-win-release-vs22.7z'
$GbeArchiveSha256 = '300186c34f49a9ea75b33ce2af299267cc1dad0225cf45e81a229578d4e11c85'
$GbeDllEntry = 'release/regular/x64/steam_api64.dll'
$GbeDllSha256 = '70d9826a402d3a78b0c07e9f7ea7c6832468a1113013f31ea570fccaa5445ea9'

$CairnAppId = '1588550'
$RootMarker = '.cairnmp-goldberg-root'
$ClientMarker = '.cairnmp-goldberg'
# Kept when the script runs again over an existing client.
$KeepEntries = @('UserData', 'Logs', 'gse-saves', $ClientMarker)
$Clients = @(
    @{ Number = 1; Name = 'CairnDev1'; SteamId = '76561199548815729' },
    @{ Number = 2; Name = 'CairnDev2'; SteamId = '76561199548815730' }
)

function Test-Sha256 {
    param([string]$Path, [string]$Expected)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ieq $Expected
}

function Get-FullPath {
    param([string]$Path)
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path).TrimEnd('\')
}

function Get-SteamLibraries {
    $roots = @()
    $registry = Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -Name SteamPath -ErrorAction SilentlyContinue
    if ($registry -and $registry.SteamPath) { $roots += ($registry.SteamPath -replace '/', '\') }
    if (${env:ProgramFiles(x86)}) { $roots += (Join-Path ${env:ProgramFiles(x86)} 'Steam') }

    $libraries = [System.Collections.Generic.List[string]]::new()
    foreach ($steamRoot in ($roots | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $steamRoot)) { continue }
        $libraries.Add($steamRoot)
        $vdf = Join-Path $steamRoot 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path -LiteralPath $vdf)) { continue }
        $text = Get-Content -LiteralPath $vdf -Raw -Encoding UTF8
        foreach ($match in [regex]::Matches($text, '"path"\s+"([^"]+)"')) {
            $libraries.Add(($match.Groups[1].Value -replace '\\\\', '\'))
        }
    }
    return @($libraries | Select-Object -Unique)
}

function Resolve-CairnSource {
    param([string]$Requested, [string[]]$Libraries)

    if ($Requested) {
        if (-not (Test-Path -LiteralPath (Join-Path $Requested 'Cairn.exe'))) {
            throw "Cairn.exe was not found in $Requested."
        }
        return (Resolve-Path -LiteralPath $Requested).ProviderPath.TrimEnd('\')
    }

    $candidates = @($env:CAIRN_DIR, (Join-Path $env:LOCALAPPDATA 'CairnMultiplayerData\game')) +
        @($Libraries | ForEach-Object { Join-Path $_ 'steamapps\common\Cairn' })
    foreach ($candidate in ($candidates | Where-Object { $_ })) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'Cairn.exe')) {
            return (Resolve-Path -LiteralPath $candidate).ProviderPath.TrimEnd('\')
        }
    }
    throw 'Cairn.exe was not found. Pass -Source with the folder where CairnMP is installed.'
}

function Initialize-Root {
    param([string]$Path)

    if (Test-Path -LiteralPath $Path) {
        $ours = Test-Path -LiteralPath (Join-Path $Path $RootMarker)
        $empty = -not (Get-ChildItem -LiteralPath $Path -Force | Select-Object -First 1)
        if (-not $ours -and -not $empty) {
            throw "$Path already holds other files. Pass a new or empty folder as -Root."
        }
    }
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
    Set-Content -LiteralPath (Join-Path $Path $RootMarker) -Value 'Made by scripts/setup-goldberg.ps1 in the CairnMP repository.' -Encoding ascii
}

function Get-VerifiedArchive {
    $cache = Join-Path $Root 'gbe-cache'
    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    $archive = Join-Path $cache $GbeArchive
    if ((Test-Path -LiteralPath $archive) -and (Test-Sha256 $archive $GbeArchiveSha256)) {
        return $archive
    }

    if ($PSVersionTable.PSVersion.Major -lt 6) {
        # Windows PowerShell may not offer TLS 1.2, which GitHub requires.
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    }
    $url = "https://github.com/Detanup01/gbe_fork/releases/download/$GbeRelease/$GbeArchive"
    Write-Host "Downloading gbe_fork $GbeRelease from $url"
    Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
    if (-not (Test-Sha256 $archive $GbeArchiveSha256)) {
        Remove-Item -LiteralPath $archive -Force
        throw "The downloaded $GbeArchive does not match the pinned SHA-256. Nothing was installed."
    }
    return $archive
}

function Find-SevenZip {
    $candidates = @(
        (Get-Command 7z.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1),
        (Get-ItemProperty -Path 'HKLM:\SOFTWARE\7-Zip' -Name Path -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.Path '7z.exe' }),
        (Join-Path $env:ProgramFiles '7-Zip\7z.exe'),
        $(if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} '7-Zip\7z.exe' })
    )
    return $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}

function Expand-EmulatorDll {
    param([string]$Archive)

    $work = Join-Path $Root 'gbe-cache\extract'
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $entryWindows = $GbeDllEntry -replace '/', '\'

    $exitCode = 1
    $sevenZip = Find-SevenZip
    $windowsTar = Join-Path $env:SystemRoot 'System32\tar.exe'
    if ($sevenZip) {
        & $sevenZip x $Archive "-o$work" $entryWindows -y | Out-Null
        $exitCode = $LASTEXITCODE
    }
    elseif (Test-Path -LiteralPath $windowsTar) {
        # Windows' own tar.exe is libarchive. Windows 10 builds cannot read this
        # archive (no LZMA); 7-Zip always can.
        & $windowsTar -xf $Archive -C $work $GbeDllEntry
        $exitCode = $LASTEXITCODE
    }

    $dll = Join-Path $work $entryWindows
    if ($exitCode -ne 0 -or -not (Test-Path -LiteralPath $dll)) {
        throw "Could not extract $GbeDllEntry from $Archive. Install 7-Zip (https://www.7-zip.org), then run this script again."
    }
    if (-not (Test-Sha256 $dll $GbeDllSha256)) {
        throw 'The extracted steam_api64.dll does not match the pinned SHA-256. Nothing was installed.'
    }
    return $dll
}

function Invoke-Robocopy {
    param([string[]]$Arguments)
    & robocopy.exe @Arguments /COPY:DAT /R:2 /W:2 /NFL /NDL /NP /NJH /NJS | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed with exit code $LASTEXITCODE (robocopy $($Arguments -join ' '))."
    }
}

function Copy-GameData {
    param([string]$From, [string]$To)

    # Plugins holds steam_api64.dll. It must be a real copy so that replacing
    # the DLL never writes through a hard link into the source folder.
    $plugins = Join-Path $From 'Plugins'
    $pluginsPrefix = $plugins + '\'
    $isPlugin = { param($Path) $Path -eq $plugins -or $Path.StartsWith($pluginsPrefix, [StringComparison]::OrdinalIgnoreCase) }
    # A hard link shares its attributes with the source file, and a later
    # Remove-Item -Force clears them. Copy such files instead of linking them.
    $sharedAttributes = [IO.FileAttributes]::ReadOnly -bor [IO.FileAttributes]::Hidden -bor [IO.FileAttributes]::System

    New-Item -ItemType Directory -Force -Path $To | Out-Null
    foreach ($directory in (Get-ChildItem -LiteralPath $From -Recurse -Directory -Force)) {
        if (& $isPlugin $directory.FullName) { continue }
        New-Item -ItemType Directory -Force -Path (Join-Path $To $directory.FullName.Substring($From.Length + 1)) | Out-Null
    }

    $files = @(Get-ChildItem -LiteralPath $From -Recurse -File -Force | Where-Object { -not (& $isPlugin $_.FullName) })
    Write-Host "  Linking $($files.Count) files under Cairn_Data."
    foreach ($file in $files) {
        $target = Join-Path $To $file.FullName.Substring($From.Length + 1)
        if ($script:UseHardLinks -and -not ($file.Attributes -band $sharedAttributes)) {
            if ([CairnMP.HardLink]::CreateHardLink($target, $file.FullName, [IntPtr]::Zero)) {
                $script:LinkedFiles++
                continue
            }
            $win32Error = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            if ($script:LinkedFiles -eq 0) {
                # The very first link failed: this volume does not support them.
                $script:UseHardLinks = $false
                Write-Warning "Hard links are not available here (Win32 error $win32Error). Copying Cairn_Data instead, about 12 GB per client."
            }
        }
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }

    Invoke-Robocopy @($plugins, (Join-Path $To 'Plugins'), '/E')
}

function Write-EmulatorSettings {
    param([string]$PluginDir, [hashtable]$Client)

    $settings = Join-Path $PluginDir 'steam_settings'
    New-Item -ItemType Directory -Force -Path $settings | Out-Null
    Set-Content -LiteralPath (Join-Path $settings 'steam_appid.txt') -Value $CairnAppId -Encoding ascii -NoNewline

    # local_save_path is relative to the DLL: the client root, outside Cairn_Data,
    # so a later setup run keeps the emulator saves.
    @"
[user::general]
account_name=$($Client.Name)
account_steamid=$($Client.SteamId)

[user::saves]
local_save_path=../../../gse-saves
"@ | Set-Content -LiteralPath (Join-Path $settings 'configs.user.ini') -Encoding ascii

    # Both clients run on this PC: announce to loopback as well as the LAN.
    Set-Content -LiteralPath (Join-Path $settings 'custom_broadcasts.txt') -Value '127.0.0.1' -Encoding ascii
}

function Initialize-Client {
    param([hashtable]$Client, [string]$SourcePath, [string]$EmulatorDll)

    $destination = Join-Path $Root "client$($Client.Number)"
    $markerPath = Join-Path $destination $ClientMarker
    Write-Host "Client $($Client.Number) ($($Client.Name)): $destination"

    if (Test-Path -LiteralPath $destination) {
        if (-not (Test-Path -LiteralPath $markerPath)) {
            throw "$destination exists but was not made by this script. Move it away or pass another -Root."
        }
        Get-ChildItem -LiteralPath $destination -Force |
            Where-Object { $KeepEntries -notcontains $_.Name } |
            Remove-Item -Recurse -Force
    }
    else {
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
    }
    Set-Content -LiteralPath $markerPath -Value 'Made by scripts/setup-goldberg.ps1 in the CairnMP repository.' -Encoding ascii

    $excluded = @('Cairn_Data', 'Logs', 'MelonLoader\Logs')
    if (Test-Path -LiteralPath (Join-Path $destination 'UserData')) { $excluded += 'UserData' }
    $excludedPaths = @($excluded | ForEach-Object { Join-Path $SourcePath $_ })
    Invoke-Robocopy (@($SourcePath, $destination, '/E', '/XD') + $excludedPaths)

    Copy-GameData (Join-Path $SourcePath 'Cairn_Data') (Join-Path $destination 'Cairn_Data')

    $pluginDir = Join-Path $destination 'Cairn_Data\Plugins\x86_64'
    $clientDll = Join-Path $pluginDir 'steam_api64.dll'
    Remove-Item -LiteralPath $clientDll -Force
    Copy-Item -LiteralPath $EmulatorDll -Destination $clientDll
    Write-EmulatorSettings $pluginDir $Client
}

$libraries = Get-SteamLibraries
$manifest = $libraries |
    ForEach-Object { Join-Path $_ "steamapps\appmanifest_$CairnAppId.acf" } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
if (-not $manifest) {
    throw 'Cairn is not installed through Steam on this PC. This setup is for developers who own the game: install it from your Steam library first.'
}

$sourcePath = Resolve-CairnSource $Source $libraries
$sourceDll = Join-Path $sourcePath 'Cairn_Data\Plugins\x86_64\steam_api64.dll'
if (-not (Test-Path -LiteralPath $sourceDll)) {
    throw "steam_api64.dll was not found in $sourcePath\Cairn_Data\Plugins\x86_64."
}
if (Test-Sha256 $sourceDll $GbeDllSha256) {
    throw "$sourcePath already holds the emulator DLL. Pass -Source with an unmodified Steam copy of Cairn where CairnMP is installed."
}
if (-not (Test-Path -LiteralPath (Join-Path $sourcePath 'MelonLoader'))) {
    throw "MelonLoader is not installed in $sourcePath. Install CairnMP there first, then run this script again."
}

# Check the overlap before anything is created, so a -Root inside the game
# folder never leaves a trace there.
$Root = Get-FullPath $Root
$sourcePrefix = $sourcePath + '\'
$rootPrefix = $Root + '\'
if ($rootPrefix.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $sourcePrefix.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The source and -Root must be separate folders.'
}

if (Get-Process -Name 'Cairn' -ErrorAction SilentlyContinue) {
    throw 'Quit every Cairn window first. A running game locks its files and the copy would be incomplete.'
}

Initialize-Root $Root
Write-Host "Source: $sourcePath"
$emulatorDll = Expand-EmulatorDll (Get-VerifiedArchive)

if (-not ('CairnMP.HardLink' -as [type])) {
    # CreateHardLinkW takes literal paths. New-Item -ItemType HardLink treats
    # its target as a wildcard pattern in most PowerShell versions.
    Add-Type -Namespace CairnMP -Name HardLink -MemberDefinition @'
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
'@
}
$script:UseHardLinks = $true
$script:LinkedFiles = 0
foreach ($client in $Clients) {
    Initialize-Client $client $sourcePath $emulatorDll
}

# The source must still hold the real Steam DLL.
if (Test-Sha256 $sourceDll $GbeDllSha256) {
    throw "The emulator DLL reached $sourceDll. Verify the game files in Steam before you play."
}

Set-Content -LiteralPath (Join-Path $Root 'source.txt') -Value $sourcePath -Encoding utf8
Write-Host ''
Write-Host 'Both clients are ready. Start them with ./scripts/start-goldberg.ps1.'
Write-Host 'They share your Windows profile, so pick a different story save slot in each window.'
