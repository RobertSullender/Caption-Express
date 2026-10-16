#!/usr/bin/env python3
"""Caption one exported clip with a single local Gemma 4 video/audio model."""

from __future__ import annotations

import base64
from array import array
import ctypes
from dataclasses import dataclass
import json
import math
import os
import re
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import time
import traceback
import urllib.error
import urllib.request
import wave
from collections.abc import Callable
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TEMP_DIR = ROOT / ".cache/segments"
MODEL_ROOTS = (
    ROOT / "models",
    Path.home() / "ComfyUI/models/text_encoders",
    Path.home() / ".lmstudio/models",
)

USER_PROMPT = (
    "[[video: Caption the visible clip in one factual, flowing paragraph for a training pair. "
    "Describe the shot, setting, visible subjects, appearance, action, and camera movement "
    "in chronological order. Compare subject positions across frames; a fixed camera does "
    "not mean the subject is still. Describe only visible structures; do not infer an exact venue. "
    "A sampled section may be part of a longer clip; do not call its last frame the end "
    "of the whole clip. "
    "Do not transcribe signs, gauge readings, or numeric overlays; describe motion qualitatively. "
    "Omit uncertain details and use timestamps only when they help locate "
    "a specific change.]] "
    "[[soundtrack: Describe the audible sounds in this recording in one or two concise "
    "factual sentences. Include speech, music, and other distinct sounds only if audible. "
    "Do not infer the visual scene.]] "
    "[[audio_only: Describe the audible sounds in this recording in one or two concise "
    "factual sentences. Include speech, music, and other distinct sounds only if audible. "
    "Do not infer the visual scene.]] "
    "[[later: This excerpt begins later in the same exported clip. The previous section "
    "was: \"{previous_caption}\". State only new visible action or scene transitions "
    "in this excerpt. Do not restate the subject identity, setting, clothing, background, or "
    "unchanged camera position. If the same action continues, describe its progress in one "
    "brief sentence. Give approximate times only for meaningful changes. Do not call the "
    "end of this excerpt the end of the whole clip. Keep this section under 50 words. "
    "If there is no new detail, answer exactly \"No new event.\"]] "
    "[[later_audio: This is a later excerpt from the same recording. The preceding audio "
    "caption was: \"{previous_caption}\". Describe only a clearly new sound or an audible "
    "change. Do not infer a visual scene or repeat unchanged sounds. If there is no clear "
    "change, answer exactly \"No new audio event.\"]] "
    "[[refine: Write one natural, flowing caption from the observations below. Establish "
    "the shot and scene, then describe visible action and camera movement in chronological "
    "order. Add the soundtrack observations as independent sentences in the same paragraph. "
    "Keep distinct audible music, speech, and effects; quote speech only when its words are "
    "provided. An audio note means the sound was heard sometime within its broad window: "
    "its exact onset, duration, and alignment with visible actions are unknown. Never say "
    "a sound lasts throughout a clip or place it during a visual event unless the notes "
    "explicitly establish that timing. Do not attribute a sound to a visible person or "
    "object, or invent spatial proximity or a causal link, without explicit evidence. "
    "Resolve repetition, but do not add objects, actions, sounds, dialogue, identities, "
    "or events after the final observation. If visual notes conflict, omit the disputed "
    "detail rather than inventing a transition. Output only the caption, without headings "
    "or commentary.]]"
)

WINDOW_SECONDS = 8.0
AUDIO_WINDOW_SECONDS = 25.0
FRAME_RATE = 2
MAX_FRAMES = 16
FRAME_EDGE = 768
GUIDANCE_LIMIT = 4000
GUIDANCE_CUE_LIMIT = 64
GUIDANCE_CUE_TEXT_LIMIT = 500
_TIMECODE = r"(?:\d+:)?\d+:\d{2}(?:\.\d{1,3})?"
_GUIDANCE_RANGE = re.compile(
    rf"^\s*(?P<start>{_TIMECODE})\s*[-–—]\s*(?P<end>{_TIMECODE})"
    r"(?:\s*[:|–—-]\s*|\s+)(?P<text>\S.*?)\s*$")
_GUIDANCE_TIMESTAMP_PREFIX = re.compile(
    r"^\s*\d+(?::\d+){1,2}(?:\.\d+)?(?=$|[\s,.;:/|()\[\]–—-])")


@dataclass(frozen=True)
class GuidanceCue:
    start: float | None
    end: float | None
    text: str


def _parse_timecode(value: str) -> float:
    fields = value.split(":")
    seconds = float(fields[-1])
    minutes = int(fields[-2])
    if seconds >= 60 or (len(fields) == 3 and minutes >= 60):
        raise ValueError(f"Invalid guidance time: {value}")
    return (int(fields[0]) * 3600 if len(fields) == 3 else 0) + minutes * 60 + seconds


def parse_guidance(value: str | None) -> tuple[GuidanceCue, ...]:
    """Parse clip-relative user notes; reject malformed ranges instead of broadening them."""
    if value is None or value == "":
        return ()
    if not isinstance(value, str):
        raise ValueError("guidance_text must be text or null.")
    if len(value) > GUIDANCE_LIMIT:
        raise ValueError(f"Guidance is limited to {GUIDANCE_LIMIT} characters.")
    cues = []
    for line in value.splitlines():
        line = line.strip()
        if not line:
            continue
        # Pasted checklist notes commonly include a simple bullet prefix.
        line = re.sub(r"^[-*•]\s+", "", line)
        match = _GUIDANCE_RANGE.match(line)
        if match:
            start, end = _parse_timecode(match["start"]), _parse_timecode(match["end"])
            if end <= start:
                raise ValueError(f"Guidance range must end after it starts: {line[:80]}")
            text = match["text"].strip()
            cue = GuidanceCue(start, end, text)
        elif _GUIDANCE_TIMESTAMP_PREFIX.match(line):
            raise ValueError(
                "Start a timed guidance line with a range such as "
                f"00:00-00:05 followed by a description: {line[:80]}")
        else:
            cue = GuidanceCue(None, None, line)
        if len(cue.text) > GUIDANCE_CUE_TEXT_LIMIT:
            raise ValueError(f"Each guidance note is limited to {GUIDANCE_CUE_TEXT_LIMIT} characters.")
        cues.append(cue)
        if len(cues) > GUIDANCE_CUE_LIMIT:
            raise ValueError(f"Guidance is limited to {GUIDANCE_CUE_LIMIT} notes.")
    return tuple(cues)


