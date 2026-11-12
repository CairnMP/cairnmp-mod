#!/usr/bin/env bash
# Makes two Goldberg (gbe_fork) copies of Cairn for same-PC multiplayer tests
# on Linux with Proton.
#
# For CairnMP developers who own Cairn on Steam. Copies the modded game folder
# twice and replaces steam_api64.dll in each copy with the gbe_fork Steam
# emulator. Each copy gets its own emulated Steam name and ID, and the two find
# each other over 127.0.0.1. One purchase is enough, and Steam does not need to
# be running.
#
# The script stops unless Cairn is installed through Steam on this PC. It never
# changes the source folder. Cairn_Data is hard-linked when the filesystem
# allows it, so the copies take little extra space.
#
# Run it again after a game or MelonLoader update. Each copy keeps its UserData,
# Logs, emulator saves and Proton prefix. Start both windows with
# scripts/start-goldberg.sh.
#
# Usage: bash scripts/setup-goldberg.sh [folder where CairnMP is installed]
#   CAIRN_DIR              source folder when no argument is given
#   CAIRNMP_GOLDBERG_ROOT  where the clients go
#                          (default: ~/.local/share/CairnMultiplayerData/goldberg)
set -euo pipefail
shopt -s inherit_errexit

# gbe_fork is the maintained Goldberg fork. The original Goldberg stops at
# Steamworks SDK 1.57 and lacks SteamInternal_SteamAPI_Init, which the mod calls.
# Proton runs the Windows build, so this is the Windows archive.
# Change these together, here and in setup-goldberg.ps1.
GBE_RELEASE=release-2026_09_27
GBE_ARCHIVE=emu-win-release-vs22.7z
GBE_ARCHIVE_SHA256=300186c34f49a9ea75b33ce2af299267cc1dad0225cf45e81a229578d4e11c85
GBE_DLL_ENTRY=release/regular/x64/steam_api64.dll
GBE_DLL_SHA256=70d9826a402d3a78b0c07e9f7ea7c6832468a1113013f31ea570fccaa5445ea9

APP_ID=1588550
ROOT_MARKER=.cairnmp-goldberg-root
MARKER=.cairnmp-goldberg
ROOT=${CAIRNMP_GOLDBERG_ROOT:-${XDG_DATA_HOME:-$HOME/.local/share}/CairnMultiplayerData/goldberg}
CLIENT_NAMES=(CairnDev1 CairnDev2)
CLIENT_STEAMIDS=(76561199548815729 76561199548815730)

die() { printf 'setup-goldberg: %s\n' "$1" >&2; exit 1; }
sha256_is() { [[ $(sha256sum -- "$1" | cut -d' ' -f1) == "$2" ]]; }

# Prints the argv[0] of every running Cairn.exe. Wine puts the Windows path
# (Z:\...\Cairn.exe) there; matching argv[0] alone skips shells and editors
# that merely mention the name.
cairn_processes() {
    local proc argv0
    for proc in /proc/[0-9]*; do
        argv0=
        IFS= read -r -d '' argv0 2>/dev/null <"$proc/cmdline" || true
        case ${argv0,,} in *cairn.exe) printf '%s\n' "$argv0" ;; esac
    done
}

