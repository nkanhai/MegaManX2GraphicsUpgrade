"""Private-input checks use only original synthetic bytes, never a game ROM."""
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import zlib
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import build_capture_host as build
import capture_frame


class PrivateWorkflowTests(unittest.TestCase):
    def test_rom_identity_and_copier_header_errors(self):
        data = bytes(range(256))*128
        expected = {"rom_size": str(len(data)), "expected_sha256": hashlib.sha256(data).hexdigest(),
                    "expected_crc32": f"{zlib.crc32(data):08x}"}
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "original synthetic bytes.sfc"
            path.write_bytes(data)
            self.assertEqual(build.check_rom(path, expected)["sha256"], expected["expected_sha256"])
            path.write_bytes(bytes(512) + data)
            with self.assertRaisesRegex(ValueError, "copier header"):
                build.check_rom(path, expected)
            path.write_bytes(bytes(len(data)))
            with self.assertRaisesRegex(ValueError, "digest mismatch"):
                build.check_rom(path, expected)
            path.unlink()
            with self.assertRaisesRegex(ValueError, "not accessible"):
                build.check_rom(path, expected)

    def test_build_plan_is_rom_bound_and_isolated(self):
        workspace = Path("/private workspace")
        rom = Path("/private ROMs/My game.sfc")
        plan = build.commands(rom, workspace, "Darwin")
        self.assertEqual(plan[0][-3:], ["--detach", str(workspace / "source"), "HEAD"])
        self.assertIn(str(rom), plan[2])  # One argv element, not shell interpolation.
        self.assertIn("-DSNESRECOMP_SETUP_HOST=OFF", plan[3])
        self.assertIn("-DCMAKE_OSX_ARCHITECTURES=arm64", plan[3])
        self.assertIn("-DHD2D_SCANOUT_CAPTURE=ON", plan[3])
        self.assertNotIn("-DCMAKE_OSX_ARCHITECTURES=arm64", build.commands(rom, workspace, "Linux")[3])
        self.assertEqual(build.identity()["expected_sha256"], "f3246755f608a1e1dc9c848b61da3b824c7853b29b3be40df6fc7f2793a887ed")

    def test_preflight_missing_rom_does_not_execute_generation(self):
        with patch.object(build, "git", return_value=""), patch.object(build, "check_pins", return_value={}), \
             patch.object(build.shutil, "which", return_value="/tool"), \
             patch.object(build.subprocess, "run") as run:
            report = build.preflight(Path("/unavailable/rom.sfc"), build.ROOT / "hd2d/local/never-created-test")
        self.assertEqual(report["status"], "blocked")
        self.assertTrue(any("not accessible" in b for b in report["blockers"]))
        run.assert_not_called()
        self.assertFalse((build.ROOT / "hd2d/local/never-created-test").exists())

    def test_only_bounded_controller_scripts(self):
        capture_frame.check_script(b"# original controller input\nwait 10\npress right+y 6\nquit\n")
        capture_frame.check_script((build.ROOT / "hd2d/fixtures/input/intro-stage-candidate.txt").read_bytes())
        for script in (b"poke 00 01\nquit", b"loadstate 1\nquit", b"wait -1\nquit", b"press start 0\nquit",
                       b"press unknown 6\nquit", b"wait 9999999999\nquit", b"quit\npress start 1", b"wait 1"):
            with self.subTest(script=script), self.assertRaises(ValueError):
                capture_frame.check_script(script)

    def test_capture_plan_preserves_paths_and_does_not_launch(self):
        local = build.ROOT / "hd2d/local"
        local.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=local) as temp:
            root = Path(temp)
            script = root / "input with spaces.txt"
            script.write_text("wait 1\nquit\n")
            private = {"path": "/private/Mega Man X2.sfc"}
            with patch.object(capture_frame, "check_rom", return_value=private), \
                 patch.object(capture_frame.subprocess, "run") as run:
                report, inputs = capture_frame.plan(sys.executable, "private", root / "new", script, 9)
            run.assert_not_called()
            self.assertIn(private["path"], report["command"])
            self.assertEqual(report["environment"]["HD2D_CAPTURE_DRAW"], "9")
            self.assertEqual(report["input_script_sha256"], hashlib.sha256(inputs).hexdigest())
            self.assertFalse((root / "new").exists())

    def test_failed_or_timed_out_host_never_reports_fidelity(self):
        with tempfile.TemporaryDirectory() as temp:
            for name, code, timeout in (("failure", "raise SystemExit(7)", 2),
                                        ("timeout", "import time; time.sleep(1)", 0.02)):
                out = Path(temp) / name
                report = {"working_directory": str(out), "environment": {},
                          "command": [sys.executable, "-c", code]}
                self.assertEqual(capture_frame.execute(report, b"quit\n", timeout), 2)
                saved = json.loads((out / "run-report.json").read_text())
                self.assertEqual(saved["status"], "blocked-or-failed")
                self.assertNotIn("capture", saved)
                self.assertTrue((out / "capture.log").is_file())


if __name__ == "__main__":
    unittest.main()