def guidance_for_window(cues: tuple[GuidanceCue, ...], start: float,
                        end: float) -> tuple[GuidanceCue, ...]:
    return tuple(cue for cue in cues if cue.start is None or
                 (max(start, cue.start) < min(end, cue.end)))


def validate_guidance_duration(cues: tuple[GuidanceCue, ...], duration: float) -> None:
    for cue in cues:
        if cue.start is not None and cue.start >= duration:
            raise ValueError(
                f"Guidance starts at {cue.start:.2f}s, outside the {duration:.2f}s exported "
                "clip. Use times relative to the exported clip.")


def validate_guidance_request(request: dict) -> None:
    """Check notes against the planned export without starting an inference model."""
    duration = request.get("duration")
    if isinstance(duration, bool) or not isinstance(duration, (int, float)) or (
            not math.isfinite(duration) or duration <= 0):
        raise ValueError("duration must be a positive number of seconds.")
    cues = parse_guidance(request.get("guidance_text"))
    validate_guidance_duration(cues, float(duration))
    emit("complete", guidance_valid=True)


def guided_prompt(prompt: str, cues: tuple[GuidanceCue, ...], start: float,
                  end: float, mode: str) -> str:
    """Keep the ordinary prompt identical unless guidance applies to this window."""
    applicable = guidance_for_window(cues, start, end)
    if not applicable:
        return prompt
    lines = []
    for cue in applicable:
        if cue.start is None:
            label = "whole clip"
        else:
            label = f"{max(start, cue.start):.1f}–{min(end, cue.end):.1f}s"
        lines.append(f"[{label}] {cue.text}")
    if mode == "video":
        modality = ("Use the visual details as a human annotation, including details you may miss "
                    "from sparse frames. Ignore sound claims in this pass.")
    elif mode in ("soundtrack", "audio_only"):
        modality = ("Use the sound details as a human listening annotation, including quiet "
                    "details you may miss. Ignore visual claims in this pass.")
    elif mode == "refine":
        modality = (
            "Use these as human observations alongside the independent visual and audio "
            "observations. Account for every relevant user cue in this interval: include its "
            "subject, action, visible details, and named sound categories when they do not "
            "directly conflict with independent observations. Keep the relative sequence and "
            "time range supplied by the user; do not move an event to another interval or "
            "invent a more precise onset. A missing or less detailed model observation is "
            "not a contradiction, so retain subtle motion and sounds supplied by the user. "
            "Clear independent observations take priority over incompatible user claims: "
            "use the independently observed object identity, and discard user actions or "
            "sounds that depend on a contradicted identity. If independent audio identifies "
            "a different sound category, prefer that independently heard category."
        )
    else:
        raise ValueError(f"Unknown caption prompt mode: {mode}")
    return (prompt + "\n\nUser guidance for this clip interval (human annotations, "
            "not a finished caption):\n" + "\n".join(lines) +
            "\n" + modality + " If a note directly conflicts with the supplied media or "
            "observations, omit the disputed claim. Do not add details, timing, causes, "
            "or links that are absent from both the notes and media. Describe only this interval.")


def render_user_prompt(prompt: str, later: bool, mode: str = "video",
                       previous_caption: str = "") -> str:
    if mode not in ("video", "soundtrack", "audio_only", "refine"):
        raise ValueError(f"Unknown caption prompt mode: {mode}")
    text = prompt
    for tag, enabled in (("video", mode == "video"), ("soundtrack", mode == "soundtrack"),
                         ("audio_only", mode == "audio_only"),
                         ("later", later and mode == "video"),
                         ("later_audio", later and mode in ("soundtrack", "audio_only")),
                         ("refine", mode == "refine")):
        text = re.sub(rf"\[\[{tag}:\s*(.*?)\]\]",
                      lambda match: match.group(1).strip() if enabled else "",
                      text, flags=re.DOTALL)
    return text.replace("{previous_caption}", previous_caption).strip()


def emit(kind: str, **data: object) -> None:
    print(json.dumps({"type": kind, **data}, ensure_ascii=False), flush=True)


def model_key(path: Path, projector: bool = False) -> str:
    name = path.stem.casefold().replace("_", "-")
    if projector:
        name = re.sub(r"^mmproj-", "", name)
    return re.sub(r"-(?:i?q\d+(?:-[a-z0-9]+)*|bf16|f16|fp16|fp8(?:-scaled)?)$", "", name)


def is_gguf(path: Path) -> bool:
    try:
        with path.open("rb") as stream:
            return stream.read(4) == b"GGUF"
    except OSError:
        return False


