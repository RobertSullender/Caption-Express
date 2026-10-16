"""Check that server discovery keeps each CUDA backend with its own libraries."""

import json
from io import BytesIO, StringIO
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "worker"))
import caption_worker


class ServerBackendTests(unittest.TestCase):
    def tearDown(self):
        caption_worker.unload_model()

    @staticmethod
    def sample_model(name="gemma-4-E4B-it"):
        return {"id": f"/{name}.gguf", "label": name,
                "family": "E4B", "model_path": f"/{name}.gguf",
                "projector_path": f"/mmproj-{name}.gguf"}

    def test_server_reused_until_model_or_thinking_changes(self):
        stock = self.sample_model()
        other = self.sample_model("gemma-4-E2B-it")
        with mock.patch("caption_worker.GemmaServer") as constructor, \
             mock.patch("caption_worker.emit"):
            first = constructor.return_value
            first.healthy.return_value = True
            self.assertIs(first, caption_worker.get_server(stock, True))
            self.assertIs(first, caption_worker.get_server(stock, True))
            constructor.assert_called_once_with(stock, True)
            self.assertEqual(stock["id"], caption_worker.loaded_status()["loaded_model_id"])
            self.assertTrue(caption_worker.loaded_status()["loaded_model_thinking"])

            caption_worker.get_server(stock, False)
            self.assertEqual(2, constructor.call_count)
            first.stop.assert_called_once()
            caption_worker.get_server(other, False)
            self.assertEqual(3, constructor.call_count)
            self.assertEqual(other["id"], caption_worker.loaded_status()["loaded_model_id"])
            caption_worker.unload_model()
            self.assertIsNone(caption_worker.loaded_status()["loaded_model_id"])

    def test_unhealthy_server_is_replaced_and_status_clears_it(self):
        model = self.sample_model()
        with mock.patch("caption_worker.GemmaServer") as constructor, \
             mock.patch("caption_worker.emit"):
            first = constructor.return_value
            first.healthy.return_value = True
            caption_worker.get_server(model, True)
            first.healthy.return_value = False
            self.assertIsNone(caption_worker.loaded_status()["loaded_model_id"])
            first.stop.assert_called_once()
            caption_worker.get_server(model, True)
            self.assertEqual(2, constructor.call_count)

    def test_audio_only_uses_gemma_and_preserves_cache_after_two_captions(self):
        model = self.sample_model()
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "clip.wav"
            source.write_bytes(b"sample")
            audio_content = ([{"type": "text", "text": "Describe sound."},
                              {"type": "input_audio", "input_audio": {"data": "AA==", "format": "wav"}}], True)
            with mock.patch.object(caption_worker, "TEMP_DIR", Path(directory)), \
                 mock.patch("caption_worker.discover_models", return_value=[model]), \
                 mock.patch("caption_worker.probe_media", return_value=(1.0, False, True)), \
                 mock.patch("caption_worker.make_content", return_value=audio_content), \
                 mock.patch("caption_worker.GemmaServer") as constructor, \
                 mock.patch("caption_worker.emit") as emit:
                server = constructor.return_value
                server.healthy.return_value = True
                server.infer.return_value = "An electronic tone sounds."
                caption_worker.caption({"path": str(source), "model_id": model["id"]})
                caption_worker.caption({"path": str(source), "model_id": model["id"]})
                constructor.assert_called_once_with(model, True)
                self.assertEqual(2, server.infer.call_count)
                self.assertFalse(server.stop.called)
                completions = [call.kwargs for call in emit.call_args_list
                               if call.args == ("complete",)]
                self.assertEqual(2, len(completions))
                self.assertEqual(model["id"], completions[-1]["loaded_model_id"])
                self.assertEqual("An electronic tone sounds.", completions[-1]["caption"])

    def test_caption_failure_unloads_server(self):
        model = self.sample_model()
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "clip.wav"
            source.write_bytes(b"sample")
            with mock.patch.object(caption_worker, "TEMP_DIR", Path(directory)), \
                 mock.patch("caption_worker.discover_models", return_value=[model]), \
                 mock.patch("caption_worker.probe_media", return_value=(1.0, False, True)), \
                 mock.patch("caption_worker.make_content", return_value=([{"type": "input_audio"}], True)), \
                 mock.patch("caption_worker.GemmaServer") as constructor, \
                 mock.patch("caption_worker.emit"):
                constructor.return_value.infer.side_effect = RuntimeError("server failed")
                with self.assertRaisesRegex(RuntimeError, "server failed"):
                    caption_worker.caption({"path": str(source), "model_id": model["id"]})
                constructor.return_value.stop.assert_called_once()
                self.assertIsNone(caption_worker.loaded_status()["loaded_model_id"])

    def test_unload_command_returns_status_without_loaded_model(self):
        model = self.sample_model()
        commands = StringIO('{"command":"unload_model"}\n{"command":"shutdown"}\n')
        with mock.patch("caption_worker.GemmaServer") as constructor, \
             mock.patch("caption_worker.discover_models", return_value=[model]), \
             mock.patch.object(sys, "stdin", commands), \
             mock.patch("caption_worker.emit") as emit:
            constructor.return_value.healthy.return_value = True
            caption_worker.get_server(model, True)
            caption_worker.main()
            statuses = [call.kwargs for call in emit.call_args_list
                        if call.args == ("status",)]
            self.assertEqual(1, len(statuses))
            self.assertEqual([model], statuses[0]["models"])
            self.assertIsNone(statuses[0]["loaded_model_id"])
            self.assertIsNone(statuses[0]["loaded_model_label"])
            self.assertIsNone(statuses[0]["loaded_model_thinking"])
            constructor.return_value.stop.assert_called_once()

    def test_invalid_guidance_and_json_leave_warm_model_available(self):
        model = self.sample_model()
        commands = StringIO('not-json\n'
                            '{"command":"validate_guidance","guidance_text":"00:10-00:20 hi",'
                            '"duration":2}\n'
                            '{"command":"status"}\n'
                            '{"command":"shutdown"}\n')
        with mock.patch("caption_worker.GemmaServer") as constructor, \
             mock.patch("caption_worker.discover_models", return_value=[model]), \
             mock.patch.object(sys, "stdin", commands), \
             mock.patch("caption_worker.emit") as emit, \
             mock.patch("caption_worker.traceback.print_exc"):
            constructor.return_value.healthy.return_value = True
            caption_worker.get_server(model, True)
            caption_worker.main()
            errors = [call.kwargs for call in emit.call_args_list if call.args == ("error",)]
            statuses = [call.kwargs for call in emit.call_args_list if call.args == ("status",)]
            self.assertEqual(2, len(errors))
            self.assertEqual(model["id"], statuses[0]["loaded_model_id"])
            constructor.return_value.stop.assert_called_once()

    def test_reruns_sample_with_seed_but_first_draft_remains_deterministic(self):
        server = caption_worker.GemmaServer.__new__(caption_worker.GemmaServer)
        server.url = "http://127.0.0.1:12345"
        server.thinking = False
        response = {"choices": [{"finish_reason": "stop",
                                 "message": {"content": "A person walks."}}]}
        payloads = []

        def fake_urlopen(request, timeout):
            payloads.append(json.loads(request.data))
            return BytesIO(json.dumps(response).encode())

        with mock.patch("caption_worker.urllib.request.urlopen", side_effect=fake_urlopen):
            self.assertEqual("A person walks.", server.infer([], None))
            self.assertEqual("A person walks.", server.infer([], None, 123))
            self.assertEqual("A person walks.", server.infer([], "Describe only visible evidence."))
        self.assertEqual(0, payloads[0]["temperature"])
        self.assertNotIn("seed", payloads[0])
        self.assertEqual(0.1, payloads[1]["temperature"])
        self.assertEqual(123, payloads[1]["seed"])
        self.assertEqual(["user"], [message["role"] for message in payloads[0]["messages"]])
        self.assertEqual(["system", "user"],
                         [message["role"] for message in payloads[2]["messages"]])

    def test_project_backend_precedes_lm_studio_and_uses_matching_libraries(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            home = root / "home"
            local = root / ".cache/llama.cpp/b11429"
            server = local / "server/bin/llama-server"
            server.parent.mkdir(parents=True)
            server.touch()
            cuda = local / "cuda/lib"
            cuda.mkdir(parents=True)
            (local / "manifest.json").write_text(json.dumps({
                "release": "b11429", "server": "server/bin/llama-server",
                "lib_dirs": ["server/bin", "cuda/lib"]}))
            lm = home / ".lmstudio/extensions/backends"
            old = lm / "llama.cpp-linux-x86_64-nvidia-cuda12-avx2-2.57.0/llama-server"
            old.parent.mkdir(parents=True)
            old.touch()
            vendor = lm / "vendor/linux-llama-cuda12-vendor-v1"
            vendor.mkdir(parents=True)
            with mock.patch.object(caption_worker, "ROOT", root), \
                 mock.patch.object(Path, "home", return_value=home), \
                 mock.patch("caption_worker.shutil.which", return_value=None):
                with mock.patch.dict(os.environ, {}, clear=True):
                    candidates = caption_worker.server_candidates()
                with mock.patch.dict(os.environ, {
                        "CAPTION_EXPRESS_LLAMA_SERVER": str(server)}, clear=True):
                    explicit = caption_worker.server_candidates()
            self.assertEqual(server, candidates[0][0])
            self.assertEqual((server.parent, cuda), candidates[0][1])
            self.assertEqual(old, candidates[1][0])
            self.assertIn(vendor, candidates[1][1])
            self.assertNotIn(vendor, candidates[0][1])
            self.assertIn(cuda, explicit[0][1])
            self.assertNotIn(vendor, explicit[0][1])

    def test_explicit_override_does_not_borrow_lm_studio_cuda_runtime(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            server = root / "custom/bin/llama-server"
            server.parent.mkdir(parents=True)
            server.touch()
            libraries = root / "custom/cuda"
            libraries.mkdir()
            with mock.patch.object(Path, "home", return_value=root / "home"), \
                 mock.patch.dict(os.environ, {
                     "CAPTION_EXPRESS_LLAMA_SERVER": str(server),
                     "CAPTION_EXPRESS_LLAMA_LIB_DIR": str(libraries)}, clear=True):
                candidates = caption_worker.server_candidates()
            self.assertEqual([server], [candidate[0] for candidate in candidates])
            self.assertEqual((libraries, server.parent), candidates[0][1])

    def test_missing_explicit_override_fails_instead_of_switching_backend(self):
        with mock.patch.dict(os.environ, {"CAPTION_EXPRESS_LLAMA_SERVER": "/missing/server"},
                             clear=True):
            with self.assertRaises(FileNotFoundError):
                caption_worker.server_candidates()

    def test_model_server_environment_removes_launcher_libvlc_paths(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            vlc = root / ".cache/libvlc-root/usr/lib"
            pinned = root / ".cache/llama.cpp/b11429/cuda"
            pinned.mkdir(parents=True)
            with mock.patch.object(caption_worker, "ROOT", root), \
                 mock.patch.dict(os.environ, {
                     "LD_LIBRARY_PATH": f"{vlc}:{vlc / 'vlc'}:/opt/custom/lib"}, clear=True):
                environment = caption_worker.server_environment((pinned,))
            self.assertEqual(f"{pinned}:/opt/custom/lib", environment["LD_LIBRARY_PATH"])


if __name__ == "__main__":
    unittest.main()
