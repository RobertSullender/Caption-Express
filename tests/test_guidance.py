"""User notes are bounded, clip-relative hints without changing unguided requests."""

from io import StringIO
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "worker"))
from caption_worker import (USER_PROMPT, caption, guidance_for_window, guided_prompt,
                            main, parse_guidance, refine_observations,
                            render_user_prompt, unload_model, validate_guidance_duration,
                            validate_guidance_request)

MODEL = {"id": "test-model", "label": "Test model",
         "model_path": "/tmp/test-model.gguf", "projector_path": "/tmp/test-mmproj.gguf"}


class GuidanceTests(unittest.TestCase):
    def setUp(self):
        unload_model()

    def tearDown(self):
        unload_model()

    def test_user_spacing_multiple_ranges_and_clip_relative_overlap(self):
        cues = parse_guidance(
            "00:00- 00:05 a teenage girl crawls through the tunnel\n"
            "00:05–00:10 she turns toward a lamp\n"
            "00:10-00:12: low metallic contacts")
        self.assertEqual((0, 5, 10), tuple(cue.start for cue in cues))
        self.assertEqual((5, 10, 12), tuple(cue.end for cue in cues))
        self.assertEqual((cues[0],), guidance_for_window(cues, 0, 5))
        self.assertEqual((cues[0], cues[1]), guidance_for_window(cues, 4.9, 5.04))
        self.assertEqual((cues[0], cues[1]), guidance_for_window(cues, 0, 5.04))
        self.assertEqual((cues[1],), guidance_for_window(cues, 5, 8))
        self.assertEqual((), guidance_for_window(cues, 12, 14))
        validate_guidance_duration(parse_guidance("00:00-00:05 a girl crawls"), 5.04)

    def test_untimed_note_applies_to_whole_clip_and_timecode_accepts_hours(self):
        cues = parse_guidance("• A faint ambient hum\n- 01:02:03-01:02:07 speech starts")
        self.assertIsNone(cues[0].start)
        self.assertEqual("A faint ambient hum", cues[0].text)
        self.assertEqual(3723, cues[1].start)
        self.assertEqual((cues[0],), guidance_for_window(cues, 0, 8))
        self.assertEqual(cues, guidance_for_window(cues, 3724, 3725))

    def test_bad_range_is_rejected_instead_of_becoming_a_whole_clip_note(self):
        for value in ("00:10-00:05 the girl turns", "00:00-00:05",
                      "00:70-00:80 a sound", "00:00-05 the girl turns",
                      "00:35 speech begins", "00:35 to 00:40 speech",
                      "00:35. speech begins", "- 00:35 speech begins"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                parse_guidance(value)
        with self.assertRaisesRegex(ValueError, "guidance_text must be text"):
            parse_guidance(["a note"])
        with self.assertRaisesRegex(ValueError, "limited"):
            parse_guidance("a" * 4001)

    def test_guidance_is_separate_from_default_prompt_and_disputed_claims_are_omitted(self):
        ordinary = render_user_prompt(USER_PROMPT, False, "video")
        self.assertEqual(ordinary, guided_prompt(ordinary, (), 0, 5, "video"))
        cues = parse_guidance("00:00-00:05 visible crawling and audible breathing")
        visual = guided_prompt(ordinary, cues, 0, 5, "video")
        audio = guided_prompt("Describe sound.", cues, 0, 5, "soundtrack")
        later = guided_prompt(ordinary, cues, 5, 10, "video")
        self.assertIn("human annotations", visual)
        self.assertIn("Ignore sound claims in this pass", visual)
        self.assertIn("Ignore visual claims in this pass", audio)
        self.assertIn("If a note directly conflicts", visual)
        self.assertIn("Do not add details, timing, causes", visual)
        self.assertEqual(ordinary, later)
        refined = guided_prompt("Merge independent observations.", cues, 0, 5, "refine")
        self.assertIn("Account for every relevant user cue", refined)
        self.assertIn("Keep the relative sequence and time range", refined)
        self.assertIn("named sound categories", refined)
        self.assertIn("use the independently observed object identity", refined)
        self.assertIn("A missing or less detailed model observation is not a contradiction", refined)
        self.assertIn("prefer that independently heard category", refined)

    def test_out_of_clip_timed_note_fails_before_model_runs(self):
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / "clip.mp4"
            source.touch()
            with mock.patch("caption_worker.probe_media", return_value=(5.04, True, True)), \
                 mock.patch("caption_worker.discover_models", return_value=[MODEL]), \
                 mock.patch("caption_worker.GemmaServer") as server_type:
                with self.assertRaisesRegex(ValueError, "outside the 5.04s exported clip"):
                    caption({"path": str(source), "model_id": "test-model",
                             "guidance_text": "00:10-00:15 a car appears"})
                server_type.assert_not_called()

    def test_validation_command_returns_complete_or_error_without_model_load(self):
        good = {"command": "validate_guidance", "duration": 5.04,
                "guidance_text": "00:00- 00:05 crawling\nA faint hum"}
        bad = {"command": "validate_guidance", "duration": 5.04,
               "guidance_text": "00:35 speech begins"}
        requests = StringIO(json.dumps(good) + "\n" + json.dumps(bad) + "\n")
        with mock.patch("caption_worker.sys.stdin", requests), \
             mock.patch("caption_worker.model_status", return_value={}), \
             mock.patch("caption_worker.GemmaServer") as gemma, \
             mock.patch("caption_worker.discover_models") as discover, \
             mock.patch("caption_worker.emit") as emit, \
             mock.patch("caption_worker.traceback.print_exc"):
            main()
        self.assertEqual(["ready", "complete", "error"],
                         [call.args[0] for call in emit.call_args_list])
        self.assertTrue(emit.call_args_list[1].kwargs["guidance_valid"])
        self.assertIn("Start a timed guidance line with a range",
                      emit.call_args_list[2].kwargs["message"])
        gemma.assert_not_called()
        discover.assert_not_called()

    def test_validation_command_checks_every_timecoded_start_and_duration(self):
        with mock.patch("caption_worker.emit") as emit:
            validate_guidance_request({"duration": 5.04,
                                       "guidance_text": "00:00-00:05 crawling"})
            self.assertEqual("complete", emit.call_args.args[0])
        with self.assertRaisesRegex(ValueError, "outside the 5.04s exported clip"):
            validate_guidance_request({"duration": 5.04,
                                       "guidance_text": "00:00-00:05 crawling\n"
                                                        "00:06-00:08 talking"})
        for duration in (None, 0, -1, True, "5.04", float("nan")):
            with self.subTest(duration=duration), self.assertRaisesRegex(ValueError, "duration"):
                validate_guidance_request({"duration": duration, "guidance_text": "A hum"})

    def test_refinement_receives_only_notes_for_each_window(self):
        server = mock.Mock()
        server.infer.side_effect = ["First interval.", "Second interval."]
        cues = parse_guidance("00:01-00:03 crawling\n00:26-00:29 a low hum")
        refine_observations(server, None, USER_PROMPT,
                            [(0, 8, "A figure appears."), (25, 30, "The figure exits.")],
                            [(0, 25, "A sound is heard."), (25, 30, "The sound changes.")],
                            [(0, 25), (25, 30)], guidance=cues)
        first = server.infer.call_args_list[0].args[0][0]["text"]
        second = server.infer.call_args_list[1].args[0][0]["text"]
        self.assertIn("crawling", first)
        self.assertNotIn("a low hum", first)
        self.assertIn("a low hum", second)
        self.assertNotIn("crawling", second)
        self.assertIn("Account for every relevant user cue", first)

    def test_guided_combined_caption_keeps_both_inference_passes_unprimed(self):
        self._caption_request("00:00- 00:05 a girl crawls while breathing")

    def test_empty_guidance_preserves_two_pass_default_without_final_synthesis(self):
        self._caption_request(None)

    def test_video_only_guidance_still_reaches_visual_inference(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            source = folder / "video.mp4"
            source.touch()
            with mock.patch("caption_worker.probe_media", return_value=(5, True, False)), \
                 mock.patch("caption_worker.discover_models", return_value=[MODEL]), \
                 mock.patch("caption_worker.GemmaServer") as server_type, \
                 mock.patch("caption_worker.make_content", return_value=([{"type": "text", "text": "frames"}], False)) as make_content, \
                 mock.patch("caption_worker.emit"), \
                 mock.patch("caption_worker.TEMP_DIR", folder):
                server_type.return_value.infer.return_value = "A girl crawls."
                caption({"path": str(source), "model_id": "test-model",
                         "guidance_text": "00:00-00:05 a girl crawls"})
                self.assertEqual(1, server_type.return_value.infer.call_count)
                self.assertEqual("a girl crawls", make_content.call_args.kwargs["guidance"][0].text)

    def _caption_request(self, guidance_text):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            source = folder / "clip.mp4"
            source.touch()
            with mock.patch("caption_worker.probe_media", return_value=(5.04, True, True)), \
                 mock.patch("caption_worker.discover_models", return_value=[MODEL]), \
                 mock.patch("caption_worker.GemmaServer") as server_type, \
                 mock.patch("caption_worker.make_content") as make_content, \
                 mock.patch("caption_worker.emit") as emit, \
                 mock.patch("caption_worker.TEMP_DIR", folder):
                make_content.side_effect = lambda *args, **kwargs: (
                    [{"type": "text", "text": "sampled content"}], args[7])
                server_type.return_value.infer.side_effect = [
                    "A girl crawls.", "Breathing and contact sounds.",
                    "A girl crawls while breathing and metal contact sounds are heard."]
                caption({"path": str(source), "model_id": "test-model",
                         "guidance_text": guidance_text})
                calls = make_content.call_args_list
                self.assertEqual(2, len(calls))
                self.assertEqual((), calls[0].kwargs["guidance"])
                self.assertEqual((), calls[1].kwargs["guidance"])
                self.assertEqual(3 if guidance_text else 2,
                                 server_type.return_value.infer.call_count)
                if guidance_text:
                    final_prompt = server_type.return_value.infer.call_args_list[-1].args[0][0]["text"]
                    self.assertIn("a girl crawls while breathing", final_prompt)
                    self.assertTrue(emit.call_args.kwargs["refinement_applied"])
                else:
                    self.assertFalse(emit.call_args.kwargs["refinement_applied"])


if __name__ == "__main__":
    unittest.main()
