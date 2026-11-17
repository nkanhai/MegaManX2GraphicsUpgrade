"""Contract tests use independently specified SNES layout facts, never a ROM."""
import base64
import copy
import importlib.util
import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest

TOOL = Path(__file__).resolve().parents[1] / "tools/frame_format.py"
spec = importlib.util.spec_from_file_location("frame_format", TOOL)
fmt = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fmt)


class FrameFormatTests(unittest.TestCase):
    def setUp(self):
        self.frame = fmt.synthetic_frame()

    def test_round_trip_preserves_every_byte_and_register(self):
        encoded = fmt.dumps(self.frame)
        decoded = fmt.loads(encoded)
        self.assertEqual(self.frame, decoded)
        self.assertEqual(encoded, fmt.dumps(decoded))
        self.assertLess(len(encoded), fmt.MAX_DOCUMENT_BYTES)
        self.assertEqual(fmt.validate(self.frame), fmt.validate(decoded))

    def test_known_tilemap_and_planar_pixels(self):
        memory = fmt.validate(self.frame)
        vram = memory["vram"]
        self.assertEqual(struct.unpack_from("<HH", vram, 0x4000), (1, 0x4001))
        self.assertEqual(struct.unpack_from("<H", vram, 0x4800)[0], 0x2401)
        # Independent 4-plane decoder: row 0 of tile 1 must be 1,2,... .
        tile = vram[32:64]
        pixels = [sum(((tile[(plane // 2) * 16 + plane % 2] >> (7 - x)) & 1) << plane
                      for plane in range(4)) for x in range(8)]
        self.assertEqual(pixels, [1, 2] * 4)
        self.assertEqual(vram[0x2020:0x2024], bytes([0xAA, 0xAA, 0x55, 0x55]))
        self.assertEqual(self.frame["ppu"]["hScroll"][:2], [3, 11])
        self.assertEqual(self.frame["ppu"]["vScroll"][:2], [5, 7])

    def test_known_palette_and_oam_layout(self):
        memory = fmt.validate(self.frame)
        self.assertEqual(memory["cgram"][2:6], bytes([0x1F, 0, 0xE0, 3]))
        self.assertEqual(struct.unpack_from("<H", memory["cgram"], 38)[0], 0x7C00)
        oam = memory["oam"]
        self.assertEqual(oam[:8], bytes([24, 32, 1, 0x30, 32, 32, 1, 0x70]))
        x = oam[8] | (((oam[512] >> 4) & 1) << 8)
        self.assertEqual(x - 512 if x & 256 else x, -4)
        self.assertEqual(oam[11] & 0xC0, 0x80)  # vertical flip
        self.assertEqual(oam[13], 240)  # unused hardware slot, not an entity

    def test_reject_corruption_even_with_correct_base64(self):
        block = self.frame["memory"]["vram"]
        raw = bytearray(base64.b64decode(block["data"]))
        raw[32] ^= 1
        block["data"] = base64.b64encode(raw).decode("ascii")
        with self.assertRaisesRegex(ValueError, "checksum"):
            fmt.validate(self.frame)

    def test_reject_bad_memory_lengths_and_encoding(self):
        for key, value in (("byte_length", 543), ("encoding", "hex"),
                           ("data", "?" * 728), ("data", "")):
            frame = copy.deepcopy(self.frame)
            frame["memory"]["oam"][key] = value
            with self.subTest(key=key, value=str(value)[:20]), self.assertRaises(ValueError):
                fmt.validate(frame)

    def test_reject_versions_types_and_unknown_fields(self):
        for key, value in (("version", 2), ("version", True), ("sequence", -1),
                           ("sequence", 1.0), ("sequence", 2**53), ("format", "other"),
                           ("source", "runtime"), ("future", 1)):
            frame = copy.deepcopy(self.frame)
            frame[key] = value
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                fmt.validate(frame)
        del self.frame["ppu"]["hScroll"]
        with self.assertRaises(ValueError):
            fmt.validate(self.frame)

    def test_reject_unsupported_features_and_invalid_registers(self):
        for key, value in (("bgmode", 7), ("mosaic", 16), ("cgadsub", 1),
                           ("screenEnabled", [0x13, 1]), ("hScroll", [0]),
                           ("hScroll", [0, 0, 0, 65536]), ("inidisp", True),
                           ("setini", 1), ("screenWindowed", [1, 0])):
            frame = copy.deepcopy(self.frame)
            frame["ppu"][key] = value
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                fmt.validate(frame)

    def test_reject_non_rgb15_palette(self):
        palette = bytearray(512)
        palette[1] = 0x80
        self.frame["memory"]["cgram"] = fmt.pack_memory(palette)
        with self.assertRaisesRegex(ValueError, "RGB15"):
            fmt.validate(self.frame)

    def test_reject_malformed_duplicate_oversized_json(self):
        encoded = fmt.dumps(self.frame)
        duplicate = encoded.replace(b'"version":1', b'"version":1,"version":1')
        for data in (b'\xff', b'{', b'[]', encoded[:-2], duplicate,
                     b' ' * (fmt.MAX_DOCUMENT_BYTES + 1)):
            with self.subTest(length=len(data)), self.assertRaises(ValueError):
                fmt.loads(data)

    def test_cli_demo_validate_and_no_overwrite(self):
        with tempfile.TemporaryDirectory() as folder:
            target = Path(folder) / "nested/demo.json"
            demo = subprocess.run([sys.executable, TOOL, "demo", target],
                                  capture_output=True, text=True, check=True)
            check = subprocess.run([sys.executable, TOOL, "validate", target],
                                   capture_output=True, text=True, check=True)
            self.assertEqual(json.loads(demo.stdout), json.loads(check.stdout))
            old = target.read_bytes()
            duplicate = subprocess.run([sys.executable, TOOL, "demo", target],
                                       capture_output=True, text=True)
            self.assertNotEqual(duplicate.returncode, 0)
            self.assertEqual(old, target.read_bytes())


if __name__ == "__main__":
    unittest.main()
