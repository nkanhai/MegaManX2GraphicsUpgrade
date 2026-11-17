#!/usr/bin/env python3
"""Strict runtime v1 container inspection. This is NOT the Sprint 0 format.

Only validates structure/integrity. Use native scanout_replay for pixel parity.
No file paths occur inside a container; no external blobs can be followed.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import struct

MAX_BYTES = 4 * 1024 * 1024
HEADER = 240
LINES = 225
RGBA_BYTES = 256 * 224 * 4
FIELDS = re.findall(r"HD2D_FIELD\((\w+), ([^)]+)\)",
                    (Path(__file__).resolve().parents[1] / "bridge/fields.inc").read_text())
LINE_BYTES = 16 + 4 * len(FIELDS)
REFERENCE = HEADER + LINES * LINE_BYTES
MEMORY = REFERENCE + RGBA_BYTES


def decode(data):
    def require(condition, message):
        if not condition:
            raise ValueError(message)

    def u32(offset):
        return struct.unpack_from("<I", data, offset)[0]

    def u64(offset):
        return struct.unpack_from("<Q", data, offset)[0]

    require(MEMORY + 32 <= len(data) <= MAX_BYTES, "size outside budget")
    require(data[:8] == b"X2SCAN01" and u32(8) == 1 and u32(12) == 1, "unknown format/profile")
    require((u32(16), u32(20), u32(24), u32(28), u32(32)) ==
            (len(data), LINES, 256, 224, len(FIELDS)), "invalid dimensions/counts/completeness")
    require(u32(36) in (0, 1) and any(data[40:56]) and u64(64) and u64(72), "invalid identity")
    require(re.fullmatch(b"[0-9a-f]{80}", data[96:176]), "invalid revisions")
    require(data[136:176] == b"93fc677a2a9b688751d575ed1232343d83d3d2c0", "unqualified engine revision")
    require(u32(176) in (0, 1, 8, 9), "unknown renderer flags")
    require(hashlib.sha256(data[:-32]).digest() == data[-32:], "checksum mismatch")
    counts = [u32(180 + k*4) for k in range(3)]
    require(all(1 <= n <= LINES for n in counts), "invalid version count")
    memories = [[], [], []]
    pos = MEMORY
    while pos < len(data) - 32:
        require(pos + 12 <= len(data) - 32, "truncated memory descriptor")
        kind, identity, size = struct.unpack_from("<III", data, pos)
        pos += 12
        require(kind < 3, "unknown memory kind")
        require(identity == len(memories[kind]) < counts[kind] and
                size == (65536, 512, 544)[kind], "invalid memory descriptor")
        require(pos + size <= len(data) - 32, "truncated memory image")
        memories[kind].append(data[pos:pos+size])
        pos += size
    require(list(map(len, memories)) == counts, "missing memory image")
    last = [0, 0, 0]
    lines = []
    for line in range(LINES):
        values = struct.unpack_from(f"<{4+len(FIELDS)}I", data, HEADER+line*LINE_BYTES)
        require(values[0] == line, "missing/reordered line")
        ids = list(values[1:4])
        require(all(i < counts[k] and last[k] <= i <= last[k]+1 and
                    (line != 0 or i == 0) for k, i in enumerate(ids)), "invalid version reference")
        last = ids
        regs = {}
        for (kind, name), raw in zip(FIELDS, values[4:]):
            value = raw - 2**32 if kind.startswith("s") and raw >= 2**31 else raw
            bounds = {"u8": (0, 255), "u16": (0, 65535), "bit": (0, 1),
                      "s16": (-32768, 32767), "s32": (-2**31, 2**31-1), "u32": (0, 2**32-1)}
            lo, hi = bounds[kind]
            require(lo <= value <= hi, f"invalid register: {name}")
            regs[name] = value
        require(regs["bgmode"] & 7 == 1 and not any(regs[k] for k in
                ("mosaic", "setini", "frameOverscan", "frameInterlace")), "unsupported display mode")
        lines.append({"line": line, "memory_ids": ids, "registers": regs})
    require([i+1 for i in last] == counts, "unused memory version")
    reference = data[REFERENCE:MEMORY]
    require(all(alpha == 255 for alpha in reference[3::4]), "noncanonical RGBA alpha")
    return {
        "format": "x2-hd2d.runtime-scanout", "version": 1, "profile": "native-mode1-v1",
        "phase": "x2_scanout_before_line", "session": data[40:56].hex(),
        "epoch": u64(56), "sequence": u64(64), "draw": u64(72),
        "guest_frame_bits": u64(80), "master_cycles": u64(88),
        "source": ("synthetic", "title-runtime")[u32(36)],
        "producer_revision": data[96:136].decode(), "engine_revision": data[136:176].decode(),
        "render_flags": u32(176), "memory_versions": counts,
        "runtime_cart_sha256": data[208:240].hex() if any(data[208:240]) else None,
        "copied_bytes": u64(192), "comparison_bytes": u64(200), "file_bytes": len(data),
        "reference_sha256": hashlib.sha256(reference).hexdigest(),
        "container_sha256": data[-32:].hex(), "lines": lines,
    }


def read(path):
    with Path(path).open("rb") as stream:
        return decode(stream.read(MAX_BYTES + 1))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    args = parser.parse_args()
    report = read(args.capture)
    report["lines"] = len(report["lines"])
    report["parity"] = "not checked; run scanout_replay"
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
