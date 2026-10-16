"""Small local HTTP fixtures for the resumable, atomic Gemma model installer."""

import hashlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from unittest.mock import patch
from urllib.parse import unquote, urlparse


SCRIPT = Path(__file__).resolve().parents[1] / "scripts/download-gemma-models.py"
IMPORT = importlib.util.spec_from_file_location("download_gemma_models", SCRIPT)
downloader = importlib.util.module_from_spec(IMPORT)
sys.modules[IMPORT.name] = downloader
IMPORT.loader.exec_module(downloader)


class FakeResponse(io.BytesIO):
    def __init__(self, payload, status=200, headers=None):
        super().__init__(payload)
        self.status = status
        self.headers = headers or {}


class FakeHTTP:
    def __init__(self, files, *, ignore_range=False):
        self.files = files
        self.ignore_range = ignore_range
        self.requests = []

    def __call__(self, request, timeout):
        self.requests.append(request)
        name = unquote(urlparse(request.full_url).path.rsplit("/", 1)[1])
        content = self.files[name]
        byte_range = request.headers.get("Range")
        if byte_range and not self.ignore_range:
            offset = int(byte_range.removeprefix("bytes=").removesuffix("-"))
            return FakeResponse(content[offset:], 206,
                                {"Content-Range": f"bytes {offset}-{len(content)-1}/{len(content)}"})
        return FakeResponse(content)


class ModelDownloaderTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        root = Path(self.directory.name)
        self.models_dir = root / "models"
        self.cache_dir = root / ".cache/model-downloads"
        self.weight = b"GGUFtiny-weight-content"
        self.projector = b"GGUFtiny-projector-content"
        self.weight_name = "gemma-4-E2B-it-Q8_0.gguf"
        self.projector_name = "mmproj-gemma-4-E2B-it-BF16.gguf"
        self.files = {self.weight_name: self.weight,
                      self.projector_name: self.projector}
        self.model = downloader.ModelSpec(
            "tiny", "Tiny Gemma 4 E2B", "testing/gemma-4-E2B-it-GGUF", "revision123",
            downloader.FileSpec(self.weight_name, len(self.weight),
                                hashlib.sha256(self.weight).hexdigest()),
            downloader.FileSpec(self.projector_name, len(self.projector),
                                hashlib.sha256(self.projector).hexdigest()),
        )
        self.original_models = downloader.MODELS
        downloader.MODELS = {"tiny": self.model}
        self.addCleanup(setattr, downloader, "MODELS", self.original_models)
        self.events = []

    def install(self):
        return downloader.install_models(["tiny"], self.models_dir,
                                         self.cache_dir, self.events.append)

    def test_downloads_pair_from_one_pinned_repository_and_publishes_together(self):
        server = FakeHTTP(self.files)
        with patch.object(downloader.urllib.request, "urlopen", server):
            result = self.install()

        destination = self.models_dir / self.model.repository
        self.assertEqual(self.weight, (destination / self.weight_name).read_bytes())
        self.assertEqual(self.projector, (destination / self.projector_name).read_bytes())
        self.assertTrue((destination / "model-manifest.json").is_file())
        self.assertEqual({"completed": ["tiny"], "skipped": []}, result)
        self.assertEqual("finished", self.events[-1]["event"])
        self.assertIn("verify", {event["event"] for event in self.events})
        self.assertIn("complete", {event["event"] for event in self.events})
        self.assertEqual(2, len(server.requests))
        for request in server.requests:
            self.assertIn("/testing/gemma-4-E2B-it-GGUF/resolve/revision123/",
                          request.full_url)
        self.assertFalse(list(self.models_dir.rglob("*.part")))

    def test_existing_verified_pair_skips_network(self):
        with patch.object(downloader.urllib.request, "urlopen", FakeHTTP(self.files)):
            self.install()
        self.events.clear()
        with patch.object(downloader.urllib.request, "urlopen",
                          side_effect=AssertionError("network should not be used")):
            result = self.install()
        self.assertEqual({"completed": [], "skipped": ["tiny"]}, result)
        self.assertTrue(next(event for event in self.events
                             if event["event"] == "complete")["skipped"])

    def test_resumes_partial_file_with_http_range(self):
        pending = self.cache_dir / "tiny.partial"
        pending.mkdir(parents=True)
        (pending / (self.weight_name + ".part")).write_bytes(self.weight[:7])
        server = FakeHTTP(self.files)
        with patch.object(downloader.urllib.request, "urlopen", server):
            self.install()
        self.assertEqual("bytes=7-", server.requests[0].headers.get("Range"))
        self.assertTrue(next(event for event in self.events
                             if event["event"] == "start" and
                             event.get("file") == self.weight_name)["resumed"])
        self.assertEqual(self.weight, (self.models_dir / self.model.repository /
                                       self.weight_name).read_bytes())

    def test_restarts_partial_when_server_ignores_range(self):
        pending = self.cache_dir / "tiny.partial"
        pending.mkdir(parents=True)
        (pending / (self.weight_name + ".part")).write_bytes(self.weight[:7])
        server = FakeHTTP(self.files, ignore_range=True)
        with patch.object(downloader.urllib.request, "urlopen", server):
            self.install()
        self.assertEqual("bytes=7-", server.requests[0].headers.get("Range"))
        self.assertEqual(self.weight, (self.models_dir / self.model.repository /
                                       self.weight_name).read_bytes())

    def test_short_transfer_leaves_resumable_partial_outside_model_catalog(self):
        short = dict(self.files)
        short[self.weight_name] = self.weight[:6]
        with patch.object(downloader.urllib.request, "urlopen", FakeHTTP(short)):
            with self.assertRaisesRegex(RuntimeError, "retry to resume"):
                self.install()
        self.assertFalse(self.models_dir.exists())
        pending = self.cache_dir / "tiny.partial" / (self.weight_name + ".part")
        self.assertEqual(self.weight[:6], pending.read_bytes())

        server = FakeHTTP(self.files)
        with patch.object(downloader.urllib.request, "urlopen", server):
            self.install()
        self.assertEqual("bytes=6-", server.requests[0].headers.get("Range"))
        self.assertEqual(self.weight, (self.models_dir / self.model.repository /
                                       self.weight_name).read_bytes())

    def test_bad_hash_cannot_become_a_discoverable_pair(self):
        corrupted = dict(self.files)
        corrupted[self.projector_name] = b"X" + self.projector[1:]
        with patch.object(downloader.urllib.request, "urlopen", FakeHTTP(corrupted)):
            with self.assertRaisesRegex(RuntimeError, "SHA256 mismatch"):
                self.install()
        self.assertFalse(self.models_dir.exists())
        self.assertFalse(list(self.cache_dir.rglob(self.projector_name + ".part")))

    def test_rejects_symlinked_pending_directory_without_touching_target(self):
        target = self.cache_dir.parent / "unrelated-folder"
        target.mkdir(parents=True)
        note = target / "keep.txt"
        note.write_text("keep")
        self.cache_dir.mkdir(parents=True)
        (self.cache_dir / "tiny.partial").symlink_to(target, target_is_directory=True)
        with patch.object(downloader.urllib.request, "urlopen",
                          side_effect=AssertionError("network should not be used")):
            with self.assertRaisesRegex(RuntimeError, "Refusing symlinked"):
                self.install()
        self.assertEqual("keep", note.read_text())

    def test_rejects_symlinked_partial_and_completed_files_without_touching_targets(self):
        pending = self.cache_dir / "tiny.partial"
        pending.mkdir(parents=True)
        target = self.cache_dir.parent / "unrelated-model.gguf"
        target.write_bytes(b"keep this file")
        for name in (self.weight_name + ".part", self.weight_name):
            with self.subTest(name=name):
                link = pending / name
                link.symlink_to(target)
                with patch.object(downloader.urllib.request, "urlopen",
                                  side_effect=AssertionError("network should not be used")):
                    with self.assertRaisesRegex(RuntimeError, "Refusing symlinked"):
                        self.install()
                self.assertEqual(b"keep this file", target.read_bytes())
                link.unlink()

    def test_unrelated_file_in_destination_is_preserved_and_blocks_replacement(self):
        destination = self.models_dir / self.model.repository
        destination.mkdir(parents=True)
        note = destination / "my-notes.txt"
        note.write_text("Keep this file")
        with patch.object(downloader.urllib.request, "urlopen",
                          side_effect=AssertionError("network should not be used")):
            with self.assertRaisesRegex(RuntimeError, "unrelated files"):
                self.install()
        self.assertEqual("Keep this file", note.read_text())

    def test_replaces_only_a_damaged_installation_of_the_same_pair(self):
        destination = self.models_dir / self.model.repository
        destination.mkdir(parents=True)
        (destination / self.weight_name).write_bytes(b"GGUFbroken")
        with patch.object(downloader.urllib.request, "urlopen", FakeHTTP(self.files)):
            self.install()
        self.assertEqual(self.weight, (destination / self.weight_name).read_bytes())
        self.assertEqual(self.projector, (destination / self.projector_name).read_bytes())

    def test_fails_preflight_before_request_when_disk_is_too_full(self):
        free = downloader.SPACE_MARGIN_BYTES - 1
        usage = shutil_disk_usage(total=free, used=0, free=free)
        with patch.object(downloader.shutil, "disk_usage", return_value=usage), \
             patch.object(downloader.urllib.request, "urlopen",
                          side_effect=AssertionError("network should not be used")):
            with self.assertRaisesRegex(OSError, "Not enough free space"):
                self.install()
        self.assertFalse(self.models_dir.exists())

    def test_json_progress_error_names_the_failed_model(self):
        free = downloader.SPACE_MARGIN_BYTES - 1
        usage = shutil_disk_usage(total=free, used=0, free=free)
        output = io.StringIO()
        with patch.object(downloader.shutil, "disk_usage", return_value=usage), \
             redirect_stdout(output):
            code = downloader.main([
                "--model-id", "tiny", "--models-dir", str(self.models_dir),
                "--cache-dir", str(self.cache_dir), "--json-progress",
            ])
        self.assertEqual(1, code)
        events = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertEqual("error", events[-1]["event"])
        self.assertEqual("tiny", events[-1]["model"])
        self.assertIn("Not enough free space", events[-1]["message"])


class shutil_disk_usage(tuple):
    __slots__ = ()

    def __new__(cls, total, used, free):
        return super().__new__(cls, (total, used, free))

    @property
    def free(self):
        return self[2]


if __name__ == "__main__":
    unittest.main()