def discover_models(roots: tuple[Path, ...] | None = None) -> list[dict[str, str]]:
    if roots is None:
        roots = MODEL_ROOTS
    files = [path for root in roots if root.is_dir()
             for path in root.rglob("*.gguf") if path.is_file()]
    projectors = {(path.parent, model_key(path, True)): path for path in files
                  if path.name.casefold().startswith("mmproj-") and is_gguf(path)}
    models = []
    for path in files:
        if path.name.casefold().startswith("mmproj-") or not is_gguf(path):
            continue
        key = model_key(path)
        family = re.search(r"gemma-?4-?e([24])b(?:-|$)", key)
        projector = projectors.get((path.parent, key))
        if family is None or projector is None:
            continue
        size = "E" + family.group(1) + "B"
        models.append({"id": str(path), "label": f"{size} · {path.stem}",
                       "family": size, "model_path": str(path),
                       "projector_path": str(projector)})
    def priority(model: dict[str, str]) -> tuple[int, str]:
        name = Path(model["id"]).stem.casefold()
        if model["family"] == "E4B" and name.startswith("gemma-4-e4b-it") and "hauhau" not in name:
            rank = 0
        elif model["family"] == "E4B" and "hauhaucs" in model["id"].casefold():
            rank = 1
        elif model["family"] == "E4B":
            rank = 2
        else:
            rank = 3
        return rank, model["label"].casefold()

    return sorted(models, key=priority)


def model_status() -> dict:
    models = discover_models()
    return {"model_present": bool(models), "models": models,
            "default_model_id": models[0]["id"] if models else None,
            "default_user_prompt": USER_PROMPT, **loaded_status()}


def probe_media(path: Path) -> tuple[float, bool, bool]:
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_format", "-show_streams", "-of", "json", str(path)],
        capture_output=True, text=True, check=True
    )
    info = json.loads(result.stdout)
    streams = info.get("streams", [])
    def valid_duration(value: object) -> float:
        try:
            seconds = float(value or 0)
            return seconds if math.isfinite(seconds) and seconds > 0 else 0
        except (TypeError, ValueError):
            return 0
    duration = valid_duration(info.get("format", {}).get("duration"))
    if duration <= 0:
        duration = max((valid_duration(stream.get("duration")) for stream in streams), default=0)
    video = any(stream.get("codec_type") == "video" and
                not stream.get("disposition", {}).get("attached_pic") for stream in streams)
    audio = any(stream.get("codec_type") == "audio" for stream in streams)
    if duration <= 0 or not (video or audio):
        raise ValueError("The exported clip has no readable video or audio duration.")
    return duration, video, audio


def run_ffmpeg(args: list[str]) -> None:
    result = subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-y", *args],
                            capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError("Could not prepare caption media: " + result.stderr.strip())


def frame_times(start: float, end: float, max_frames: int = MAX_FRAMES) -> list[float]:
    duration = end - start
    count = min(max_frames, max(1, math.ceil(duration * FRAME_RATE)))
    if count == 1:
        return [start]
    last = start + max(0.0, duration - 0.04)
    return [start + (last - start) * index / (count - 1) for index in range(count)]


def make_frame(source: Path, seconds: float, output: Path) -> float:
    # Seeking beyond the last frame can exit successfully without writing an image.
    for rewind in (0, 0.02, 0.1, 0.5, 1, 2, 4, 8):
        sampled_at = max(0.0, seconds - rewind)
        output.unlink(missing_ok=True)
        run_ffmpeg(["-ss", f"{sampled_at:.3f}", "-i", str(source), "-map", "0:V:0", "-an",
                    "-frames:v", "1", "-vf", f"scale={FRAME_EDGE}:{FRAME_EDGE}:force_original_aspect_ratio=decrease,format=yuvj420p",
                    "-q:v", "4", str(output)])
        if output.is_file() and output.stat().st_size:
            return sampled_at
        if sampled_at == 0:
            break
    raise RuntimeError(f"Could not decode a video frame near {seconds:.2f}s.")


