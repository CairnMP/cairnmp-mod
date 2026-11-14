#!/usr/bin/env bash
# Starts the two Goldberg copies of Cairn made by setup-goldberg.sh, with Proton.
#
# Copies the source game's Mods folder into each client first, so a deploy
# reaches both. Then starts each client in a small window that keeps simulating
# in the background. Each client has its own Proton prefix, so its own saves;
# the first start of a prefix takes about a minute. Steam does not need to be
# running. A client that is already running is left alone.
#
# Usage: bash scripts/start-goldberg.sh [1] [2]   (default: both)
#   PROTON                 Proton folder to use (default: the newest "Proton N.N"
#                          in your Steam libraries, else Proton - Experimental)
#   CAIRNMP_GOLDBERG_ROOT  the same root as setup-goldberg.sh
#   CAIRN_WIDTH, CAIRN_HEIGHT  window size (default 960x540)
set -euo pipefail
shopt -s inherit_errexit

APP_ID=1588550
ROOT=${CAIRNMP_GOLDBERG_ROOT:-${XDG_DATA_HOME:-$HOME/.local/share}/CairnMultiplayerData/goldberg}
ROOT=$(realpath -m -- "$ROOT")
WIDTH=${CAIRN_WIDTH:-960}
HEIGHT=${CAIRN_HEIGHT:-540}

die() { printf 'start-goldberg: %s\n' "$1" >&2; exit 1; }
# Wine maps / to Z:, so a Linux path becomes a Windows one the game understands.
wine_path() { local path=$1; printf 'Z:%s' "${path//\//\\}"; }

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

steam_roots() {
    local root
    for root in "$HOME/.local/share/Steam" "$HOME/.steam/steam" \
        "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam" \
        "$HOME/snap/steam/common/.local/share/Steam"; do
        if [[ -d $root/steamapps ]]; then realpath -e -- "$root"; fi
    done | awk '!seen[$0]++'
}

steam_libraries() {
    local root vdf path
    while IFS= read -r root; do
        printf '%s\n' "$root"
        vdf=$root/steamapps/libraryfolders.vdf
        if [[ ! -f $vdf ]]; then continue; fi
        while IFS= read -r path; do
            if [[ -d $path ]]; then realpath -e -- "$path"; fi
        done < <(sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$vdf")
    done < <(steam_roots) | awk '!seen[$0]++'
}

find_proton() {
    local library dir experimental=
    if [[ -n ${PROTON:-} ]]; then
        dir=${PROTON%/}
        dir=${dir%/proton}
        [[ -x $dir/proton ]] || die "PROTON=$PROTON does not contain a proton script."
        printf '%s\n' "$dir"
        return
    fi
    local versions=()
    while IFS= read -r library; do
        for dir in "$library"/steamapps/common/Proton*; do
            if [[ ! -x $dir/proton ]]; then continue; fi
            case ${dir##*/} in
                "Proton "[0-9]*) versions+=("${dir##*/}"$'\t'"$dir") ;;
                "Proton - Experimental") experimental=$dir ;;
            esac
        done
    done < <(steam_libraries)
    if ((${#versions[@]})); then
        printf '%s\n' "${versions[@]}" | sort -t $'\t' -k1,1V | tail -n 1 | cut -f 2
    elif [[ -n $experimental ]]; then
        printf '%s\n' "$experimental"
    else
        die "No Proton was found in your Steam libraries. Install one from Steam, or set PROTON."
    fi
}

start_client() {
    local number=$1 dest=$ROOT/client$1 proxy= candidate log prefix running
    [[ -f $dest/Cairn.exe ]] || die "Client $number is missing from $dest. Run bash scripts/setup-goldberg.sh first."

    running=$(cairn_processes)
    if grep -qixF -e "$dest/Cairn.exe" -e "$(wine_path "$dest")\\Cairn.exe" <<<"$running"; then
        printf 'Client %s is already running. Leaving it alone.\n' "$number"
        return
    fi

    if [[ -d $SOURCE_MODS ]]; then
        rm -rf -- "$dest/Mods"
        cp -a -- "$SOURCE_MODS" "$dest/Mods"
    fi

    for candidate in version winhttp winmm dinput8 dsound; do
        if [[ -f $dest/$candidate.dll ]]; then proxy=$candidate; break; fi
    done
    [[ -n $proxy ]] ||
        die "No MelonLoader proxy DLL (version.dll, winhttp.dll...) in $dest. Install CairnMP in the source folder and run setup again."

    # Let the first window bind the emulator's port before the second starts.
    if [[ -n ${STARTED_ONE:-} ]]; then sleep 3; fi
    STARTED_ONE=1

    prefix=$ROOT/prefix$number
    log=$dest/Logs/Player.log
    mkdir -p -- "$prefix" "$dest/Logs"
    printf 'Starting client %s from %s with %s. Unity log: %s\n' "$number" "$dest" "${PROTON_DIR##*/}" "$log"
    (
        cd -- "$dest"
        export STEAM_COMPAT_CLIENT_INSTALL_PATH=$STEAM_ROOT
        export STEAM_COMPAT_DATA_PATH=$prefix
        export WINEDLLOVERRIDES="$proxy=n,b${WINEDLLOVERRIDES:+;$WINEDLLOVERRIDES}"
        export SteamAppId=$APP_ID SteamGameId=$APP_ID
        export CAIRNMP_RUN_IN_BACKGROUND=1
        # Emulated Steam IDs must stay off the public lobby list. Nothing listens
        # on this port, so each heartbeat fails at once and is only logged.
        export CAIRNMP_API_URL=http://127.0.0.1:9
        nohup "$PROTON_DIR/proton" run "$dest/Cairn.exe" --melonloader.debug \
            -screen-fullscreen 0 -screen-width "$WIDTH" -screen-height "$HEIGHT" \
            -logFile "$(wine_path "$log")" >"$dest/Logs/proton.log" 2>&1 &
    )
}

[[ -f $ROOT/source.txt ]] || die "No Goldberg clients were found in $ROOT. Run bash scripts/setup-goldberg.sh first."
SOURCE_MODS=$(<"$ROOT/source.txt")/Mods
if [[ ! -d $SOURCE_MODS ]]; then
    printf 'start-goldberg: %s was not found. The clients start with the Mods they already have.\n' "$SOURCE_MODS" >&2
fi
STEAM_ROOT=$(steam_roots | head -n 1)
[[ -n $STEAM_ROOT ]] || die "Steam was not found. Proton needs the Steam client files, even when Steam is closed."
PROTON_DIR=$(find_proton)

clients=("$@")
if ((${#clients[@]} == 0)); then clients=(1 2); fi
for number in "${clients[@]}"; do
    case $number in
        1 | 2) start_client "$number" ;;
        *) die "Unknown client '$number'. Use 1, 2, or nothing for both." ;;
    esac
done

printf 'Host in one window and join from the other. Mute voice.\n'
