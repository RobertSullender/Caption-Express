"""Exercise the real FFmpeg paths for visual and soundtrack evidence."""

import base64
from array import array
from io import BytesIO
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import wave

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "worker"))
from caption_worker import (caption, clean_caption, final_caption, make_content, normalize_quiet_audio,
                            remove_audio_absence_claims, remove_audio_input_refusal,
                            unload_model, wav_is_digital_silence)


@unittest.skipUnless(shutil.which("ffmpeg"), "FFmpeg is needed for media extraction")
class CaptionContentTests(unittest.TestCase):
    def test_removed_video_preamble_keeps_initial_capital(self):
        self.assertEqual("A red apple stays still.",
                         clean_caption("The video begins with a red apple stays still."))

    def test_inference_meta_trailer_is_not_saved_as_caption(self):
        answer = "The soundtrack has a low hum and several sharp contacts. " \
                 "3. Final Review against Constraints: Check the format. 4. Generate Output."
        self.assertEqual("The soundtrack has a low hum and several sharp contacts.",
                         final_caption(answer))

    def test_audio_caption_drops_unverified_absence_list(self):
        caption = "The soundtrack has a low hum and sharp contacts. " \
                  "There are no transient sounds or voices."
        self.assertEqual("The soundtrack has a low hum and sharp contacts.",
                         remove_audio_absence_claims(caption))

    def test_audio_caption_retains_positive_clause_after_absence(self):
        self.assertEqual("A metal clang follows.",
                         remove_audio_absence_claims("There is no speech, but a metal clang follows."))

    def test_attached_audio_refusal_is_removed_and_flagged(self):
        caption, refused = remove_audio_input_refusal(
            "A low hum is audible. I cannot describe the rest because no recording was provided. "
            "Please upload an audio file.")
        self.assertTrue(refused)
        self.assertEqual("A low hum is audible.", caption)
        self.assertEqual(("", True), remove_audio_input_refusal(
            "I need the audio to describe the sounds."))

    def test_silent_audio_only_file_completes(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            source = folder / "silent.flac"
            subprocess.run([
                "ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono",
                "-t", "1", "-c:a", "flac", str(source),
            ], check=True, capture_output=True)
            model = {"id": "test", "label": "test", "model_path": "/tmp/test.gguf",
                     "projector_path": "/tmp/test-mmproj.gguf"}
            self.addCleanup(unload_model)
            with mock.patch("caption_worker.discover_models", return_value=[model]), \
                 mock.patch("caption_worker.GemmaServer"), \
                 mock.patch("caption_worker.emit") as emit, \
                 mock.patch("caption_worker.TEMP_DIR", folder):
                caption({"path": str(source), "model_id": "test"})
            self.assertEqual("No discernible sound.", emit.call_args.kwargs["caption"])
            self.assertEqual("complete", emit.call_args.args[0])


    def test_codec_residue_is_silent_but_one_clear_click_is_not(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "noise.wav"
            samples = array("h", [2, -2] * 8000)
            def save():
                with wave.open(str(path), "wb") as audio:
                    audio.setnchannels(1)
                    audio.setsampwidth(2)
                    audio.setframerate(16000)
                    audio.writeframes(samples.tobytes())
            save()
            self.assertTrue(wav_is_digital_silence(path))
            samples[8000] = 1000
            save()
            self.assertFalse(wav_is_digital_silence(path))

    def test_quiet_audio_is_lifted_without_clipping(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "quiet.wav"
            with wave.open(str(path), "wb") as audio:
                audio.setnchannels(1)
                audio.setsampwidth(2)
                audio.setframerate(16000)
                audio.writeframes(array("h", [1000, -1000] * 100).tobytes())

            normalize_quiet_audio(path)

            with wave.open(str(path), "rb") as audio:
                self.assertEqual(16000, audio.getframerate())
                samples = array("h")
                samples.frombytes(audio.readframes(audio.getnframes()))
            self.assertGreater(max(samples), 1000)
            self.assertLess(max(abs(sample) for sample in samples), 32768)

    def test_video_and_soundtrack_passes_use_the_same_export(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            source = folder / "source.mp4"
            subprocess.run([
                "ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-f", "lavfi", "-i", "color=c=blue:s=64x64:r=24:d=1",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=16000:duration=1",
                "-c:v", "libx264", "-c:a", "aac", "-shortest", str(source),
            ], check=True, capture_output=True)

            video_content, video_has_audio = make_content(
                source, 0, 1, folder, "[[video: Describe the motion.]] "
                "[[soundtrack: Describe the sound.]]", False, True, False)
            audio_content, audio_attached = make_content(
                source, 0, 1, folder, "[[video: Describe the motion.]] "
                "[[soundtrack: Describe the sound.]]", False, False, True,
                prompt_mode="soundtrack")

            self.assertFalse(video_has_audio)
            self.assertTrue(audio_attached)
            self.assertEqual(["image_url", "text", "image_url", "text", "text"],
                             [part["type"] for part in video_content])
            self.assertEqual("Describe the motion.", video_content[-1]["text"])
            self.assertEqual(["input_audio", "text"], [part["type"] for part in audio_content])
            self.assertEqual("Describe the sound.", audio_content[-1]["text"])
            self.assertTrue(base64.b64decode(video_content[0]["image_url"]["url"].split(",", 1)[1]).startswith(b"\xff\xd8"))
            with wave.open(BytesIO(base64.b64decode(audio_content[0]["input_audio"]["data"]))) as audio:
                self.assertEqual(16000, audio.getframerate())
                self.assertEqual(1, audio.getnchannels())

    def test_digital_silence_is_not_sent_as_soundtrack(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            source = folder / "silent.mp4"
            subprocess.run([
                "ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-f", "lavfi", "-i", "color=c=blue:s=64x64:r=24:d=1",
                "-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono",
                "-t", "1", "-c:v", "libx264", "-c:a", "aac", str(source),
            ], check=True, capture_output=True)

            content, audio_attached = make_content(
                source, 0, 1, folder, "[[video: Describe the image.]] "
                "[[soundtrack: Describe the sound.]]", False, True, True)

            self.assertFalse(audio_attached)
            self.assertNotIn("input_audio", [part["type"] for part in content])
            self.assertEqual("Describe the image.", content[-1]["text"])
            self.assertTrue(wav_is_digital_silence(folder / "audio.wav"))


if __name__ == "__main__":
    unittest.main()
