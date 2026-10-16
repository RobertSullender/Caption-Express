"""Check time-bounded synthesis without running local models."""

from pathlib import Path
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "worker"))
from caption_worker import (USER_PROMPT, qualify_section_end, refine_observations,
                            render_user_prompt)


class RefinementTests(unittest.TestCase):
    def test_nonfinal_section_cannot_claim_full_clip_end(self):
        note = "The pose remains until the end of the clip at 7.96s."
        self.assertEqual("The pose remains until the end of this section at 7.96s.",
                         qualify_section_end(note, False))
        self.assertEqual(note, qualify_section_end(note, True))

    def test_refinement_prompt_stays_out_of_visual_and_audio_passes(self):
        self.assertNotIn("Write one natural, flowing caption", render_user_prompt(USER_PROMPT, False, "video"))
        self.assertNotIn("Write one natural, flowing caption", render_user_prompt(USER_PROMPT, False, "soundtrack"))
        refined = render_user_prompt(USER_PROMPT, False, "refine")
        self.assertIn("Write one natural, flowing caption", refined)
        self.assertIn("exact onset, duration", refined)
        self.assertIn("independent sentences", refined)
        self.assertNotIn("This excerpt begins later", refined)
        self.assertNotIn("Describe the audible sounds", refined)

    def test_refinement_uses_separate_time_labeled_evidence_per_audio_window(self):
        server = mock.Mock()
        server.infer.side_effect = ["A person enters while music plays.",
                                    "She continues walking as the sound fades."]
        result = refine_observations(
            server, None, USER_PROMPT,
            [(0, 8, "A person enters."), (24, 32, "She continues walking.")],
            [(0, 25, "Music plays."), (25, 35, "Music fades.")],
            [(0, 25), (25, 35)],
        )
        self.assertEqual("A person enters while music plays. She continues walking as the sound fades.",
                         result)
        self.assertEqual(2, server.infer.call_count)
        first = server.infer.call_args_list[0].args[0]
        second = server.infer.call_args_list[1].args[0]
        self.assertEqual(["text"], [part["type"] for part in first])
        self.assertIn("[0.0–8.0s] A person enters.", first[0]["text"])
        self.assertIn("[0.0–25.0s] Music plays.", first[0]["text"])
        self.assertNotIn("Music fades.", first[0]["text"])
        self.assertIn("[24.0–32.0s] She continues walking.", second[0]["text"])
        self.assertIn("[25.0–35.0s] Music fades.", second[0]["text"])
        self.assertIn("Previous refined passage", second[0]["text"])

    def test_refinement_reruns_use_distinct_reproducible_seeds(self):
        server = mock.Mock()
        server.infer.return_value = "A concise caption."
        inputs = (server, None, USER_PROMPT,
                  [(0, 8, "A person enters."), (25, 32, "She turns.")],
                  [(0, 25, "Music plays."), (25, 35, "Music fades.")],
                  [(0, 25), (25, 35)])
        refine_observations(*inputs)
        self.assertEqual([None, None], [call.args[2] for call in server.infer.call_args_list])
        server.reset_mock()
        refine_observations(*inputs, variation_seed=123)
        self.assertEqual([200_123, 200_124],
                         [call.args[2] for call in server.infer.call_args_list])


if __name__ == "__main__":
    unittest.main()
