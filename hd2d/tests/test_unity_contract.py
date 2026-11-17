"""Independent checks that the managed reader follows the native wire contract."""
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

HD2D = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HD2D / "tools"))
import make_unity_fixtures


class UnityContractTests(unittest.TestCase):
    def test_managed_wire_fields_match_native_order_and_types(self):
        fields = re.findall(r"HD2D_FIELD\((\w+), ([^)]+)\)", (HD2D / "bridge/fields.inc").read_text())
        managed = (HD2D / "unity/Assets/X2HD2D/Core/WireFields.cs").read_text()
        names, types = managed.split("internal static readonly string[] Types")
        self.assertEqual(re.findall(r'"([^"]+)"', names), [name for _, name in fields])
        self.assertEqual(re.findall(r'"([^"]+)"', types), [kind for kind, _ in fields])

    def test_private_unity_material_and_generated_data_are_ignored(self):
        paths = ["hd2d/unity/Assets/accidental.x2scan", "hd2d/unity/Assets/accidental.x2scan.meta",
                 "hd2d/unity/Assets/LocalGenerated/DiagnosticURP.asset", "hd2d/unity/Library/test",
                 "hd2d/local/unity-comparisons/test/reference.png", "hd2d/tests/unity/obj/cache"]
        result = subprocess.run(["git", "check-ignore", "--stdin"], cwd=HD2D.parent,
                                input="\n".join(paths)+"\n", text=True, capture_output=True, check=True)
        self.assertEqual(result.stdout.splitlines(), paths)

    def test_fixtures_refuse_game_capture_and_nonlocal_output(self):
        with tempfile.TemporaryDirectory() as temp:
            source = Path(temp) / "input"; source.write_bytes(b"synthetic test placeholder")
            with self.assertRaisesRegex(ValueError, "ignored hd2d/local"):
                make_unity_fixtures.generate(source, Path("unused"), Path(temp) / "output")
            output = HD2D / "local" / "must-not-create-fixtures"
            with patch.object(make_unity_fixtures.fmt, "decode", return_value={"source": "title-runtime"}):
                with self.assertRaisesRegex(ValueError, "only original synthetic"):
                    make_unity_fixtures.generate(source, Path("unused"), output)
            self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
