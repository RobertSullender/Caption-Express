"""Exercise Gemma soundtrack captioning with actual exported-media formats."""

from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "worker"))
import caption_worker


MODEL = {"id": "test-gemma", "label": "Gemma test",
         "model_path": "/tmp/test-gemma.gguf", "projector_path": "/tmp/test-mmproj.gguf"}


class AudioClaimFilterTests(unittest.TestCase):
    def test_absence_claim_keeps_audible_sound_after_contrast(self):
        for answer in ("No speech but a low hum is audible.",
                       "There is no speech; a low hum is audible.",
                       "No speech, only a low hum is audible."):
            with self.subTest(answer=answer):
                self.assertEqual("A low hum is audible.",
                                 caption_worker.remove_audio_absence_claims(answer))

    def test_absence_claim_keeps_audible_sound_before_contrast(self):
        for answer in ("A low hum is audible; no speech.",
                       "A low hum is audible but no music is heard."):
            with self.subTest(answer=answer):
                self.assertEqual("A low hum is audible.",
                                 caption_worker.remove_audio_absence_claims(answer))

    def test_absence_claim_does_not_create_sounds_from_an_absence_list(self):
        for answer in ("No speech, music, or effects are audible.",
                       "No speech but no music is audible.",
                       "No speech; the recording is silent."):
            with self.subTest(answer=answer):
                self.assertEqual("", caption_worker.remove_audio_absence_claims(answer))
        self.assertEqual("A voice sounds tired but clear.",
                         caption_worker.remove_audio_absence_claims(
                             "A voice sounds tired but clear."))

    def test_refusal_keeps_separate_positive_sound_and_still_flags_refusal(self):
        for answer in ("No recording was provided, but a low hum is audible.",
                       "No audio file was attached; a low hum is audible.",
                       "A low hum is audible; no audio file was attached."):
            with self.subTest(answer=answer):
                self.assertEqual(("A low hum is audible.", True),
                                 caption_worker.remove_audio_input_refusal(answer))
        self.assertEqual(("", True), caption_worker.remove_audio_input_refusal(
            "No recording was provided. Please upload the audio file."))
        self.assertEqual(("", True), caption_worker.remove_audio_input_refusal(
            "No recording was provided; I can only guess."))

    def test_absence_is_not_mistaken_for_a_missing_audio_input(self):
        answer = "A low hum is audible; the recording is silent."
        self.assertEqual((answer, False), caption_worker.remove_audio_input_refusal(answer))
        self.assertEqual("A low hum is audible.",
                         caption_worker.remove_audio_absence_claims(answer))


