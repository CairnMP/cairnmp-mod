<#
.SYNOPSIS
    Copies the local Cairn install to a second folder for same-PC multiplayer tests.

.DESCRIPTION
    Client 1 stays where it is. Client 2 is a full copy so loader logs, mod logs,
    and session files do not collide. Sets the user environment variable
    CAIRN_DIR_2 so a DeployMod build updates both Mods folders.

    The second Steam account needs its own purchased copy of Cairn. This script
    does not start Steam or the game.

.EXAMPLE
    ./scripts/clone-cairn.ps1
#>
[CmdletBinding()]
param(
    [string]$Source,
    [string]$Destination = 'C:\Users\Public\CairnMP\instance2',
    [string]$WindowsUser = 'cairnmp2'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Resolve-CairnSource {
    param([string]$Requested)

    if ($Requested) {
        if (-not (Test-Path -LiteralPath (Join-Path $Requested 'Cairn.exe'))) {
            throw "Cairn.exe was not found in $Requested."
        }
        return (Resolve-Path -LiteralPath $Requested).Path
    }

    $candidates = @(
        $env:CAIRN_DIR,
        (Join-Path $env:LOCALAPPDATA 'CairnMultiplayerData\game'),
        "${env:ProgramFiles(x86)}\Steam\steamapps\common\Cairn"
    ) | Where-Object { $_ }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'Cairn.exe')) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw 'Cairn.exe was not found. Pass -Source with the game folder.'
}

if ($WindowsUser -notmatch '^[A-Za-z0-9._-]+$') {
    throw 'Windows user name must be letters, digits, dot, underscore, or hyphen.'
}

$sourcePath = Resolve-CairnSource $Source
$destinationParent = Split-Path -Parent $Destination
if (-not $destinationParent) { throw "Destination '$Destination' has no parent folder." }
New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null

$destinationFull = [System.IO.Path]::GetFullPath($Destination)
$sourcePrefix = $sourcePath.TrimEnd('\') + '\'
$destinationPrefix = $destinationFull.TrimEnd('\') + '\'
if ($destinationPrefix.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $sourcePrefix.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Source and destination must be separate folders. A copy inside the game folder would recurse.'
}

if (Get-Process -Name 'Cairn' -ErrorAction SilentlyContinue) {
    throw 'Quit Cairn before cloning. A running game locks files, and the copy would be incomplete.'
}

Write-Host "Copying about 12 GB from $sourcePath to $destinationFull. This takes several minutes."
& robocopy.exe $sourcePath $destinationFull /E /XO /COPY:DAT /R:2 /W:2 /NFL /NDL /NP
if ($LASTEXITCODE -ge 8) {
    throw "robocopy failed with exit code $LASTEXITCODE."
}

$account = Get-LocalUser -Name $WindowsUser -ErrorAction SilentlyContinue
if ($account) {
    # (OI)(CI) must stay a literal ACE. Double quotes would treat (OI) as a subexpression.
    $ace = '{0}:(OI)(CI)M' -f $WindowsUser
    & icacls.exe $destinationFull /grant $ace /T /C | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Could not grant $WindowsUser write access to $destinationFull (icacls exit $LASTEXITCODE)."
    }
    else {
        Write-Host "Granted $WindowsUser write access to $destinationFull."
    }
}
else {
    Write-Warning @"
Windows user '$WindowsUser' does not exist yet. Create a local account with a password
(Settings, Accounts, Other users, no Microsoft account), then run this script again
so that user can write logs. The copy itself is done.
"@
}

[Environment]::SetEnvironmentVariable('CAIRN_DIR_2', $destinationFull, 'User')
Write-Host "CAIRN_DIR_2 is set for your user to $destinationFull."
Write-Host "Open a new terminal before the next deploy so it sees CAIRN_DIR_2."
Write-Host "The second Steam account still needs its own purchased copy of Cairn."