def make_audio_window(source: Path, start: float, end: float, output: Path) -> None:
    args = ["-ss", f"{start:.3f}", "-i", str(source), "-t", f"{end - start:.3f}",
            "-vn", "-map", "0:a:0", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", str(output)]
    run_ffmpeg(args)


def wav_is_digital_silence(path: Path) -> bool:
    """Also skip codec residue far below a useful listening level."""
    with wave.open(str(path), "rb") as audio:
        if audio.getsampwidth() != 2:
            return False
        samples = array("h")
        samples.frombytes(audio.readframes(audio.getnframes()))
    if sys.byteorder == "big":
        samples.byteswap()
    if not samples:
        return True
    peak = max(abs(sample) for sample in samples)
    if peak == 0:
        return True
    # A real isolated click can have low RMS, so require both thresholds.
    rms = math.sqrt(sum(sample * sample for sample in samples) / len(samples))
    return rms < 32768 * 10 ** (-70 / 20) and peak < 32768 * 10 ** (-55 / 20)


def normalize_quiet_audio(path: Path) -> None:
    """Lift quiet inference audio without changing the exported soundtrack."""
    with wave.open(str(path), "rb") as audio:
        if audio.getsampwidth() != 2 or audio.getnchannels() != 1:
            return
        sample_rate = audio.getframerate()
        samples = array("h")
        samples.frombytes(audio.readframes(audio.getnframes()))
    if sys.byteorder == "big":
        samples.byteswap()
    if not samples:
        return
    rms = math.sqrt(sum(sample * sample for sample in samples) / len(samples))
    peak = max(abs(sample) for sample in samples)
    if rms < 32768 * 10 ** (-55 / 20) or peak == 0:
        return
    target_rms = 32768 * 10 ** (-18 / 20)
    target_peak = 32768 * 10 ** (-2 / 20)
    gain = min(target_rms / rms, target_peak / peak, 10)
    if gain <= 1.05:
        return
    scaled = array("h", (round(sample * gain) for sample in samples))
    if sys.byteorder == "big":
        scaled.byteswap()
    with wave.open(str(path), "wb") as audio:
        audio.setnchannels(1)
        audio.setsampwidth(2)
        audio.setframerate(sample_rate)
        audio.writeframes(scaled.tobytes())


def make_content(source: Path, start: float, end: float, directory: Path, prompt: str,
                 later: bool, has_video: bool, has_audio: bool,
                 max_frames: int = MAX_FRAMES, previous_caption: str = "",
                 prompt_mode: str | None = None,
                 guidance: tuple[GuidanceCue, ...] = ()) -> tuple[list[dict], bool]:
    audio_part = None
    if has_audio:
        audio = directory / "audio.wav"
        make_audio_window(source, start, end, audio)
        if not wav_is_digital_silence(audio):
            normalize_quiet_audio(audio)
            audio_part = {"type": "input_audio", "input_audio": {
                "data": base64.b64encode(audio.read_bytes()).decode("ascii"), "format": "wav"}}
    # Gemma 4's llama.cpp audio path can treat a text-first request as if no
    # recording was attached. Present the audio payload before instructions.
    content = [audio_part] if audio_part is not None else []
    if has_video:
        for index, seconds in enumerate(frame_times(start, end, max_frames)):
            frame = directory / f"frame-{index:02d}.jpg"
            sampled_at = make_frame(source, seconds, frame)
            content.append({"type": "image_url", "image_url": {
                "url": "data:image/jpeg;base64," + base64.b64encode(frame.read_bytes()).decode("ascii")}})
            content.append({"type": "text", "text": f"The preceding frame is near {sampled_at:.2f}s."})
    mode = prompt_mode or ("video" if has_video else "audio_only")
    content.append({"type": "text", "text": render_user_prompt(
        prompt, later, mode, previous_caption)})
    if guidance:
        content[-1]["text"] = guided_prompt(content[-1]["text"], guidance, start, end, mode)
    return content, audio_part is not None


LOCAL_LLAMA_RELEASE = "b11429"


def _library_dirs(executable: Path) -> tuple[Path, ...]:
    """Use libraries shipped with this executable, never another backend's CUDA libs."""
    directories = [executable.parent]
    sibling_lib = executable.parent.parent / "lib"
    if sibling_lib.is_dir():
        directories.append(sibling_lib)
    return tuple(directory for directory in directories if directory.is_dir())


def server_environment(library_dirs: tuple[Path, ...]) -> dict[str, str]:
    environment = os.environ.copy()
    vlc_root = (ROOT / ".cache/libvlc-root").resolve()
    inherited = [Path(part).expanduser() for part in environment.get("LD_LIBRARY_PATH", "").split(":")
                 if part]
    inherited = [path for path in inherited if not path.resolve().is_relative_to(vlc_root)]
    paths = dict.fromkeys([*library_dirs, *inherited])
    if paths:
        environment["LD_LIBRARY_PATH"] = ":".join(map(str, paths))
    else:
        environment.pop("LD_LIBRARY_PATH", None)
    return environment


def server_candidates() -> list[tuple[Path, tuple[Path, ...]]]:
    configured = os.environ.get("CAPTION_EXPRESS_LLAMA_SERVER")
    extra_libraries = tuple(Path(part).expanduser() for part in
                            os.environ.get("CAPTION_EXPRESS_LLAMA_LIB_DIR", "").split(":") if part)
    if configured:
        executable = Path(configured).expanduser()
        if not executable.is_file():
            raise FileNotFoundError(f"Configured llama-server does not exist: {executable}")
        libraries = (*extra_libraries, *_library_dirs(executable))
        local = ROOT / ".cache/llama.cpp" / LOCAL_LLAMA_RELEASE
        manifest = local / "manifest.json"
        if manifest.is_file() and executable.resolve().is_relative_to(local.resolve()):
            try:
                installed = json.loads(manifest.read_text())
                if (installed.get("release") == LOCAL_LLAMA_RELEASE and
                        executable.resolve() == (local / installed["server"]).resolve()):
                    libraries = (*extra_libraries,
                                 *(local / part for part in installed["lib_dirs"]),
                                 *_library_dirs(executable))
            except (OSError, ValueError, KeyError, TypeError):
                pass
        lm_backends = Path.home() / ".lmstudio/extensions/backends"
        if executable.parent.parent == lm_backends and "nvidia-cuda12" in executable.parent.name:
            vendor = lm_backends / "vendor/linux-llama-cuda12-vendor-v1"
            if vendor.is_dir():
                libraries = (*libraries, vendor)
        return [(executable, tuple(directory for directory in libraries if directory.is_dir()))]

    candidates: list[tuple[Path, tuple[Path, ...]]] = []
    local = ROOT / ".cache/llama.cpp" / LOCAL_LLAMA_RELEASE
    manifest = local / "manifest.json"
    if manifest.is_file():
        try:
            installed = json.loads(manifest.read_text())
            if installed.get("release") == LOCAL_LLAMA_RELEASE:
                executable = local / installed["server"]
                libraries = tuple(local / part for part in installed["lib_dirs"])
                local_root = local.resolve()
                if (executable.is_file() and executable.resolve().is_relative_to(local_root)
                        and libraries and all(path.is_dir() and path.resolve().is_relative_to(local_root)
                                          for path in libraries)):
                    candidates.append((executable, libraries))
        except (OSError, ValueError, KeyError, TypeError):
            pass

    backends = Path.home() / ".lmstudio/extensions/backends"
    def version(path: Path) -> tuple[int, ...]:
        match = re.search(r"-(\d+(?:\.\d+)+)$", path.parent.name)
        return tuple(map(int, match.group(1).split("."))) if match else ()
    lm_servers = sorted(backends.glob("llama.cpp-linux-x86_64-nvidia-cuda12-avx2-*/llama-server"),
                        key=version, reverse=True)
    vendor = backends / "vendor/linux-llama-cuda12-vendor-v1"
    for executable in lm_servers:
        libraries = (*_library_dirs(executable), *((vendor,) if vendor.is_dir() else ()))
        candidates.append((executable, libraries))

    on_path = shutil.which("llama-server")
    if on_path:
        executable = Path(on_path)
        if executable.is_file() and all(executable != candidate[0] for candidate in candidates):
            candidates.append((executable, _library_dirs(executable)))
    if not candidates:
        raise FileNotFoundError("llama-server is unavailable. Run scripts/setup-llama-local.py "
                                "or install a compatible LM Studio CUDA backend.")
    return candidates


def find_server() -> tuple[Path, tuple[Path, ...]]:
    """Return the preferred server; startup can try later candidates if it fails."""
    return server_candidates()[0]


class GemmaServer:
    def __init__(self, model: dict[str, str], thinking: bool) -> None:
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            port = listener.getsockname()[1]
        self.url = f"http://127.0.0.1:{port}"
        self.thinking = thinking
        log_dir = ROOT / ".cache/logs"
        log_dir.mkdir(parents=True, exist_ok=True)
        self.log_path = log_dir / "gemma-server.log"
        failures = []
        self.log_path.write_text("")
        parent_pid = os.getpid()

        def parent_death_guard() -> None:
            # A fatal worker exit must not strand a model in VRAM on Linux.
            if ctypes.CDLL(None).prctl(1, signal.SIGTERM) != 0 or os.getppid() != parent_pid:
                os._exit(127)

        for executable, library_dirs in server_candidates():
            environment = server_environment(library_dirs)
            args = [str(executable), "-m", model["model_path"], "-mm", model["projector_path"], "-ngl", "99",
                    "-c", "8192", "--host", "127.0.0.1", "--port", str(port),
                    "--parallel", "1", "--reasoning", "on" if thinking else "off", "--jinja"]
            try:
                with self.log_path.open("a") as log:
                    log.write(f"\n=== Starting {executable} ===\n")
                    log.flush()
                    self.process = subprocess.Popen(args, stdout=log, stderr=subprocess.STDOUT,
                                                    env=environment,
                                                    preexec_fn=parent_death_guard if sys.platform == "linux" else None)
                self._wait_ready()
                return
            except (OSError, RuntimeError, TimeoutError) as error:
                if hasattr(self, "process"):
                    self.stop()
                failures.append(f"{executable}: {error}")
        raise RuntimeError("No llama-server backend could load the model. See "
                           f"{self.log_path}. Attempts: {'; '.join(failures)}")

    def _wait_ready(self) -> None:
        deadline = time.monotonic() + 90
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                raise RuntimeError("Gemma server exited while loading. See " + str(self.log_path))
            try:
                with urllib.request.urlopen(self.url + "/health", timeout=1) as response:
                    if response.status == 200:
                        return
            except (urllib.error.URLError, TimeoutError):
                pass
            time.sleep(0.25)
        self.stop()
        raise TimeoutError("Gemma server did not become ready. See " + str(self.log_path))

    def infer(self, content: list[dict], system_prompt: str | None,
              variation_seed: int | None = None) -> str:
        messages = []
        if system_prompt:
            messages.append({"role": "system", "content": system_prompt})
        messages.append({"role": "user", "content": content})
        payload = {"model": "caption-gemma", "messages": messages,
                   "temperature": 0.1 if variation_seed is not None else 0,
                   "top_p": 0.95, "max_tokens": 1400 if self.thinking else 900,
                   "chat_template_kwargs": {"enable_thinking": self.thinking}, "stream": False}
        if variation_seed is not None:
            payload["seed"] = variation_seed
        if self.thinking:
            payload["thinking_budget_tokens"] = 256
        else:
            payload["reasoning_effort"] = "none"
        request = urllib.request.Request(
            self.url + "/v1/chat/completions", data=json.dumps(payload).encode(),
            headers={"Content-Type": "application/json"}
        )
        try:
            with urllib.request.urlopen(request, timeout=300) as response:
                result = json.load(response)
        except urllib.error.HTTPError as error:
            raise RuntimeError(f"Gemma request failed: {error.read().decode()[:1000]}") from error
        choice = result["choices"][0]
        if choice.get("finish_reason") == "length":
            raise RuntimeError("Gemma reached its output limit before finishing the caption.")
        answer = final_caption(choice["message"].get("content") or "")
        if not answer:
            raise RuntimeError("Gemma returned no final caption. Try disabling thinking or reducing the clip window.")
        return answer

    def stop(self) -> None:
        if self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)

    def healthy(self) -> bool:
        if self.process.poll() is not None:
            return False
        try:
            with urllib.request.urlopen(self.url + "/health", timeout=2) as response:
                return response.status == 200
        except (OSError, urllib.error.URLError, TimeoutError):
            return False


