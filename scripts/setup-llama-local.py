#!/usr/bin/env python3
"""Install a pinned official llama.cpp CUDA server inside this project's ignored cache."""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.request


ROOT = Path(__file__).resolve().parents[1]
RELEASE = "b11429"
BASE_URL = f"https://github.com/ggml-org/llama.cpp/releases/download/{RELEASE}"
# SHA256 values from the release's GitHub provenance attestation:
# https://github.com/ggml-org/llama.cpp/attestations/52904614
ARCHIVES = (
    ("llama-b11429-bin-ubuntu-cuda-12.8-x64.tar.gz",
     "b13c64b9224d3c89d9993945d70cc21177ad00178c21b8d34237e3bc139834f1", "server"),
    ("cudart-llama-b11429-bin-ubuntu-cuda-12.8-x64.tar.gz",
     "f4ab593e31507320b121f2cb21791f4c72420a2483d3df8bf2ca1a97d04e6845", "cuda"),
)
MAX_ARCHIVE_BYTES = 4 * 1024**3
MAX_EXTRACTED_BYTES = 8 * 1024**3


def download(name: str, expected_sha256: str, output: Path) -> None:
    url = f"{BASE_URL}/{name}"
    print(f"Downloading {url}", flush=True)
    request = urllib.request.Request(url, headers={"User-Agent": "Caption-Express-setup/1"})
    digest = hashlib.sha256()
    size = 0
    with urllib.request.urlopen(request, timeout=90) as source, output.open("wb") as target:
        length = source.headers.get("Content-Length", "")
        total = int(length) if length.isdigit() else 0
        last_reported = 0
        last_report_time = time.monotonic()
        while chunk := source.read(1024 * 1024):
            size += len(chunk)
            if size > MAX_ARCHIVE_BYTES:
                raise ValueError(f"Archive exceeds the 4 GiB safety limit: {name}")
            target.write(chunk)
            digest.update(chunk)
            now = time.monotonic()
            if size - last_reported >= 64 * 1024 * 1024 or now - last_report_time >= 5:
                progress = f"{size / 1e6:,.0f} MB"
                if total > 0:
                    progress += f" / {total / 1e6:,.0f} MB ({100 * size / total:.0f}%)"
                print(f"  {name}: {progress}", flush=True)
                last_reported, last_report_time = size, now
    if digest.hexdigest() != expected_sha256:
        raise ValueError(f"SHA256 mismatch for {name}; no files were installed")
    print(f"Verified {name} ({size:,} bytes)", flush=True)


def extract_archive(archive: Path, destination: Path) -> None:
    """Extract only regular files, directories, and symlinks contained in destination."""
    destination.mkdir()
    links: list[tuple[Path, str]] = []
    seen: set[Path] = set()
    total = 0
    with tarfile.open(archive, "r:gz") as source:
        for member in source:
            name = Path(member.name)
            if name.is_absolute() or ".." in name.parts or not name.parts:
                raise ValueError(f"Unsafe archive path: {member.name}")
            target = destination / name
            if target in seen:
                raise ValueError(f"Duplicate archive path: {member.name}")
            seen.add(target)
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
            elif member.isfile():
                total += member.size
                if total > MAX_EXTRACTED_BYTES:
                    raise ValueError("Archive exceeds the 8 GiB extraction safety limit")
                target.parent.mkdir(parents=True, exist_ok=True)
                stream = source.extractfile(member)
                if stream is None:
                    raise ValueError(f"Unreadable archive member: {member.name}")
                with stream, target.open("wb") as output:
                    shutil.copyfileobj(stream, output)
                target.chmod(0o755 if member.mode & 0o111 else 0o644)
            elif member.issym():
                links.append((target, member.linkname))
            else:
                raise ValueError(f"Unsupported archive member: {member.name}")
    for target, link in links:
        link_path = Path(link)
        if link_path.is_absolute() or not (target.parent / link_path).resolve().is_relative_to(
                destination.resolve()):
            raise ValueError(f"Unsafe archive symlink: {target} -> {link}")
        target.parent.mkdir(parents=True, exist_ok=True)
        target.symlink_to(link)


