"""Real SDL trigger + title loop + recorder; original synthetic graphics only."""
import os
import json
from pathlib import Path
import subprocess
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
import scanout_format as fmt


def main():
    adapter, off, replay = sys.argv[1:]
    env = {k: v for k, v in os.environ.items() if not k.startswith(("HD2D_CAPTURE_", "TEST_"))}
    env.update(SDL_VIDEODRIVER="dummy", TEST_INTERACTIVE="1")
    env.pop("SNESRECOMP_LAYER_MASK", None)

    def run(exe, extra):
        result = subprocess.run([exe], env=env | extra, text=True, capture_output=True, timeout=60)
        assert result.returncode == 0, result.stdout + result.stderr
        return result

    with tempfile.TemporaryDirectory(prefix="hd2d-interactive-") as temporary:
        root = Path(temporary)
        baseline = run(off, {}).stdout
        for case in ("normal", "TEST_REJECT_ONCE", "TEST_COLLISION", "TEST_KEY_CONFLICT", "TEST_LIMIT"):
            directory = root / case
            config = {"HD2D_CAPTURE_DIR": str(directory), "HD2D_CAPTURE_INTERACTIVE": "1",
                      "HD2D_CAPTURE_SESSION": "0123456789abcdef0123456789abcdef"}
            extra = {} if case == "normal" else {case: "1"}
            result = run(adapter, config | extra)
            expected = run(off, extra if case != "TEST_COLLISION" else {}).stdout
            assert result.stdout == expected, f"capture changed PPU/CPU/DMA/surface: {case}"
            files = sorted(directory.glob("*/capture.x2scan"))
            count = {"normal": 2, "TEST_REJECT_ONCE": 1, "TEST_COLLISION": 1,
                     "TEST_KEY_CONFLICT": 0, "TEST_LIMIT": 64}[case]
            assert len(files) == count, (case, len(files), result.stderr)
            reports = [fmt.read(path) for path in files]
            assert len({(r["session"], r["sequence"]) for r in reports}) == count
            for path in files:
                checked = subprocess.run([replay, str(path)], text=True, capture_output=True)
                assert checked.returncode == 0, checked.stdout + checked.stderr
            if case == "normal":
                assert [r["draw"] for r in reports] == [2, 4]
                assert [r["sequence"] for r in reports] == [1, 2]
                assert [r["epoch"] for r in reports] == [7, 8]
                assert [r["guest_frame_bits"] for r in reports] == [42, 0]
                assert reports[0]["reference_sha256"] != reports[1]["reference_sha256"]
                for index, path in enumerate(files):
                    output = root / f"validation-{index}"
                    validation = subprocess.run([sys.executable,
                        str(Path(__file__).resolve().parents[2] / "tools/validate_capture.py"),
                        str(path), "--replay", replay, "--output", str(output)],
                        text=True, capture_output=True)
                    assert validation.returncode == 0, validation.stdout + validation.stderr
                    assert json.loads((output / "report.json").read_text())["metrics"]["different_pixels"] == 0
            if case in ("TEST_REJECT_ONCE", "TEST_COLLISION"):
                assert reports[0]["sequence"] == 2 and reports[0]["draw"] == 4
                assert ("mosaic: requires 0; observed 17" if case == "TEST_REJECT_ONCE" else "file-error") in result.stderr
            if case == "TEST_LIMIT":
                assert reports[-1]["sequence"] == 64 and "session capture limit reached" in result.stderr
            # An existing root is never reused, even with the same session ID.
            repeated = run(adapter, config)
            assert "NEW writable directory" in repeated.stderr and repeated.stdout == baseline
        for mode in ("0", "invalid"):
            bad = run(adapter, {"HD2D_CAPTURE_DIR": str(root / "invalid"),
                "HD2D_CAPTURE_INTERACTIVE": mode, "HD2D_CAPTURE_SESSION": "12"*16})
            assert "rejected" in bad.stderr and bad.stdout == baseline
        conflict = run(adapter, {"HD2D_CAPTURE_DIR": str(root / "mixed"),
            "HD2D_CAPTURE_INTERACTIVE": "1", "HD2D_CAPTURE_DRAW": "2",
            "HD2D_CAPTURE_SESSION": "12"*16})
        assert "rejected" in conflict.stderr and not (root / "mixed").exists()
    print("interactive multi-capture, identities, epoch changes, rejection recovery, limits and compiled-out isolation: PASS")


if __name__ == "__main__":
    main()