_loaded_server: GemmaServer | None = None
_loaded_model: dict[str, str] | None = None
_loaded_thinking: bool | None = None


def unload_model() -> None:
    """Release the cached llama-server and the GPU memory it owns."""
    global _loaded_server, _loaded_model, _loaded_thinking
    server = _loaded_server
    _loaded_server = None
    _loaded_model = None
    _loaded_thinking = None
    if server is not None:
        server.stop()


def loaded_status() -> dict[str, object]:
    if _loaded_server is not None and not _loaded_server.healthy():
        unload_model()
    return {"loaded_model_id": _loaded_model["id"] if _loaded_model else None,
            "loaded_model_label": _loaded_model["label"] if _loaded_model else None,
            "loaded_model_thinking": _loaded_thinking}


def get_server(model: dict[str, str], thinking: bool) -> GemmaServer:
    """Reuse a healthy server only for the same weights, projector, and thinking mode."""
    global _loaded_server, _loaded_model, _loaded_thinking
    key = (model["model_path"], model["projector_path"], thinking)
    loaded_key = ((_loaded_model["model_path"], _loaded_model["projector_path"],
                   _loaded_thinking) if _loaded_model else None)
    if _loaded_server is not None and key == loaded_key and _loaded_server.healthy():
        return _loaded_server
    unload_model()
    emit("progress", message=f"Loading {model['label']}…")
    server = GemmaServer(model, thinking)
    _loaded_server, _loaded_model, _loaded_thinking = server, model, thinking
    return server


