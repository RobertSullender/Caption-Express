"""Check local Gemma GGUF/projector pairing without loading model weights."""

import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "worker"))
from caption_worker import USER_PROMPT, discover_models, final_caption, frame_times, render_user_prompt


class ModelCatalogTests(unittest.TestCase):
    def test_user_prompt_controls_later_section_instruction(self):
        first = render_user_prompt(USER_PROMPT, False)
        later = render_user_prompt(USER_PROMPT, True)
        self.assertNotIn("This excerpt begins later", first)
        self.assertIn("This excerpt begins later", later)
        self.assertIn("new visible action", later)
        self.assertNotIn("sound changes", later)
        self.assertIn("A blue screen.", render_user_prompt(
            USER_PROMPT, True, previous_caption="A blue screen."))
        self.assertEqual("Describe motion.", render_user_prompt("Describe motion.", True))
        self.assertEqual("Describe motion. Follow the subject.", render_user_prompt(
            "Describe motion. [[later: Follow the subject.]]", True))
        self.assertNotIn("audio only", first)
        audio_only = render_user_prompt(USER_PROMPT, False, "audio_only")
        self.assertIn("Describe the audible sounds", audio_only)
        self.assertNotIn("visible subjects", audio_only)
        soundtrack = render_user_prompt(USER_PROMPT, False, "soundtrack")
        self.assertEqual(audio_only, soundtrack)
        self.assertIn("one or two concise factual sentences", soundtrack)
        self.assertNotIn("visible shot", soundtrack)
        later_audio = render_user_prompt(USER_PROMPT, True, "audio_only", "A motor hums.")
        self.assertIn("preceding audio caption", later_audio)
        self.assertIn("A motor hums.", later_audio)
        self.assertNotIn("scene transitions", later_audio)
        self.assertNotIn("unchanged camera", later_audio)
        self.assertNotIn("Describe the audible sounds", first)

    def test_frame_sampling_is_bounded_and_reaches_near_the_end(self):
        short = frame_times(0, 5.04)
        long = frame_times(32, 40)
        self.assertEqual(11, len(short))
        self.assertAlmostEqual(5.0, short[-1])
        self.assertEqual(16, len(long))
        self.assertAlmostEqual(39.96, long[-1])

    def test_only_final_caption_is_kept_when_thinking_is_returned_inline(self):
        result = final_caption("<|channel>thought\nI should inspect the frame.<channel|>A blue frame remains still.")
        self.assertEqual("A blue frame remains still.", result)
        with self.assertRaises(RuntimeError):
            final_caption("<|channel>thought\nunfinished thought")

    def test_e2b_quantizations_share_only_the_matching_projector(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            e2b = root / "gemma-4-E2B-it-GGUF"
            e2b.mkdir()
            for name in ("gemma-4-E2B-it-Q8_0.gguf", "gemma-4-E2B-it-Q4_K_M.gguf",
                         "mmproj-gemma-4-E2B-it-BF16.gguf"):
                (e2b / name).write_bytes(b"GGUFtest")
            (e2b / "gemma-4-E2B-other-Q4_K_M.gguf").write_bytes(b"GGUFtest")
            (e2b / "gemma4_e2b_it_int8_convrot.safetensors").write_bytes(b"test")

            models = discover_models((root,))

            self.assertEqual(2, len(models))
            self.assertEqual({"E2B"}, {model["family"] for model in models})
            self.assertEqual({str(e2b / "mmproj-gemma-4-E2B-it-BF16.gguf")},
                             {model["projector_path"] for model in models})

    def test_stock_e4b_precedes_hauhau_while_both_remain_available(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for base in ("gemma-4-E4B-it", "Gemma-4-E4B-Uncensored-HauhauCS-Aggressive"):
                (root / f"{base}-Q8_0.gguf").write_bytes(b"GGUFtest")
                (root / f"mmproj-{base}-f16.gguf").write_bytes(b"GGUFtest")

            models = discover_models((root,))

            self.assertEqual(2, len(models))
            self.assertIn("gemma-4-E4B-it", models[0]["id"])
            self.assertIn("HauhauCS", models[1]["id"])

    def test_uncensored_e2b_gguf_requires_its_own_matching_projector(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            stock = "gemma-4-E2B-it"
            uncensored = "Gemma-4-E2B-Uncensored-HauhauCS"
            for base in (stock, uncensored):
                (root / f"{base}-Q4_K_M.gguf").write_bytes(b"GGUFtest")
            (root / f"mmproj-{stock}-BF16.gguf").write_bytes(b"GGUFtest")
            models_without_pair = discover_models((root,))
            self.assertEqual([stock], [Path(model["id"]).stem.removesuffix("-Q4_K_M")
                                       for model in models_without_pair])

            (root / f"mmproj-{uncensored}-f16.gguf").write_bytes(b"GGUFtest")
            models = discover_models((root,))
            self.assertEqual(2, len(models))
            self.assertEqual({"E2B"}, {model["family"] for model in models})
            self.assertIn(uncensored, models[1]["id"])
            self.assertIn(uncensored, models[1]["projector_path"])


if __name__ == "__main__":
    unittest.main()
