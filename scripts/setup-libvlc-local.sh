#!/usr/bin/env bash
set -euo pipefail

project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
packages="$project_root/.cache/libvlc-debs"
runtime="$project_root/.cache/libvlc-root"
if ! command -v dpkg-deb >/dev/null 2>&1; then
    printf 'LibVLC setup needs dpkg-deb on PATH.\n' >&2
    exit 1
fi
mkdir -p "$packages" "$runtime"
for package in libvlc5 libvlccore9 vlc-plugin-base vlc-plugin-video-output vlc-data; do
    if ! compgen -G "$packages/${package}_*.deb" >/dev/null; then
        if ! command -v apt-get >/dev/null 2>&1; then
            printf 'LibVLC setup needs apt-get to download %s.\n' "$package" >&2
            exit 1
        fi
        (cd "$packages" && apt-get download "$package")
    fi
done
for package in "$packages"/*.deb; do
    dpkg-deb -x "$package" "$runtime"
done
library="$runtime/usr/lib/x86_64-linux-gnu"
if [[ ! -f "$library/libvlc.so.5" ||
      ! -f "$library/libvlccore.so.9" ||
      ! -f "$library/vlc/libvlc_xcb_events.so.0" ||
      ! -f "$library/vlc/plugins/codec/libavcodec_plugin.so" ||
      ! -f "$library/vlc/plugins/audio_output/libpulse_plugin.so" ||
      ! -f "$library/vlc/plugins/video_output/libxcb_x11_plugin.so" ]]; then
    printf 'LibVLC installation is incomplete.\n' >&2
    exit 1
fi
# The app selects software decoding and XCB output. VA-API OpenGL converters
# only trigger unsupported driver probes on the target NVIDIA/X11 setup.
video_plugins="$library/vlc/plugins/video_output"
rm -f "$video_plugins/libglconv_vaapi_x11_plugin.so" \
      "$video_plugins/libglconv_vaapi_drm_plugin.so" \
      "$video_plugins/libglconv_vaapi_wl_plugin.so"
ln -sfn libvlc.so.5 "$library/libvlc.so"
ln -sfn libvlccore.so.9 "$library/libvlccore.so"
touch "$runtime/.ready"
printf 'LibVLC is ready in %s\n' "$runtime"