def clean_caption(value: str) -> str:
    value = re.sub(r"\s+", " ", value.replace("**", "")).strip()
    value, removed = re.subn(r"(?i)^(?:the|this) (?:video|clip) (?:begins|starts) with\s+",
                             "", value, count=1)
    if removed and value:
        value = value[0].upper() + value[1:]
    return value


def final_caption(value: str) -> str:
    value = re.sub(r"(?s)<\|channel>thought.*?<channel\|>", "", value)
    value = re.sub(r"(?s)<think>.*?</think>", "", value)
    if "<|channel>thought" in value or "<think>" in value:
        raise RuntimeError("Gemma produced thinking without a final caption. Try disabling thinking.")
    # Some GGUF/template combinations place a self-check after the final answer
    # without wrapping it in a thought channel. Keep only the preceding caption.
    value = re.split(r"(?i)\b\d+\.\s+(?:final review|generate output)\b", value, maxsplit=1)[0]
    return clean_caption(value)


_AUDIO_CLAUSE_BREAK = re.compile(
    r"\s*;\s*|\s*,?\s+(?:but|yet|however)\s+|,\s*(?:only|just)\s+",
    flags=re.IGNORECASE)
_AUDIO_ABSENCE_FOLLOWUP = re.compile(
    r"(?i)^(?:the (?:recording|audio|soundtrack) (?:is|was|sounds?) "
    r"(?:silent|inaudible)|(?:it|everything) (?:is|was) (?:silent|inaudible)|"
    r"nothing (?:is )?audible|(?:i|we) (?:can|could) only guess)\b")
_AUDIO_REFUSAL_FOLLOWUP = re.compile(r"(?i)^(?:i|we) (?:can|could) only guess\b")


def _keep_audio_clauses(
        sentence: str, rejects: Callable[[str], re.Match[str] | None],
        discard_with_rejection: Callable[[str], re.Match[str] | None] | None = None
) -> tuple[str, bool]:
    """Remove a negative claim while retaining separate, affirmative sound clauses."""
    # Splitting only at contrasts avoids turning "no speech, music, or effects"
    # into positive claims about music or effects.
    clauses = _AUDIO_CLAUSE_BREAK.split(sentence)
    if not any(rejects(clause.strip()) for clause in clauses if clause.strip()):
        return sentence, False
    kept = []
    rejected = False
    for clause in clauses:
        clause = clause.strip()
        if not clause:
            continue
        if rejects(clause):
            rejected = True
            continue
        if discard_with_rejection and discard_with_rejection(clause):
            continue
        clause = clause[0].upper() + clause[1:]
        kept.append(clause)
    if not kept:
        return "", rejected
    for index in range(len(kept) - 1):
        if kept[index][-1] not in ".!?":
            kept[index] += "."
    if sentence.endswith((".", "!", "?")) and kept[-1][-1] not in ".!?":
        kept[-1] += sentence[-1]
    return " ".join(kept), rejected


def remove_audio_absence_claims(value: str) -> str:
    """Keep heard sounds; absence lists add little and can contradict faint audio."""
    sentences = re.split(r"(?<=[.!?])\s+", value)
    absence = re.compile(r"(?i)^(?:there (?:are|is) no|no (?:other|discernible|human|transient|"
                         r"background|speech|music|vocal|audible)|nothing (?:else|more))\b")
    def rejects_absence(clause: str) -> re.Match[str] | None:
        return absence.match(clause) or _AUDIO_ABSENCE_FOLLOWUP.match(clause)
    retained = []
    for sentence in sentences:
        if sentence.strip():
            cleaned, _ = _keep_audio_clauses(sentence.strip(), rejects_absence)
            if cleaned:
                retained.append(cleaned)
    return " ".join(retained).strip()


def remove_audio_input_refusal(value: str) -> tuple[str, bool]:
    """Drop claims that attached audio is missing, while flagging that pass."""
    refusal = re.compile(
        r"(?i)\b(?:no|without|missing)\s+(?:audio|recording|soundtrack)(?:\s+file)?\b|"
        r"\b(?:audio|recording|soundtrack)(?:\s+file)?\b.{0,70}\bnot\s+"
        r"(?:provided|attached|available|uploaded|supplied)\b|"
        r"\b(?:upload|provide|attach|send)\b.{0,60}\b(?:audio|recording|soundtrack|file)\b|"
        r"\b(?:i|we)\s+(?:need|require|cannot|can't|do not have|don't have)\b"
        r".{0,100}\b(?:audio|recording|soundtrack|sounds?)\b")
    retained = []
    rejected = False
    for sentence in re.split(r"(?<=[.!?])\s+", value):
        if sentence.strip():
            cleaned, clause_rejected = _keep_audio_clauses(
                sentence.strip(), refusal.search, _AUDIO_REFUSAL_FOLLOWUP.match)
            rejected |= clause_rejected
            if cleaned:
                retained.append(cleaned)
    return " ".join(retained).strip(), rejected


def qualify_section_end(value: str, is_final: bool) -> str:
    """A model viewing one window cannot declare that the entire export ends there."""
    if is_final:
        return value
    return re.sub(r"\b(end|remainder) of (?:the )?clip\b",
                  lambda match: f"{match.group(1)} of this section", value,
                  flags=re.IGNORECASE)