steam_libraries() {
    local root vdf path
    for root in "$HOME/.local/share/Steam" "$HOME/.steam/steam" \
        "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam" \
        "$HOME/snap/steam/common/.local/share/Steam"; do
        if [[ ! -d $root/steamapps ]]; then continue; fi
        realpath -e -- "$root"
        vdf=$root/steamapps/libraryfolders.vdf
        if [[ ! -f $vdf ]]; then continue; fi
        while IFS= read -r path; do
            if [[ -d $path ]]; then realpath -e -- "$path"; fi
        done < <(sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$vdf")
    done | awk '!seen[$0]++'
}

resolve_source() {
    local candidate
    if [[ -n ${1:-} ]]; then
        [[ -f $1/Cairn.exe ]] || die "Cairn.exe was not found in $1."
        realpath -e -- "$1"
        return
    fi
    for candidate in "${CAIRN_DIR:-}" "${LIBRARIES[@]/%//steamapps/common/Cairn}"; do
        if [[ -n $candidate && -f $candidate/Cairn.exe ]]; then
            realpath -e -- "$candidate"
            return
        fi
    done
    die "Cairn.exe was not found. Pass the folder where CairnMP is installed."
}

verified_archive() {
    local downloads=$ROOT/gbe-cache
    local archive=$downloads/$GBE_ARCHIVE
    local url=https://github.com/Detanup01/gbe_fork/releases/download/$GBE_RELEASE/$GBE_ARCHIVE
    mkdir -p -- "$downloads"
    if [[ -f $archive ]] && sha256_is "$archive" "$GBE_ARCHIVE_SHA256"; then
        printf '%s\n' "$archive"
        return
    fi
    printf 'Downloading gbe_fork %s from %s\n' "$GBE_RELEASE" "$url" >&2
    curl -fL --proto '=https' --retry 2 -o "$archive.part" -- "$url"
    if ! sha256_is "$archive.part" "$GBE_ARCHIVE_SHA256"; then
        rm -f -- "$archive.part"
        die "The downloaded $GBE_ARCHIVE does not match the pinned SHA-256. Nothing was installed."
    fi
    mv -f -- "$archive.part" "$archive"
    printf '%s\n' "$archive"
}

extract_dll() {
    local archive=$1 work=$ROOT/gbe-cache/extract tool
    rm -rf -- "$work"
    mkdir -p -- "$work"
    if command -v bsdtar >/dev/null; then
        bsdtar -xf "$archive" -C "$work" "$GBE_DLL_ENTRY"
    else
        for tool in 7z 7zz 7za; do
            if command -v "$tool" >/dev/null; then break; fi
            tool=
        done
        [[ -n $tool ]] || die "Install bsdtar (libarchive) or 7-Zip to unpack $GBE_ARCHIVE."
        "$tool" x "$archive" "-o$work" "$GBE_DLL_ENTRY" -y >/dev/null
    fi
    [[ -f $work/$GBE_DLL_ENTRY ]] || die "Could not extract $GBE_DLL_ENTRY from $archive."
    sha256_is "$work/$GBE_DLL_ENTRY" "$GBE_DLL_SHA256" ||
        die "The extracted steam_api64.dll does not match the pinned SHA-256. Nothing was installed."
    printf '%s\n' "$work/$GBE_DLL_ENTRY"
}

write_settings() {
    local settings=$1/steam_settings name=$2 steamid=$3
    mkdir -p -- "$settings"
    printf '%s' "$APP_ID" >"$settings/steam_appid.txt"
    # local_save_path is relative to the DLL: the client root, outside Cairn_Data,
    # so a later setup run keeps the emulator saves.
    cat >"$settings/configs.user.ini" <<EOF
[user::general]
account_name=$name
account_steamid=$steamid

[user::saves]
local_save_path=../../../gse-saves
EOF
    # Both clients run on this PC: announce to loopback as well as the LAN.
    printf '127.0.0.1\n' >"$settings/custom_broadcasts.txt"
}

build_client() {
    local number=$1 name=$2 steamid=$3 emulator_dll=$4
    local dest=$ROOT/client$number entry base
    printf 'Client %s (%s): %s\n' "$number" "$name" "$dest"

    if [[ -e $dest ]]; then
        [[ -f $dest/$MARKER ]] ||
            die "$dest exists but was not made by this script. Move it away or set CAIRNMP_GOLDBERG_ROOT."
        for entry in "$dest"/*; do
            case ${entry##*/} in
                UserData | Logs | gse-saves | "$MARKER") ;;
                *) rm -rf -- "$entry" ;;
            esac
        done
    fi
    mkdir -p -- "$dest"
    printf 'Made by scripts/setup-goldberg.sh in the CairnMP repository.\n' >"$dest/$MARKER"

    for entry in "$SOURCE"/*; do
        base=${entry##*/}
        case $base in
            Cairn_Data | Logs) continue ;;
            UserData) if [[ -d $dest/UserData ]]; then continue; fi ;;
        esac
        cp -a -- "$entry" "$dest/"
    done
    rm -rf -- "$dest/MelonLoader/Logs"

    if [[ $USE_HARDLINKS == 1 ]] && cp -al -- "$SOURCE/Cairn_Data" "$dest/Cairn_Data" 2>/dev/null; then
        :
    else
        if [[ $USE_HARDLINKS == 1 ]]; then
            printf 'Hard links are not available here. Copying Cairn_Data instead, about 12 GB per client.\n' >&2
            USE_HARDLINKS=0
        fi
        rm -rf -- "$dest/Cairn_Data"
        cp -a -- "$SOURCE/Cairn_Data" "$dest/Cairn_Data"
    fi

    # Plugins holds steam_api64.dll. It must be a real copy so that replacing
    # the DLL never writes through a hard link into the source folder.
    rm -rf -- "$dest/Cairn_Data/Plugins"
    cp -a -- "$SOURCE/Cairn_Data/Plugins" "$dest/Cairn_Data/Plugins"
    rm -f -- "$dest/Cairn_Data/Plugins/x86_64/steam_api64.dll"
    cp -- "$emulator_dll" "$dest/Cairn_Data/Plugins/x86_64/steam_api64.dll"
    write_settings "$dest/Cairn_Data/Plugins/x86_64" "$name" "$steamid"
}

