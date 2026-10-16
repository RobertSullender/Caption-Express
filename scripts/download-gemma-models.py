#!/usr/bin/env python3
"""Download the four supported Gemma 4 GGUF/projector pairs into this project.

Only explicitly selected models are downloaded. A pair becomes visible under
``models/`` after both files match the sizes and SHA256 digests published by
their Hugging Face repositories. Interrupted transfers remain in the ignored
``.cache/model-downloads`` directory and resume on the next invocation.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from typing import Callable


ROOT = Path(__file__).resolve().parents[1]
CHUNK_BYTES = 4 * 1024 * 1024
PROGRESS_BYTES = 32 * 1024 * 1024
PROGRESS_SECONDS = 0.5
SPACE_MARGIN_BYTES = 64 * 1024 * 1024


@dataclass(frozen=True)
class FileSpec:
    name: str
    size: int
    sha256: str


@dataclass(frozen=True)
class ModelSpec:
    model_id: str
    label: str
    repository: str
    revision: str
    weight: FileSpec
    projector: FileSpec

    @property
    def files(self) -> tuple[FileSpec, FileSpec]:
        return self.weight, self.projector


# Full revisions and LFS SHA256/byte sizes from each repository's public API.
# Keep the weight and its own projector pinned to the same repository revision.
MODELS: dict[str, ModelSpec] = {
    "stock-e2b": ModelSpec(
        "stock-e2b", "Stock Gemma 4 E2B · Q8_0",
        "lmstudio-community/gemma-4-E2B-it-GGUF",
        "eed445ccfb8aef2e279df1a97e133baf06e443b4",
        FileSpec("gemma-4-E2B-it-Q8_0.gguf", 4967497152,
                 "1265ad3b888ca0b8b312c4f51358514fd49d4f7ad2f608f141ea9596b641d576"),
        FileSpec("mmproj-gemma-4-E2B-it-BF16.gguf", 986833248,
                 "04125f8e2a1135824e48064c32157356704e6681a9f3af669af321a3c70c39d7"),
    ),
    "stock-e4b": ModelSpec(
        "stock-e4b", "Stock Gemma 4 E4B · Q8_0",
        "lmstudio-community/gemma-4-E4B-it-GGUF",
        "99210a71094c6aa8559f764e0cadedd7d89de94d",
        FileSpec("gemma-4-E4B-it-Q8_0.gguf", 8031240000,
                 "6f6904e2925d5389ed51390d6c731f752914025fd2d7ad7df9d45f9c24164397"),
        FileSpec("mmproj-gemma-4-E4B-it-BF16.gguf", 991551840,
                 "bdfc4935857658dfdc8d1adebfa3897c2fec8b3323410f4f73ade36d6efa02be"),
    ),
    "uncensored-e2b": ModelSpec(
        "uncensored-e2b", "HauhauCS Gemma 4 E2B · Q8_K_P",
        "HauhauCS/Gemma-4-E2B-Uncensored-HauhauCS-Aggressive",
        "da8593c3e407afcd3e7da94ff2d69d77e2a28a48",
        FileSpec("Gemma-4-E2B-Uncensored-HauhauCS-Aggressive-Q8_K_P.gguf", 5036057536,
                 "dfee358c38cc41419dd538d9afe2e22245738c752a2e6b5adb9a6654cf30d67b"),
        FileSpec("mmproj-Gemma-4-E2B-Uncensored-HauhauCS-Aggressive-f16.gguf", 985570240,
                 "628b7e999f89beef70b32396ae84f59c096e867747d7901f0134064ff672e290"),
    ),
    "uncensored-e4b": ModelSpec(
        "uncensored-e4b", "HauhauCS Gemma 4 E4B · Q8_K_P",
        "HauhauCS/Gemma-4-E4B-Uncensored-HauhauCS-Aggressive",
        "45b6a334b4bcd1d7f37179df58b3b1d66a184e5d",
        FileSpec("Gemma-4-E4B-Uncensored-HauhauCS-Aggressive-Q8_K_P.gguf", 8133226464,
                 "a4c4177f9fd7e3f56522675afb742f079a53f9226195b7db5e9888c872f053da"),
        FileSpec("mmproj-Gemma-4-E4B-Uncensored-HauhauCS-Aggressive-f16.gguf", 990288832,
                 "debad39ab9c1152ab67695a674fb35e8375b2320c57bfd5075835d3ccb16c7db"),
    ),
}

Progress = Callable[[dict[str, object]], None]


class DownloadPaused(Exception):
    """A user-requested SIGINT/SIGTERM pause, with partial bytes retained."""


def _signal_pause(_number: int, _frame: object) -> None:
    raise DownloadPaused("Download paused; run the same model again to resume")


def _emit(progress: Progress, event: str, model: str, **details: object) -> None:
    progress({"event": event, "model": model, **details})


def _reject_symlink(path: Path) -> None:
    if path.is_symlink():
        raise RuntimeError(f"Refusing symlinked model download path: {path}")


def _verify(path: Path, file: FileSpec, progress: Progress, model: str) -> bool:
    if not path.is_file() or path.is_symlink() or path.stat().st_size != file.size:
        return False
    _emit(progress, "verify", model, file=file.name, bytes=file.size, total=file.size)
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(CHUNK_BYTES):
            digest.update(chunk)
    return digest.hexdigest() == file.sha256


def _preflight_space(directory: Path, file: FileSpec, current_bytes: int) -> None:
    remaining = file.size - current_bytes
    free = shutil.disk_usage(directory).free
    needed = remaining + SPACE_MARGIN_BYTES
    if free < needed:
        raise OSError(
            f"Not enough free space for {file.name}: need at least {needed:,} "
            f"bytes, have {free:,} bytes at {directory}"
        )


def _download_file(
    model: ModelSpec, file: FileSpec, pending: Path, progress: Progress,
) -> Path:
    completed = pending / file.name
    partial = pending / (file.name + ".part")
    for path in (pending, completed, partial):
        _reject_symlink(path)
    if _verify(completed, file, progress, model.model_id):
        return completed
    if completed.exists():
        completed.unlink()
    if partial.exists():
        size = partial.stat().st_size
        if size == file.size and _verify(partial, file, progress, model.model_id):
            partial.replace(completed)
            return completed
        if size >= file.size:
            partial.unlink()

    offset = partial.stat().st_size if partial.exists() else 0
    _preflight_space(pending, file, offset)
    _emit(progress, "start", model.model_id, file=file.name, bytes=offset,
          total=file.size, resumed=bool(offset))
    url = (
        "https://huggingface.co/"
        + urllib.parse.quote(model.repository, safe="/")
        + "/resolve/" + model.revision + "/"
        + urllib.parse.quote(file.name, safe="")
    )
    headers = {"User-Agent": "Caption-Express/1.0", "Accept-Encoding": "identity"}
    if offset:
        headers["Range"] = f"bytes={offset}-"
    request = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(request, timeout=90) as response:
        status = response.status
        if status not in (200, 206):
            raise RuntimeError(f"Unexpected HTTP {status} for {file.name}")
        encoding = response.headers.get("Content-Encoding", "identity")
        if encoding.lower() != "identity":
            raise RuntimeError(f"Unexpected HTTP encoding for {file.name}: {encoding}")
        if status == 206:
            expected_start = offset
            content_range = response.headers.get("Content-Range", "")
            if not content_range.startswith(f"bytes {expected_start}-"):
                raise RuntimeError(
                    f"Incorrect resume range for {file.name}: {content_range or '(missing)'}"
                )
        elif offset:
            # Some mirrors ignore Range. Their 200 response contains the whole
            # file, so replace the partial instead of appending duplicate bytes.
            offset = 0
        mode = "ab" if offset else "wb"
        written = offset
        last_bytes = offset
        last_time = time.monotonic()
        with partial.open(mode) as target:
            while chunk := response.read(CHUNK_BYTES):
                if written + len(chunk) > file.size:
                    raise RuntimeError(f"Received more than {file.size:,} bytes for {file.name}")
                target.write(chunk)
                written += len(chunk)
                now = time.monotonic()
                if written - last_bytes >= PROGRESS_BYTES or now - last_time >= PROGRESS_SECONDS:
                    _emit(progress, "progress", model.model_id, file=file.name,
                          bytes=written, total=file.size)
                    last_bytes, last_time = written, now
    if written != file.size:
        raise RuntimeError(
            f"Incomplete download for {file.name}: got {written:,} of {file.size:,} bytes; "
            "retry to resume"
        )
    _emit(progress, "progress", model.model_id, file=file.name,
          bytes=written, total=file.size)
    if not _verify(partial, file, progress, model.model_id):
        partial.unlink(missing_ok=True)
        raise RuntimeError(f"SHA256 mismatch for {file.name}; corrupt bytes were removed")
    partial.replace(completed)
    return completed


def _installed(model: ModelSpec, destination: Path, progress: Progress) -> bool:
    return all(_verify(destination / file.name, file, progress, model.model_id)
               for file in model.files)


def _check_destination(model: ModelSpec, destination: Path) -> None:
    """Only a damaged installation of this exact pair may be replaced."""
    if not destination.exists() and not destination.is_symlink():
        return
    if destination.is_symlink() or not destination.is_dir():
        raise RuntimeError(f"Model destination is not a folder: {destination}")
    expected = {file.name for file in model.files} | {"model-manifest.json"}
    unrelated = {entry.name for entry in destination.iterdir()} - expected
    if unrelated:
        raise RuntimeError(
            f"Model destination contains unrelated files; refusing to replace {destination}: "
            + ", ".join(sorted(unrelated))
        )


def _publish(model: ModelSpec, pending: Path, destination: Path, cache_dir: Path) -> None:
    _check_destination(model, destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    if pending.stat().st_dev != destination.parent.stat().st_dev:
        raise OSError(
            "The model cache and models folder must be on the same filesystem "
            "for atomic pair installation"
        )
    ready = Path(tempfile.mkdtemp(prefix=f".{model.model_id}-ready-", dir=cache_dir))
    replaced: Path | None = None
    try:
        for file in model.files:
            os.link(pending / file.name, ready / file.name)
        manifest = {
            "model": model.model_id,
            "repository": model.repository,
            "revision": model.revision,
            "files": {file.name: {"bytes": file.size, "sha256": file.sha256}
                      for file in model.files},
        }
        (ready / "model-manifest.json").write_text(
            json.dumps(manifest, indent=2) + "\n", encoding="utf-8"
        )
        if destination.exists():
            replaced = Path(tempfile.mkdtemp(prefix=f".{model.model_id}-old-", dir=cache_dir))
            replaced.rmdir()
            os.replace(destination, replaced)
        try:
            os.replace(ready, destination)
        except OSError:
            if replaced is not None and not destination.exists():
                os.replace(replaced, destination)
                replaced = None
            raise
    finally:
        if ready.exists():
            shutil.rmtree(ready)
    # Cleanup failures must not invalidate a fully published and verified pair.
    try:
        shutil.rmtree(pending)
        if replaced is not None:
            known = {file.name for file in model.files} | {"model-manifest.json"}
            if {path.name for path in replaced.iterdir()} <= known:
                shutil.rmtree(replaced)
    except OSError:
        pass


def install_models(
    model_ids: list[str], models_dir: Path, cache_dir: Path, progress: Progress,
) -> dict[str, object]:
    """Install selected pairs. The caller owns error/pause reporting."""
    models_dir = models_dir.expanduser().resolve()
    cache_dir = cache_dir.expanduser().resolve()
    cache_dir.mkdir(parents=True, exist_ok=True)
    completed: list[str] = []
    skipped: list[str] = []
    for model_id in dict.fromkeys(model_ids):
        model = MODELS[model_id]
        destination = models_dir / model.repository
        _emit(progress, "start", model_id, phase="model", path=str(destination))
        _check_destination(model, destination)
        if _installed(model, destination, progress):
            skipped.append(model_id)
            _emit(progress, "complete", model_id, path=str(destination), skipped=True)
            continue
        pending = cache_dir / (model_id + ".partial")
        _reject_symlink(pending)
        pending.mkdir(parents=True, exist_ok=True)
        _reject_symlink(pending)
        for file in model.files:
            _download_file(model, file, pending, progress)
        if not _installed(model, pending, progress):
            raise RuntimeError(f"Verified pair was lost before publication: {model_id}")
        _publish(model, pending, destination, cache_dir)
        completed.append(model_id)
        _emit(progress, "complete", model_id, path=str(destination), skipped=False)
    result: dict[str, object] = {"completed": completed, "skipped": skipped}
    progress({"event": "finished", **result})
    return result


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model-id", action="append", choices=tuple(MODELS),
                        help="A model to download; repeat for several")
    parser.add_argument("--all", action="store_true", help="Download all four model pairs")
    parser.add_argument("--list", action="store_true", help="List available model pairs and exit")
    parser.add_argument("--models-dir", type=Path, default=ROOT / "models")
    parser.add_argument("--cache-dir", type=Path, default=ROOT / ".cache/model-downloads")
    parser.add_argument("--json-progress", action="store_true",
                        help="Emit one flushed JSON object per progress event")
    arguments = parser.parse_args(argv)
    current_model = ""

    def progress(event: dict[str, object]) -> None:
        nonlocal current_model
        if isinstance(event.get("model"), str) and event["model"]:
            current_model = str(event["model"])
        if arguments.json_progress:
            print(json.dumps(event, ensure_ascii=False, separators=(",", ":")), flush=True)
        elif event["event"] == "progress":
            print(f"{event['model']}: {event['file']} "
                  f"{event['bytes']:,}/{event['total']:,} bytes", flush=True)
        else:
            print(json.dumps(event, ensure_ascii=False), flush=True)

    if arguments.list:
        for model in MODELS.values():
            progress({"event": "available", "model": model.model_id, "label": model.label,
                      "repository": model.repository, "bytes": sum(file.size for file in model.files),
                      "installed_path": str(arguments.models_dir / model.repository)})
        return 0
    if not arguments.all and not arguments.model_id:
        parser.error("select at least one --model-id, or use --all explicitly")
    chosen = list(MODELS) if arguments.all else arguments.model_id
    assert chosen is not None
    original_term = signal.signal(signal.SIGTERM, _signal_pause)
    original_int = signal.signal(signal.SIGINT, _signal_pause)
    try:
        install_models(chosen, arguments.models_dir, arguments.cache_dir, progress)
        return 0
    except DownloadPaused as error:
        progress({"event": "paused", "model": current_model, "message": str(error)})
        return 130
    except (OSError, ValueError, RuntimeError, urllib.error.URLError) as error:
        progress({"event": "error", "model": current_model, "message": str(error)})
        return 1
    finally:
        signal.signal(signal.SIGTERM, original_term)
        signal.signal(signal.SIGINT, original_int)


if __name__ == "__main__":
    raise SystemExit(main())