def library_dirs(server_root: Path, cuda_root: Path, executable: Path) -> list[Path]:
    paths = [executable.parent]
    for root in (server_root, cuda_root):
        for path in root.rglob("*"):
            if path.name.startswith("lib") and ".so" in path.name and (path.is_file() or path.is_symlink()):
                if path.parent not in paths:
                    paths.append(path.parent)
    all_names = {path.name for root in (server_root, cuda_root) for path in root.rglob("*")}
    required = {"libggml-cuda.so", "libcudart.so.12", "libcublas.so.12", "libcublasLt.so.12"}
    missing = required - all_names
    if missing:
        raise ValueError(f"The official archives lack required CUDA libraries: {sorted(missing)}")
    return paths


def main() -> int:
    if sys.platform != "linux" or os.uname().machine != "x86_64":
        raise RuntimeError("This pinned CUDA package supports Ubuntu x86_64 only")
    parent = ROOT / ".cache/llama.cpp"
    parent.mkdir(parents=True, exist_ok=True)
    installed = parent / RELEASE
    if installed.is_symlink():
        raise RuntimeError(f"Refusing to replace symlinked runtime at {installed}")
    if installed.exists():
        manifest = installed / "manifest.json"
        try:
            details = json.loads(manifest.read_text())
            paths = [details["server"], *details["lib_dirs"]]
            complete = (details["release"] == RELEASE and
                        details["archives"] == {name: digest for name, digest, _ in ARCHIVES} and
                        bool(details["lib_dirs"]) and
                        all((installed / path).resolve().is_relative_to(installed.resolve())
                            for path in paths) and
                        (installed / details["server"]).is_file() and
                        os.access(installed / details["server"], os.X_OK) and
                        all((installed / path).is_dir() for path in details["lib_dirs"]))
        except (OSError, ValueError, KeyError, TypeError):
            complete = False
        if complete:
            print(f"Already installed: {installed}")
            return 0
        print(f"Repairing incomplete project-owned runtime at {installed}", file=sys.stderr)
        shutil.rmtree(installed)
    staging = Path(tempfile.mkdtemp(prefix=f".{RELEASE}-", dir=parent))
    try:
        for name, expected_sha256, kind in ARCHIVES:
            archive = staging / name
            download(name, expected_sha256, archive)
            extract_archive(archive, staging / kind)
            archive.unlink()
        executables = [path for path in (staging / "server").rglob("llama-server") if path.is_file()]
        if len(executables) != 1 or not os.access(executables[0], os.X_OK):
            raise ValueError("Expected exactly one executable llama-server in the release archive")
        executable = executables[0]
        libraries = library_dirs(staging / "server", staging / "cuda", executable)
        environment = os.environ.copy()
        environment["LD_LIBRARY_PATH"] = ":".join(map(str, libraries))
        version = subprocess.run([str(executable), "--version"], env=environment,
                                 capture_output=True, text=True, timeout=30)
        if version.returncode:
            raise RuntimeError(f"Installed server failed --version: {version.stderr[-1000:]}")
        manifest = {
            "release": RELEASE,
            "server": str(executable.relative_to(staging)),
            "lib_dirs": [str(path.relative_to(staging)) for path in libraries],
            "archives": {name: digest for name, digest, _ in ARCHIVES},
        }
        (staging / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
        staging.rename(installed)
        print(f"Installed {RELEASE} at {installed}")
        print(f"Server version: {version.stdout.strip() or version.stderr.strip()}")
        return 0
    finally:
        if staging.exists():
            shutil.rmtree(staging)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError) as error:
        print(f"Setup failed: {error}", file=sys.stderr)
        raise SystemExit(1)