@unittest.skipUnless(shutil.which("ffmpeg"), "FFmpeg is needed for media extraction")
class GemmaAudioTests(unittest.TestCase):
    def setUp(self):
        caption_worker.unload_model()

    def tearDown(self):
        caption_worker.unload_model()

    @staticmethod
    def media(path: Path, video: bool, silent: bool = False,
              duration: float = 1) -> None:
        command = ["ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-y"]
        if video:
            command += ["-f", "lavfi", "-i", f"color=c=blue:s=64x64:r=24:d={duration}"]
        command += ["-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono" if silent else
                    f"sine=frequency=440:sample_rate=16000:duration={duration}",
                    "-t", str(duration)]
        command += (["-c:v", "libx264", "-c:a", "aac"] if video else ["-c:a", "flac"])
        subprocess.run([*command, str(path)], check=True, capture_output=True)

    def test_audio_only_uses_selected_gemma_and_keeps_it_loaded(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            source = folder / "tone.flac"
            self.media(source, False)
            with mock.patch.object(caption_worker, "discover_models", return_value=[MODEL]), \
                 mock.patch.object(caption_worker, "GemmaServer") as server_type, \
                 mock.patch.object(caption_worker, "TEMP_DIR", folder), \
                 mock.patch.object(caption_worker, "emit") as emit:
                server_type.return_value.infer.return_value = "A steady electronic tone is audible."
                caption_worker.caption({"path": str(source), "model_id": MODEL["id"],
                                        "system_prompt": "Describe only audible sounds."})
                server_type.assert_called_once_with(MODEL, True)
                server_type.return_value.stop.assert_not_called()
                self.assertEqual(MODEL["id"], caption_worker.loaded_status()["loaded_model_id"])
                content, system_prompt = server_type.return_value.infer.call_args.args[:2]
                self.assertEqual("Describe only audible sounds.", system_prompt)
                self.assertIn("input_audio", [part["type"] for part in content])
                self.assertNotIn("image_url", [part["type"] for part in content])
                self.assertEqual("input_audio", content[0]["type"])
                self.assertEqual("A steady electronic tone is audible.",
                                 emit.call_args.kwargs["caption"])

    def test_audio_only_retains_audible_sound_after_negative_claim(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            source = folder / "tone.flac"
            self.media(source, False)
            with mock.patch.object(caption_worker, "discover_models", return_value=[MODEL]), \
                 mock.patch.object(caption_worker, "GemmaServer") as server_type, \
                 mock.patch.object(caption_worker, "TEMP_DIR", folder), \
                 mock.patch.object(caption_worker, "emit") as emit:
                server_type.return_value.infer.return_value = (
                    "No speech but a low hum is audible.")
                caption_worker.caption({"path": str(source), "model_id": MODEL["id"]})
                self.assertEqual("A low hum is audible.", emit.call_args.kwargs["caption"])
                self.assertIsNone(emit.call_args.kwargs["audio_warning"])

    def test_video_audio_and_refinement_share_one_server(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            source = folder / "blue.mp4"
            self.media(source, True)
            with mock.patch.object(caption_worker, "discover_models", return_value=[MODEL]), \
                 mock.patch.object(caption_worker, "GemmaServer") as server_type, \
                 mock.patch.object(caption_worker, "TEMP_DIR", folder), \
                 mock.patch.object(caption_worker, "emit") as emit:
                server_type.return_value.infer.side_effect = [
                    "A still blue frame.", "A steady tone.",
                    "A still blue frame accompanies a steady tone."]
                caption_worker.caption({"path": str(source), "model_id": MODEL["id"],
                                        "refine_caption": True, "variation_seed": 123})
                self.assertEqual(1, server_type.call_count)
                self.assertEqual(3, server_type.return_value.infer.call_count)
                self.assertEqual([123, 100_123, 200_123], [
                    call.args[2] for call in server_type.return_value.infer.call_args_list])
                self.assertEqual("A still blue frame accompanies a steady tone.",
                                 emit.call_args.kwargs["caption"])
                self.assertTrue(emit.call_args.kwargs["refinement_applied"])
                server_type.return_value.stop.assert_not_called()

    def test_soundtrack_pass_sends_audio_without_visual_frame(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            source = folder / "blue.mp4"
            self.media(source, True)
            with mock.patch.object(caption_worker, "discover_models", return_value=[MODEL]), \
                 mock.patch.object(caption_worker, "GemmaServer") as server_type, \
                 mock.patch.object(caption_worker, "TEMP_DIR", folder), \
                 mock.patch.object(caption_worker, "emit"):
                server_type.return_value.infer.side_effect = ["A blue frame.", "A steady tone."]
                caption_worker.caption({"path": str(source), "model_id": MODEL["id"]})
                content = server_type.return_value.infer.call_args_list[1].args[0]
                self.assertEqual(0, sum(part["type"] == "image_url" for part in content))
                self.assertEqual(1, sum(part["type"] == "input_audio" for part in content))

    def test_attached_non_silent_audio_with_empty_answer_requires_review(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            source = folder / "blue.mp4"
            self.media(source, True)
            with mock.patch.object(caption_worker, "discover_models", return_value=[MODEL]), \
                 mock.patch.object(caption_worker, "GemmaServer") as server_type, \
                 mock.patch.object(caption_worker, "TEMP_DIR", folder), \
                 mock.patch.object(caption_worker, "emit") as emit:
                server_type.return_value.infer.side_effect = [
                    "A blue frame.", "There are no audible sounds in this recording."]
                caption_worker.caption({"path": str(source), "model_id": MODEL["id"],
                                        "refine_caption": True})
                self.assertEqual("A blue frame.", emit.call_args.kwargs["caption"])
                self.assertFalse(emit.call_args.kwargs["refinement_applied"])
                self.assertIn("refinement was skipped",
                              emit.call_args.kwargs["refinement_skipped_reason"])
                self.assertIsNone(emit.call_args.kwargs["refinement_error"])
                self.assertIn("attached non-silent audio",
                              emit.call_args.kwargs["audio_warning"])

    def test_later_empty_audio_window_requires_review_after_an_earlier_sound(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            source = folder / "long-tone.flac"
            self.media(source, False, duration=26)
            with mock.patch.object(caption_worker, "discover_models", return_value=[MODEL]), \
                 mock.patch.object(caption_worker, "GemmaServer") as server_type, \
                 mock.patch.object(caption_worker, "TEMP_DIR", folder), \
                 mock.patch.object(caption_worker, "emit") as emit:
                server_type.return_value.infer.side_effect = [
                    "A steady tone is audible.", "There are no audible sounds in this recording."]
                caption_worker.caption({"path": str(source), "model_id": MODEL["id"]})
                self.assertEqual("A steady tone is audible.", emit.call_args.kwargs["caption"])
                self.assertIn("25.0–26.0s", emit.call_args.kwargs["audio_warning"])

    def test_silence_is_not_sent_to_gemma(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            source = folder / "silence.flac"
            self.media(source, False, silent=True)
            with mock.patch.object(caption_worker, "discover_models", return_value=[MODEL]), \
                 mock.patch.object(caption_worker, "GemmaServer") as server_type, \
                 mock.patch.object(caption_worker, "TEMP_DIR", folder), \
                 mock.patch.object(caption_worker, "emit") as emit:
                caption_worker.caption({"path": str(source), "model_id": MODEL["id"]})
                server_type.return_value.infer.assert_not_called()
                self.assertEqual("No discernible sound.", emit.call_args.kwargs["caption"])

    def test_refusal_does_not_enter_video_caption_and_reports_review_warning(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            source = folder / "blue.mp4"
            self.media(source, True)
            with mock.patch.object(caption_worker, "discover_models", return_value=[MODEL]), \
                 mock.patch.object(caption_worker, "GemmaServer") as server_type, \
                 mock.patch.object(caption_worker, "TEMP_DIR", folder), \
                 mock.patch.object(caption_worker, "emit") as emit:
                server_type.return_value.infer.side_effect = [
                    "A still blue frame.",
                    "I cannot describe the sound because no recording was provided. "
                    "Please upload the audio file."]
                caption_worker.caption({"path": str(source), "model_id": MODEL["id"]})
                self.assertEqual("A still blue frame.", emit.call_args.kwargs["caption"])
                self.assertIn("Listen and add", emit.call_args.kwargs["audio_warning"])

    def test_audio_only_refusal_is_an_error_instead_of_false_silence(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            source = folder / "tone.flac"
            self.media(source, False)
            with mock.patch.object(caption_worker, "discover_models", return_value=[MODEL]), \
                 mock.patch.object(caption_worker, "GemmaServer") as server_type, \
                 mock.patch.object(caption_worker, "TEMP_DIR", folder), \
                 mock.patch.object(caption_worker, "emit"):
                server_type.return_value.infer.return_value = (
                    "No recording was provided. I need the audio to describe the sounds.")
                with self.assertRaisesRegex(RuntimeError, "usable audio caption"):
                    caption_worker.caption({"path": str(source), "model_id": MODEL["id"]})

    def test_refinement_skips_missing_sound_but_guidance_can_still_synthesize(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            source = folder / "blue.mp4"
            self.media(source, True)
            refusal = "No recording was provided. Please upload the audio file."
            with mock.patch.object(caption_worker, "discover_models", return_value=[MODEL]), \
                 mock.patch.object(caption_worker, "GemmaServer") as server_type, \
                 mock.patch.object(caption_worker, "TEMP_DIR", folder), \
                 mock.patch.object(caption_worker, "emit") as emit:
                server_type.return_value.infer.side_effect = ["A still blue frame.", refusal]
                caption_worker.caption({"path": str(source), "model_id": MODEL["id"],
                                        "refine_caption": True})
                self.assertEqual(2, server_type.return_value.infer.call_count)
                self.assertFalse(emit.call_args.kwargs["refinement_applied"])
                self.assertIn("refinement was skipped",
                              emit.call_args.kwargs["refinement_skipped_reason"])
                self.assertIsNone(emit.call_args.kwargs["refinement_error"])
                self.assertEqual("A still blue frame.", emit.call_args.kwargs["caption"])
                self.assertIn("Listen and add", emit.call_args.kwargs["audio_warning"])

                server_type.return_value.infer.reset_mock()
                server_type.return_value.infer.side_effect = [
                    "A still blue frame.", refusal, "A blue frame remains visible."]
                caption_worker.caption({"path": str(source), "model_id": MODEL["id"],
                                        "guidance_text": "00:00-00:01: A blue frame remains visible."})
                self.assertEqual(3, server_type.return_value.infer.call_count)
                self.assertTrue(emit.call_args.kwargs["refinement_applied"])
                self.assertIn("Listen and add", emit.call_args.kwargs["audio_warning"])

    def test_identical_later_audio_is_not_repeated(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            source = folder / "long-tone.flac"
            self.media(source, False, duration=26)
            with mock.patch.object(caption_worker, "discover_models", return_value=[MODEL]), \
                 mock.patch.object(caption_worker, "GemmaServer") as server_type, \
                 mock.patch.object(caption_worker, "TEMP_DIR", folder), \
                 mock.patch.object(caption_worker, "emit") as emit:
                server_type.return_value.infer.return_value = "A steady tone is audible."
                caption_worker.caption({"path": str(source), "model_id": MODEL["id"]})
                self.assertEqual(2, server_type.return_value.infer.call_count)
                later_content = server_type.return_value.infer.call_args.args[0]
                later_prompt = next(part["text"] for part in later_content
                                    if part["type"] == "text")
                self.assertIn("preceding audio caption", later_prompt)
                self.assertEqual("A steady tone is audible.", emit.call_args.kwargs["caption"])

    def test_recurrence_after_an_intervening_sound_is_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            source = folder / "sequence.flac"
            source.touch()
            with mock.patch.object(caption_worker, "probe_media", return_value=(51, False, True)), \
                 mock.patch.object(caption_worker, "discover_models", return_value=[MODEL]), \
                 mock.patch.object(caption_worker, "make_content",
                                   return_value=([{"type": "input_audio"}], True)), \
                 mock.patch.object(caption_worker, "GemmaServer") as server_type, \
                 mock.patch.object(caption_worker, "TEMP_DIR", folder), \
                 mock.patch.object(caption_worker, "emit") as emit:
                server_type.return_value.infer.side_effect = [
                    "A bell rings.", "A hum starts.", "A bell rings."]
                caption_worker.caption({"path": str(source), "model_id": MODEL["id"]})
                self.assertEqual(3, server_type.return_value.infer.call_count)
                self.assertEqual("A bell rings. A hum starts. A bell rings.",
                                 emit.call_args.kwargs["caption"])

    def test_invalid_rerun_seed_fails_before_loading_model(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            source = folder / "tone.flac"
            self.media(source, False)
            with mock.patch.object(caption_worker, "discover_models", return_value=[MODEL]), \
                 mock.patch.object(caption_worker, "GemmaServer") as server_type:
                for invalid in (True, 0, -1, 2_147_483_648, 1.5, "123"):
                    with self.subTest(invalid=invalid), self.assertRaisesRegex(
                            ValueError, "variation_seed"):
                        caption_worker.caption({"path": str(source), "model_id": MODEL["id"],
                                                "variation_seed": invalid})
                server_type.assert_not_called()


if __name__ == "__main__":
    unittest.main()
