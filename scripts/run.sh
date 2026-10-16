#!/usr/bin/env bash
set -euo pipefail

project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
libvlc_root="$project_root/.cache/libvlc-root/usr/lib/x86_64-linux-gnu"
tools_root="$project_root/.cache/tools"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_CLI_HOME="$project_root/.cache/dotnet-home"
export NUGET_PACKAGES="$project_root/.cache/nuget"
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES"

# The pinned toolchain lives in this checkout. If a download is interrupted,
# keep this already-provisioned computer usable through compatible host tools.
if [[ "${CAPTION_EXPRESS_SKIP_LOCAL_SETUP:-0}" != 1 ]]; then
    if ! bash "$project_root/scripts/setup-host-tools-local.sh"; then
        printf 'Caption-Express could not prepare every private build/media tool. Checking host fallbacks.\n' >&2
    fi
fi

local_dotnet="$tools_root/dotnet-10.0.402/dotnet"
if [[ -f "$tools_root/dotnet-10.0.402/.ready" && -x "$local_dotnet" &&
      "$("$local_dotnet" --version 2>/dev/null)" == 10.0.402 ]]; then
    export DOTNET_ROOT="$(dirname "$local_dotnet")"
    export PATH="$DOTNET_ROOT:$PATH"
elif ! command -v dotnet >/dev/null 2>&1 ||
     [[ "$(dotnet --version 2>/dev/null)" != 10.* ]]; then
    printf 'Caption-Express needs .NET 10; private setup failed and no compatible host SDK was found.\n' >&2
    exit 1
fi

local_python="$tools_root/python-3.14.8/python/bin/python3"
if [[ -f "$tools_root/python-3.14.8/.ready" && -x "$local_python" &&
      "$("$local_python" --version 2>/dev/null)" == 'Python 3.14.8' ]]; then
    export PATH="$(dirname "$local_python"):$PATH"
    default_python="$local_python"
elif command -v python3 >/dev/null 2>&1; then
    default_python="$(command -v python3)"
else
    printf 'Caption-Express needs Python 3; private setup failed and no host Python was found.\n' >&2
    exit 1
fi
export CAPTION_EXPRESS_PYTHON="${CAPTION_EXPRESS_PYTHON:-$default_python}"
if ! "$CAPTION_EXPRESS_PYTHON" --version >/dev/null 2>&1; then
    printf 'Configured Caption-Express Python cannot run: %s\n' "$CAPTION_EXPRESS_PYTHON" >&2
    exit 1
fi

local_ffmpeg_bin="$tools_root/ffmpeg-8.1.3/bin"
if [[ -f "$tools_root/ffmpeg-8.1.3/.ready" &&
      -x "$local_ffmpeg_bin/ffmpeg" && -x "$local_ffmpeg_bin/ffprobe" &&
      "$("$local_ffmpeg_bin/ffmpeg" -version 2>/dev/null)" == *'ffmpeg version n8.1.3'* &&
      "$("$local_ffmpeg_bin/ffprobe" -version 2>/dev/null)" == *'ffprobe version n8.1.3'* ]]; then
    export PATH="$local_ffmpeg_bin:$PATH"
else
    for executable in ffmpeg ffprobe; do
        if ! command -v "$executable" >/dev/null 2>&1; then
            printf 'Caption-Express needs %s; private setup failed and no host copy was found.\n' "$executable" >&2
            exit 1
        fi
    done
fi

# Populate the private LibVLC runtime on a fresh checkout as well as repair an
# incomplete one. Merely checking for a missing plugin skipped first-time setup.
if [[ ! -f "$project_root/.cache/libvlc-root/.ready" ||
      ! -f "$libvlc_root/libvlc.so.5" ||
      ! -f "$libvlc_root/libvlccore.so.9" ||
      ! -f "$libvlc_root/vlc/libvlc_xcb_events.so.0" ||
      ! -f "$libvlc_root/vlc/plugins/codec/libavcodec_plugin.so" ||
      ! -f "$libvlc_root/vlc/plugins/audio_output/libpulse_plugin.so" ||
      ! -f "$libvlc_root/vlc/plugins/video_output/libxcb_x11_plugin.so" ]]; then
    if ! bash "$project_root/scripts/setup-libvlc-local.sh"; then
        printf 'Caption-Express could not prepare private LibVLC; playback may be unavailable until setup succeeds.\n' >&2
    fi
fi
if [[ -f "$libvlc_root/libvlc.so.5" ]]; then
    # Software decoding and XCB output do not use VA-API OpenGL converters.
    # Older local installs may still have these plugins, which probe unavailable
    # Intel/NVIDIA VA-API drivers and flood the terminal on this machine.
    video_plugins="$libvlc_root/vlc/plugins/video_output"
    rm -f "$video_plugins/libglconv_vaapi_x11_plugin.so" \
          "$video_plugins/libglconv_vaapi_drm_plugin.so" \
          "$video_plugins/libglconv_vaapi_wl_plugin.so"
    if [[ ! -f "$libvlc_root/libvlc.so" ]]; then
        ln -sfn libvlc.so.5 "$libvlc_root/libvlc.so"
    fi
    if [[ ! -f "$libvlc_root/libvlccore.so" ]]; then
        ln -sfn libvlccore.so.9 "$libvlc_root/libvlccore.so"
    fi
    export LD_LIBRARY_PATH="$libvlc_root:$libvlc_root/vlc${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
    export VLC_PLUGIN_PATH="$libvlc_root/vlc/plugins"
fi
project="$project_root/src/CaptionExpress/CaptionExpress.csproj"

# Restore on every launch. This is usually an up-to-date no-op, and it repairs
# a cleared NuGet cache that still has a stale project.assets.json file.
dotnet restore "$project"

# The pinned CUDA server is project owned. Its installer is idempotent; an
# unavailable network should still leave the editor usable for manual review.
if ! "$CAPTION_EXPRESS_PYTHON" "$project_root/scripts/setup-llama-local.py"; then
    printf 'Caption-Express could not prepare its model server; captioning may be unavailable until setup succeeds.\n' >&2
fi

exec dotnet run --configuration Release --project "$project" --no-restore "$@"
