"""Launch planning/failures with synthetic executables and mocked ROM identity."""
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import interactive_capture as interactive


class InteractiveWorkflowTests(unittest.TestCase):
    def test_plans_use_unique_private_sessions_and_no_input_script(self):
        with tempfile.TemporaryDirectory() as temporary:
            exe = Path(temporary) / "synthetic executable"
            exe.write_bytes(interactive.MARKER)
            exe.chmod(0o700)
            with patch.object(interactive, "check_rom", return_value={"path": "/private/Game with spaces.sfc"}):
                first, second = interactive.plan(exe, "private"), interactive.plan(exe, "private")
            self.assertNotEqual(first["session"], second["session"])
            self.assertNotEqual(first["working_directory"], second["working_directory"])
            self.assertFalse(Path(first["working_directory"]).exists())
            self.assertEqual(first["command"][1:4], ["--no-launcher", "--rom", "/private/Game with spaces.sfc"])
            self.assertNotIn("--script", first["command"])
            self.assertNotIn("HD2D_CAPTURE_DRAW", first["environment"])
            self.assertEqual(first["environment"]["HD2D_CAPTURE_INTERACTIVE"], "1")

    def test_old_or_capture_disabled_executable_is_rejected(self):
        with patch.object(interactive, "check_rom", return_value={"path": "private"}):
            with self.assertRaisesRegex(ValueError, "interactive capture marker"):
                interactive.plan(sys.executable, "private")

    def test_launch_preserves_keyboard_mode_and_filters_overrides(self):
        local = interactive.ROOT / "hd2d/local"
        local.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=local) as temporary:
            output = Path(temporary) / "new"
            report = {"working_directory": str(output), "command": [sys.executable, "-c", "pass"],
                      "environment": {"HD2D_CAPTURE_INTERACTIVE": "1"}, "shortcut": "test"}
            def launch(command, *, cwd, env):
                self.assertEqual(command, report["command"])
                self.assertEqual(cwd, output)
                self.assertNotIn("HD2D_CAPTURE_DRAW", env)
                self.assertNotIn("SNESRECOMP_RUN_FRAMES", env)
                self.assertNotIn("MMX_REPLAY_FAST", env)
                self.assertIn("EnableAudio = 1", (output / "config.ini").read_text())
                self.assertEqual(output.stat().st_mode & 0o777, 0o700)
                return interactive.subprocess.CompletedProcess(command, 0)
            with patch.dict(os.environ, {"HD2D_CAPTURE_DRAW": "1", "SNESRECOMP_RUN_FRAMES": "1", "MMX_REPLAY_FAST": "1"}), \
                 patch.object(interactive.subprocess, "run", side_effect=launch) as run:
                self.assertEqual(interactive.execute(report), 0)
                run.assert_called_once()
            self.assertEqual(json.loads((output / "session.json").read_text())["status"], "closed-no-captures")
            with self.assertRaises(FileExistsError):
                interactive.execute(report)

    def test_failed_launch_has_no_fidelity_claim(self):
        local = interactive.ROOT / "hd2d/local"
        local.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=local) as temporary:
            report = {"working_directory": str(Path(temporary) / "new"), "command": ["missing"],
                      "environment": {}, "shortcut": "test"}
            with patch.object(interactive.subprocess, "run", side_effect=OSError("missing executable")):
                self.assertEqual(interactive.execute(report), 2)
            self.assertEqual(report["status"], "launch-failed")
            self.assertNotIn("fidelity", report)


if __name__ == "__main__":
    unittest.main()