def refine_observations(server: GemmaServer, system_prompt: str | None,
                        user_prompt: str,
                        visual_notes: list[tuple[float, float, str]],
                        audio_notes: list[tuple[float, float, str]],
                        audio_ranges: list[tuple[float, float]],
                        variation_seed: int | None = None,
                        guidance: tuple[GuidanceCue, ...] = ()) -> str:
    """Merge bounded, time-labeled evidence without asking Gemma to hear audio."""
    instruction = render_user_prompt(user_prompt, False, "refine")
    if not instruction:
        raise ValueError("Enter refinement instructions or turn off Refine combined caption.")
    refined = []
    for index, (start, end) in enumerate(audio_ranges):
        visuals = [f"[{begin:.1f}–{finish:.1f}s] {note}" for begin, finish, note
                   in visual_notes if start <= (begin + finish) / 2 < end or
                   (index == len(audio_ranges) - 1 and (begin + finish) / 2 == end)]
        sounds = [f"[{begin:.1f}–{finish:.1f}s] {note}" for begin, finish, note
                  in audio_notes if start <= begin < end]
        if not visuals and not sounds and not guidance_for_window(guidance, start, end):
            continue
        previous = ("\nPrevious refined passage, for continuity only: " + refined[-1][-700:]
                    if refined else "")
        evidence = (f"Clip interval: {start:.1f}–{end:.1f}s.\n"
                    "Visual observations from sampled frames:\n" +
                    ("\n".join(visuals) if visuals else "No new visual observation.") +
                    "\nSounds heard at some point in each broad audio window; exact timing "
                    "within the window is unknown:\n" +
                    ("\n".join(sounds) if sounds else "No verified sound observation.") +
                    previous)
        prompt = instruction + "\n\n" + evidence
        if guidance:
            prompt = guided_prompt(prompt, guidance, start, end, "refine")
        if index:
            prompt += "\nContinue the same clip. State only progress or changes; avoid repeating the setup."
        # Keep first runs deterministic, but give rerun refinement a sampling
        # stream distinct from the video and audio passes.
        refine_seed = None if variation_seed is None else (
            variation_seed + 200_000 + index - 1) % 2_147_483_647 + 1
        paragraph = server.infer([{"type": "text", "text": prompt}],
                                 system_prompt, refine_seed)
        refined.append(paragraph)
    return " ".join(refined).strip()


