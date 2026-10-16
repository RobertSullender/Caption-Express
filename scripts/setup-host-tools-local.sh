#!/usr/bin/env bash
# Prepare the build and media tools in this checkout, without apt or sudo.
set -euo pipefail

project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tools_root="$project_root/.cache/tools"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_CLI_HOME="$project_root/.cache/dotnet-home"
export NUGET_PACKAGES="$project_root/.cache/nuget"
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES"
dotnet_dir="$tools_root/dotnet-10.0.402"
python_dir="$tools_root/python-3.14.8"
ffmpeg_dir="$tools_root/ffmpeg-8.1.3"

# .NET's SHA512 comes from Microsoft's 10.0 release metadata. The other SHA256
# values are the immutable GitHub release asset digests from the publishers.
# Keep these names, URLs and hashes together when intentionally upgrading.
dotnet_url='https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.402/dotnet-sdk-10.0.402-linux-x64.tar.gz'
dotnet_sha512='1904a98dda2db34ba78ba3c4fb1b224455329af91c341d83547843c1839e112b04ae699ee92fa2449a8f62249196a938723078c0c60b9f65c88539d5687729c2'
python_url='https://github.com/astral-sh/python-build-standalone/releases/download/20261013/cpython-3.14.8%2B20261013-x86_64-unknown-linux-gnu-install_only_stripped.tar.gz'
python_sha256='d8faa1b588506b9e3e5a3f57807bb056d534a1641a6b10d15b6ea3f0b413b960'
ffmpeg_url='https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-14-13-03/ffmpeg-n8.1.3-18-g46d899d7a0-linux64-gpl-8.1.tar.xz'
ffmpeg_sha256='c14ffffa2f3f908fcd9b557a6dca5f048435abed58b2d69bd9d9dc39006deb48'

mode="${1:-install}"
if [[ $# -gt 1 ]] ||
   [[ "$mode" != install && "$mode" != --dry-run && "$mode" != --check ]]; then
    printf 'Usage: %s [--dry-run|--check]\n' "${BASH_SOURCE[0]}" >&2
    exit 2
fi

if [[ "$(uname -s)" != Linux || "$(uname -m)" != x86_64 ]]; then
    printf 'Caption-Express local tools are pinned for Ubuntu/Linux x86_64.\n' >&2
    exit 1
fi

dotnet_ready() {
    [[ -f "$dotnet_dir/.ready" && -x "$dotnet_dir/dotnet" ]] &&
        [[ "$("$dotnet_dir/dotnet" --version 2>/dev/null)" == 10.0.402 ]]
}

python_ready() {
    [[ -f "$python_dir/.ready" && -x "$python_dir/python/bin/python3" ]] &&
        [[ "$("$python_dir/python/bin/python3" --version 2>/dev/null)" == 'Python 3.14.8' ]]
}

ffmpeg_ready() {
    [[ -f "$ffmpeg_dir/.ready" && -x "$ffmpeg_dir/bin/ffmpeg" &&
       -x "$ffmpeg_dir/bin/ffprobe" ]] &&
        [[ "$("$ffmpeg_dir/bin/ffmpeg" -version 2>/dev/null)" == *'ffmpeg version n8.1.3'* ]] &&
        [[ "$("$ffmpeg_dir/bin/ffprobe" -version 2>/dev/null)" == *'ffprobe version n8.1.3'* ]]
}

if [[ "$mode" == --dry-run || "$mode" == --check ]]; then
    missing=0
    for tool in dotnet python ffmpeg; do
        if "${tool}_ready"; then
            case "$tool" in
                dotnet) location="$dotnet_dir" ;;
                python) location="$python_dir" ;;
                ffmpeg) location="$ffmpeg_dir" ;;
            esac
            printf '%s: ready in %s\n' "$tool" "$location"
        else
            printf '%s: local install needed\n' "$tool"
            ((missing += 1))
        fi
    done
    [[ "$mode" == --dry-run || "$missing" -eq 0 ]]
    exit $?
fi

for command in tar sha256sum sha512sum mktemp realpath find; do
    if ! command -v "$command" >/dev/null 2>&1; then
        printf 'Local tool setup needs %s from the base Ubuntu system.\n' "$command" >&2
        exit 1
    fi
