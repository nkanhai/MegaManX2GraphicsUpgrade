"""End-to-end PNG/report validation using real native replay, not a mock."""
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import zlib
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
import scanout_format as fmt
from validate_capture import validate


def pixels(path):
    data = path.read_bytes()
    assert data[:8] == b"\x89PNG\r\n\x1a\n"
    pos, compressed = 8, bytearray()
    while pos < len(data):
        size = struct.unpack_from(">I", data, pos)[0]
        kind, payload = data[pos+4:pos+8], data[pos+8:pos+8+size]
        assert zlib.crc32(kind + payload) == struct.unpack_from(">I", data, pos+8+size)[0]
        if kind == b"IHDR":
            assert struct.unpack(">IIBBBBB", payload) == (256, 224, 8, 6, 0, 0, 0)
        if kind == b"IDAT":
            compressed.extend(payload)
        pos += 12 + size
    decoded = zlib.decompress(compressed)
    assert len(decoded) == 224 * 1025
    assert all(decoded[y*1025] == 0 for y in range(224))
    return b"".join(decoded[y*1025+1:(y+1)*1025] for y in range(224))


def main():
    capture_tool, replay = sys.argv[1:]
    with tempfile.TemporaryDirectory(prefix="hd2d-validation-") as tmp:
        root = Path(tmp)
        subprocess.run([capture_tool, str(root / "capture")], check=True)
        source = root / "capture/capture.x2scan"
        data = source.read_bytes()
        assert validate(source, replay, root / "match") == 0
        report = json.loads((root / "match/report.json").read_text())
        assert report["status"] == "verified" and report["metrics"]["different_pixels"] == 0
        expected = data[fmt.REFERENCE:fmt.MEMORY]
        assert pixels(root / "match/reference.png") == expected
        assert pixels(root / "match/reconstruction.png") == expected
        assert pixels(root / "match/difference.png") == bytes((0, 0, 0, 255))*57344
        # A wrong, re-hashed reference remains structurally valid. Preserve the
        # independently reconstructed image and identify exactly the changed pixel.
        changed = bytearray(data)
        offset = (100*256 + 17)*4
        changed[fmt.REFERENCE+offset] ^= 7
        changed[-32:] = hashlib.sha256(changed[:-32]).digest()
        source.write_bytes(changed)
        assert validate(source, replay, root / "mismatch") == 1
        report = json.loads((root / "mismatch/report.json").read_text())
        assert report["status"] == "mismatch"
        assert report["metrics"]["different_pixels"] == 1
        assert report["metrics"]["difference_bbox_inclusive"] == [17, 100, 17, 100]
        assert pixels(root / "mismatch/reconstruction.png") == expected
        assert pixels(root / "mismatch/difference.png")[offset:offset+4] == bytes((255, 0, 255, 255))
        raw = root / "native.rgba"
        result = subprocess.run([replay, str(source), "--rgba-output", str(raw)])
        assert result.returncode == 1 and raw.read_bytes() == expected
        result = subprocess.run([replay, str(source), "--rgba-output", str(raw)])
        assert result.returncode == 2 and raw.read_bytes() == expected
        # Missing/corrupt input or absent replay is an error, never 'zero diff'.
        source.write_bytes(data[:-1])
        assert validate(source, replay, root / "invalid") == 2
        assert not list((root / "invalid").glob("*.png"))
        source.write_bytes(data)
        assert validate(source, root / "no-tool", root / "no-tool-report") == 2
        assert not list((root / "no-tool-report").glob("*.png"))
        try:
            validate(source, replay, root / "match")
        except FileExistsError:
            pass
        else:
            raise AssertionError("existing report directory overwritten")
    print("validation PNG pixels, mismatch localization, errors and no-overwrite: PASS")


if __name__ == "__main__":
    main()