def _caption_impl(request: dict) -> None:
    source = Path(request["path"]).resolve()
    if not source.is_file():
        raise FileNotFoundError(source)
    duration, has_video, has_audio = probe_media(source)
    models = discover_models()
    selected_id = request.get("model_id") or (models[0]["id"] if models else None)
    model = next((candidate for candidate in models if candidate["id"] == selected_id), None)
    if model is None:
        raise RuntimeError("Select an installed Gemma 4 E2B/E4B GGUF with its matching mmproj projector.")
    window = float(request.get("window_seconds", WINDOW_SECONDS))
    if not 1 <= window <= 10:
        raise ValueError("Caption windows must be 1–10 seconds.")
    system_prompt = (request.get("system_prompt") or "").strip() or None
    thinking = request.get("enable_thinking", True)
    if not isinstance(thinking, bool):
        raise ValueError("enable_thinking must be true or false.")
    refine_caption = request.get("refine_caption", False)
    if not isinstance(refine_caption, bool):
        raise ValueError("refine_caption must be true or false.")
    variation_seed = request.get("variation_seed")
    if variation_seed is not None and (type(variation_seed) is not int or
                                       not 1 <= variation_seed <= 2_147_483_647):
        raise ValueError("variation_seed must be a positive 31-bit integer.")

    def section_seed(index: int, audio: bool = False) -> int | None:
        if variation_seed is None:
            return None
        # Different sections get distinct sampling streams while rerunning the
        # same request with the same seed remains reproducible.
        offset = index + (100_000 if audio else 0)
        return (variation_seed + offset - 1) % 2_147_483_647 + 1
    guidance = parse_guidance(request.get("guidance_text"))
    validate_guidance_duration(guidance, duration)
    guided_refinement = bool(guidance) and has_video and has_audio
    user_prompt = request.get("user_prompt", USER_PROMPT)
    if not isinstance(user_prompt, str) or any(not render_user_prompt(user_prompt, False, mode)
            for mode in ((["video"] if has_video else []) +
                         (["soundtrack" if has_video else "audio_only"] if has_audio else []) +
                         (["refine"] if refine_caption and has_video and has_audio else []))):
        raise ValueError("Enter a caption user prompt.")
    # An annotation calls for a final text synthesis even when the optional
    # unguided refinement switch is off. A custom prompt without a refine
    # section can still use the project's default synthesis instruction.
    refinement_prompt = (user_prompt if render_user_prompt(user_prompt, False, "refine")
                         else USER_PROMPT)

    def ranges(seconds: float) -> list[tuple[float, float]]:
        sections = []
        start = 0.0
        while start < duration - 0.001:
            end = min(duration, start + seconds)
            sections.append((start, end))
            start = end
        return sections

    video_ranges = ranges(window) if has_video else []
    audio_ranges = ranges(AUDIO_WINDOW_SECONDS) if has_audio else []
    TEMP_DIR.mkdir(parents=True, exist_ok=True)
    visual_sections = []
    audio_sections = []
    visual_notes: list[tuple[float, float, str]] = []
    audio_notes: list[tuple[float, float, str]] = []
    audio_refusal_windows: list[tuple[float, float]] = []
    audio_empty_windows: list[tuple[float, float]] = []
    audio_inferred = False
    raw_result = ""
    refined_result = ""
    refinement_error = None
    refinement_skipped_reason = None
    with tempfile.TemporaryDirectory(prefix="caption-", dir=TEMP_DIR) as directory:
        temporary = Path(directory)
        server = get_server(model, thinking)
        total = len(video_ranges) + len(audio_ranges)
        completed = 0
        for index, (start, end) in enumerate(video_ranges):
            emit("progress", message=f"Inspecting video section {index + 1}/{len(video_ranges)}…")
            with tempfile.TemporaryDirectory(prefix=f"video-{index}-", dir=temporary) as window_dir:
                content, _ = make_content(source, start, end, Path(window_dir), user_prompt,
                                          index > 0, True, False,
                                          previous_caption=visual_sections[-1] if visual_sections else "",
                                          guidance=guidance if not has_audio else ())
                try:
                    paragraph = server.infer(content, system_prompt, section_seed(index))
                except RuntimeError as error:
                    if "memory" not in str(error).lower() and "context" not in str(error).lower():
                        raise
                    emit("progress", message="Retrying with fewer video frames…")
                    unload_model()
                    server = get_server(model, thinking)
                    content, _ = make_content(source, start, end, Path(window_dir), user_prompt,
                                              index > 0, True, False, 8,
                                              visual_sections[-1] if visual_sections else "",
                                              guidance=guidance if not has_audio else ())
                    paragraph = server.infer(content, system_prompt, section_seed(index))
            paragraph = qualify_section_end(paragraph, index == len(video_ranges) - 1)
            if paragraph and paragraph.casefold().rstrip(".") != "no new event":
                visual_sections.append(paragraph)
                visual_notes.append((start, end, paragraph))
            completed += 1
            emit("section", index=completed - 1, total=total)

        for index, (start, end) in enumerate(audio_ranges):
            emit("progress", message=f"Listening to soundtrack section {index + 1}/{len(audio_ranges)}…")
            with tempfile.TemporaryDirectory(prefix=f"audio-{index}-", dir=temporary) as window_dir:
                content, audio_attached = make_content(
                    source, start, end, Path(window_dir), user_prompt, index > 0,
                    False, True,
                    previous_caption=audio_sections[-1] if audio_sections else "",
                    prompt_mode="soundtrack" if has_video else "audio_only",
                    guidance=guidance if not has_video else ())
                if audio_attached:
                    audio_inferred = True
                    answer = server.infer(content, system_prompt,
                                          section_seed(index, audio=True))
                    answer, refused_audio = remove_audio_input_refusal(answer)
                    if refused_audio:
                        audio_refusal_windows.append((start, end))
                    paragraph = remove_audio_absence_claims(answer)
                    if (not paragraph.strip() or paragraph.strip().casefold().rstrip(".") ==
                            "no discernible sound") and not refused_audio:
                        audio_empty_windows.append((start, end))
                else:
                    paragraph = "" if has_video else "No discernible sound."
            if paragraph and paragraph.casefold().rstrip(".") not in (
                    "no new event", "no new audio event", "no discernible sound"):
                normalized = re.sub(r"\W+", " ", paragraph.casefold()).strip()
                previous = (re.sub(r"\W+", " ", audio_sections[-1].casefold()).strip()
                            if audio_sections else None)
                if normalized != previous:
                    audio_sections.append(paragraph)
                    audio_notes.append((start, end, paragraph))
            completed += 1
            emit("section", index=completed - 1, total=total)

        raw_result = " ".join(visual_sections + audio_sections).strip()
        if (refine_caption or guided_refinement) and has_video and has_audio and (
                visual_notes or audio_notes or guidance):
            # A failed refinement leaves the initial visual+audio draft available.
            if not audio_notes and not guided_refinement:
                refinement_skipped_reason = ("No usable soundtrack observations were available; "
                                             "refinement was skipped. Listen and add missing sounds if needed.")
                emit("progress", message=refinement_skipped_reason)
            else:
                try:
                    emit("progress", message="Refining visual and audio observations…")
                    refined = refine_observations(server, system_prompt, refinement_prompt,
                                                  visual_notes, audio_notes, audio_ranges,
                                                  variation_seed, guidance)
                    if refined:
                        refined_result = refined
                except (OSError, RuntimeError, TimeoutError, ValueError) as error:
                    refinement_error = str(error)
                    emit("progress", message=f"Refinement unavailable; keeping initial draft: {error}")
    result = refined_result or raw_result
    if not result and has_audio and not has_video:
        if audio_inferred:
            raise RuntimeError("Gemma did not produce a usable audio caption. Try another draft or model.")
        result = "No discernible sound."
    if not result:
        raise RuntimeError("The selected Gemma model returned no caption text.")
    emit("complete", caption=result, initial_caption=raw_result,
         refinement_applied=bool(refined_result), refinement_error=refinement_error,
         refinement_skipped_reason=refinement_skipped_reason,
         audio_warning=("Gemma returned no usable description of attached non-silent audio at " +
                        ", ".join(f"{start:.1f}–{end:.1f}s" for start, end in
                                  dict.fromkeys(audio_refusal_windows + audio_empty_windows)) +
                        ". Listen and add the missing sounds before review."
                        if audio_refusal_windows or audio_empty_windows else None),
         **loaded_status())


def caption(request: dict) -> None:
    try:
        _caption_impl(request)
    except BaseException:
        unload_model()
        raise


def main() -> None:
    try:
        emit("ready", **model_status())
        for line in sys.stdin:
            command = None
            try:
                request = json.loads(line)
                command = request.get("command")
                if command == "status":
                    emit("status", **model_status())
                elif command == "caption":
                    caption(request)
                elif command == "unload_model":
                    unload_model()
                    emit("status", **model_status())
                elif command == "validate_guidance":
                    validate_guidance_request(request)
                elif command == "shutdown":
                    break
                else:
                    raise ValueError(f"Unknown command: {command}")
            except Exception as error:
                if command == "caption":
                    unload_model()
                traceback.print_exc(file=sys.stderr)
                emit("error", message=str(error), error_type=type(error).__name__)
    finally:
        unload_model()


if __name__ == "__main__":
    if sys.platform == "linux":
        # A sudden UI exit must also terminate this worker. The server has the
        # same parent-death guard, so the chain releases VRAM even on a crash.
        parent_pid = os.getppid()
        if ctypes.CDLL(None).prctl(1, signal.SIGTERM) != 0 or os.getppid() != parent_pid:
            os._exit(127)
    main()