shopt -s dotglob nullglob

mapfile -t LIBRARIES < <(steam_libraries)
owned=
for library in "${LIBRARIES[@]}"; do
    if [[ -f $library/steamapps/appmanifest_$APP_ID.acf ]]; then owned=1; break; fi
done
[[ -n $owned ]] ||
    die "Cairn is not installed through Steam on this PC. This setup is for developers who own the game: install it from your Steam library first."

SOURCE=$(resolve_source "${1:-}")
SOURCE_DLL=$SOURCE/Cairn_Data/Plugins/x86_64/steam_api64.dll
[[ -f $SOURCE_DLL ]] || die "steam_api64.dll was not found in $SOURCE/Cairn_Data/Plugins/x86_64."
if sha256_is "$SOURCE_DLL" "$GBE_DLL_SHA256"; then
    die "$SOURCE already holds the emulator DLL. Pass an unmodified Steam copy of Cairn where CairnMP is installed."
fi
[[ -d $SOURCE/MelonLoader ]] ||
    die "MelonLoader is not installed in $SOURCE. Install CairnMP there first, then run this script again."

# Check the overlap before anything is created, so a root inside the game
# folder never leaves a trace there.
ROOT=$(realpath -m -- "$ROOT")
case "$ROOT/" in "$SOURCE/"*) die "The clients cannot go inside the source folder." ;; esac
case "$SOURCE/" in "$ROOT/"*) die "The source cannot be inside $ROOT." ;; esac

if [[ -n $(cairn_processes) ]]; then
    die "Quit every Cairn window first. A running game locks its files and the copy would be incomplete."
fi

if [[ -d $ROOT && ! -f $ROOT/$ROOT_MARKER && -n $(ls -A -- "$ROOT") ]]; then
    die "$ROOT already holds other files. Set CAIRNMP_GOLDBERG_ROOT to a new or empty folder."
fi
mkdir -p -- "$ROOT"
printf 'Made by scripts/setup-goldberg.sh in the CairnMP repository.\n' >"$ROOT/$ROOT_MARKER"

printf 'Source: %s\n' "$SOURCE"
ARCHIVE=$(verified_archive)
EMULATOR_DLL=$(extract_dll "$ARCHIVE")

USE_HARDLINKS=1
for index in 0 1; do
    build_client $((index + 1)) "${CLIENT_NAMES[index]}" "${CLIENT_STEAMIDS[index]}" "$EMULATOR_DLL"
done

# The source must still hold the real Steam DLL.
if sha256_is "$SOURCE_DLL" "$GBE_DLL_SHA256"; then
    die "The emulator DLL reached $SOURCE_DLL. Verify the game files in Steam before you play."
fi

printf '%s\n' "$SOURCE" >"$ROOT/source.txt"
printf '\nBoth clients are ready. Start them with: bash scripts/start-goldberg.sh\n'