done
if ! command -v curl >/dev/null 2>&1 && ! command -v wget >/dev/null 2>&1; then
    printf 'Local tool setup needs curl or wget to download the verified archives.\n' >&2
    exit 1
fi
mkdir -p "$tools_root"

download_verified() {
    local url="$1" expected="$2" algorithm="$3" output="$4"
    printf 'Downloading %s\n' "$url"
    if command -v curl >/dev/null 2>&1; then
        curl --fail --location --show-error --silent --retry 3 \
            --connect-timeout 20 --max-time 1800 --output "$output" "$url"
    else
        wget --quiet --tries=3 --timeout=30 --output-document="$output" "$url"
    fi
    if ! printf '%s  %s\n' "$expected" "$output" | "${algorithm}sum" --check --status; then
        printf 'Checksum mismatch for %s. No tool was installed.\n' "$url" >&2
        return 1
    fi
    printf 'Verified %s archive.\n' "$algorithm"
}

validate_archive() {
    # This is a defense in depth check before GNU tar sees any archive members.
    # The pinned publisher digest is also verified before this function runs.
    local archive="$1" stage="$2" member listing type link target resolved
    local root_check='/caption-express-extraction-root'
    local -a members=() listings=()
    tar -tf "$archive" > "$stage/archive-members"
    tar -tvf "$archive" > "$stage/archive-listing"
    mapfile -t members < "$stage/archive-members"
    mapfile -t listings < "$stage/archive-listing"
    if [[ "${#members[@]}" -ne "${#listings[@]}" ]]; then
        printf 'Archive member listing is inconsistent; refusing extraction.\n' >&2
        return 1
    fi
    for i in "${!members[@]}"; do
        member="${members[i]}"
        listing="${listings[i]}"
        type="${listing:0:1}"
        case "$member" in
            ''|/*|..|../*|*/..|*/../*)
                printf 'Unsafe archive member path: %s\n' "$member" >&2
                return 1 ;;
        esac
        case "$type" in
            -|d) ;;
            l)
                if [[ "$listing" != *' -> '* ]]; then
                    printf 'Unreadable symlink in archive: %s\n' "$member" >&2
                    return 1
                fi
                link="${listing##* -> }"
                if [[ "$link" == /* ]]; then
                    printf 'Absolute archive symlink: %s\n' "$member" >&2
                    return 1
                fi
                target="$root_check/$(dirname "$member")/$link"
                resolved="$(realpath --canonicalize-missing --no-symlinks "$target")"
                if [[ "$resolved" != "$root_check" && "$resolved" != "$root_check/"* ]]; then
                    printf 'Archive symlink leaves install directory: %s\n' "$member" >&2
                    return 1
                fi ;;
            h)
                if [[ "$listing" != *' link to '* ]]; then
                    printf 'Unreadable hard link in archive: %s\n' "$member" >&2
                    return 1
                fi
                link="${listing##* link to }"
                if [[ "$link" == /* ]]; then
                    printf 'Absolute archive hard link: %s\n' "$member" >&2
                    return 1
                fi
                resolved="$(realpath --canonicalize-missing --no-symlinks "$root_check/$link")"
                if [[ "$resolved" != "$root_check" && "$resolved" != "$root_check/"* ]]; then
                    printf 'Archive hard link leaves install directory: %s\n' "$member" >&2
                    return 1
                fi ;;
            *)
                printf 'Unsupported archive member type %s: %s\n' "$type" "$member" >&2
                return 1 ;;
        esac
    done
    rm -f "$stage/archive-members" "$stage/archive-listing"
}

install_dotnet() {
    dotnet_ready && return 0
    local stage archive
    stage="$(mktemp -d "$tools_root/.dotnet-10.0.402.XXXXXX")"
    archive="$stage/archive.tar.gz"
    if ! (download_verified "$dotnet_url" "$dotnet_sha512" sha512 "$archive" &&
          validate_archive "$archive" "$stage" &&
          mkdir "$stage/files" &&
          tar --no-same-owner --no-same-permissions -xzf "$archive" -C "$stage/files" &&
          [[ "$("$stage/files/dotnet" --version)" == 10.0.402 ]]); then
        rm -rf "$stage"
        printf 'Could not prepare local .NET SDK 10.0.402.\n' >&2
        return 1
    fi
    rm -f "$archive"
    touch "$stage/files/.ready"
    if [[ -L "$dotnet_dir" ]]; then
        rm -rf "$stage"
        printf 'Refusing to replace symlinked local .NET directory.\n' >&2
        return 1
    fi
    rm -rf "$dotnet_dir"
    mv "$stage/files" "$dotnet_dir"
    rmdir "$stage"
    printf 'Installed project-local .NET SDK 10.0.402.\n'
}

install_python() {
    python_ready && return 0
    local stage archive
    stage="$(mktemp -d "$tools_root/.python-3.14.8.XXXXXX")"
    archive="$stage/archive.tar.gz"
    if ! (download_verified "$python_url" "$python_sha256" sha256 "$archive" &&
          validate_archive "$archive" "$stage" &&
          mkdir "$stage/files" &&
          tar --no-same-owner --no-same-permissions -xzf "$archive" -C "$stage/files" &&
          [[ "$("$stage/files/python/bin/python3" --version)" == 'Python 3.14.8' ]]); then
        rm -rf "$stage"
        printf 'Could not prepare local Python 3.14.8.\n' >&2
        return 1
    fi
    rm -f "$archive"
    touch "$stage/files/.ready"
    if [[ -L "$python_dir" ]]; then
        rm -rf "$stage"
        printf 'Refusing to replace symlinked local Python directory.\n' >&2
        return 1
    fi
    rm -rf "$python_dir"
    mv "$stage/files" "$python_dir"
    rmdir "$stage"
    printf 'Installed project-local Python 3.14.8.\n'
}

install_ffmpeg() {
    ffmpeg_ready && return 0
    local stage archive source_bin
    stage="$(mktemp -d "$tools_root/.ffmpeg-8.1.3.XXXXXX")"
    archive="$stage/archive.tar.xz"
    if ! (download_verified "$ffmpeg_url" "$ffmpeg_sha256" sha256 "$archive" &&
          validate_archive "$archive" "$stage" &&
          mkdir "$stage/files" &&
          tar --no-same-owner --no-same-permissions -xJf "$archive" -C "$stage/files"); then
        rm -rf "$stage"
        printf 'Could not download or extract local FFmpeg.\n' >&2
        return 1
    fi
    rm -f "$archive"
    source_bin="$(find "$stage/files" -path '*/bin/ffmpeg' -type f -print -quit)"
    if [[ -z "$source_bin" || ! -x "$source_bin" || ! -x "$(dirname "$source_bin")/ffprobe" ]]; then
        rm -rf "$stage"
        printf 'The verified FFmpeg archive lacks ffmpeg or ffprobe.\n' >&2
        return 1
    fi
    mkdir "$stage/files/bin"
    ln -s "$(realpath --relative-to="$stage/files/bin" "$source_bin")" "$stage/files/bin/ffmpeg"
    ln -s "$(realpath --relative-to="$stage/files/bin" "$(dirname "$source_bin")/ffprobe")" "$stage/files/bin/ffprobe"
    if [[ "$("$stage/files/bin/ffmpeg" -version 2>/dev/null)" != *'ffmpeg version n8.1.3'* ||
          "$("$stage/files/bin/ffprobe" -version 2>/dev/null)" != *'ffprobe version n8.1.3'* ]]; then
        rm -rf "$stage"
        printf 'Local FFmpeg failed its version check.\n' >&2
        return 1
    fi
    touch "$stage/files/.ready"
    if [[ -L "$ffmpeg_dir" ]]; then
        rm -rf "$stage"
        printf 'Refusing to replace symlinked local FFmpeg directory.\n' >&2
        return 1
    fi
    rm -rf "$ffmpeg_dir"
    mv "$stage/files" "$ffmpeg_dir"
    rmdir "$stage"
    printf 'Installed project-local FFmpeg and FFprobe 8.1.3.\n'
}

install_dotnet
install_python
install_ffmpeg
