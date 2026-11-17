#!/usr/bin/env python3
"""Derive ORIGINAL synthetic Unity test cases; reference images come from pinned PPU.

Accepts only source=synthetic runtime captures. Never use private game captures.
Outputs remain in a NEW ignored local directory, not Unity Assets or Git.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import sys

import scanout_format as fmt


def digest(data):
    return data + hashlib.sha256(data).digest()


def generate(source, replay, output):
    local = Path(__file__).resolve().parents[1] / "local"
    if not output.resolve().is_relative_to(local.resolve()):
        raise ValueError("fixture output must stay under ignored hd2d/local/")
    original = source.read_bytes()
    info = fmt.decode(original)
    if info["source"] != "synthetic":
        raise ValueError("only original synthetic captures may supply fixtures")
    output.mkdir(mode=0o700, parents=True, exist_ok=False)
    (output / "raster-existing.x2scan").write_bytes(original)
    memories = {}
    pos = fmt.MEMORY
    while pos < len(original) - 32:
        kind, identity, size = struct.unpack_from("<III", original, pos)
        pos += 12
        if identity == 0:
            memories[kind] = bytearray(original[pos:pos+size])
        pos += size
    names = [name for _, name in fmt.FIELDS]
    registers = list(struct.unpack_from("<70I", original, fmt.HEADER + fmt.LINE_BYTES + 16))
    cases = ["static8", "static16", "bg3-high", "palette-flips", "obj-rotation", "obj-wrap", "obj-overflow", "obj-unlimited", "dim", "blank", "legacy", "obj-ownership", "obj-bank-wrap"]
    cases += ["obj-size-" + str(size) for size in range(1, 6)]
    records = []
    for index, case in enumerate(cases):
        regs = registers.copy()
        def reg(name, value): regs[names.index(name)] = value
        for field in ("windowsel", "screenWindowed[0]", "screenWindowed[1]", "cgwsel", "cgadsub", "fixedColor", "oamaddh", "oamaddl", "timeOver", "rangeOver"):
            reg(field, 0)
        reg("bgmode", 1); reg("inidisp", 15); reg("screenEnabled[0]", 0x17)
        vram, cgram, oam = (bytearray(memories[k]) for k in range(3))
        # BG3 uses fresh original 2bpp stripe/checker tiles at word $3000.
        reg("bgTileAdr", 0x321); reg("bgXsc[2]", 8)
        for y in range(8):
            struct.pack_into("<H", vram, (0x3000+y)*2, 0xaa55 if y & 1 else 0x00cc)
        for cell in range(1024):
            struct.pack_into("<H", vram, (0x800+cell)*2, (cell % 8) << 10 | (0x2000 if cell % 3 == 0 else 0))
        if case == "static16": reg("bgmode", 0x71)
        if case == "bg3-high": reg("bgmode", 9)
        if case == "palette-flips":
            reg("bgXsc[0]", 3); reg("hScroll[0]", 507); reg("vScroll[0]", 509)
            for cell in range(4096):
                struct.pack_into("<H", vram, cell*2, (cell % 2) | ((cell % 8) << 10) | ((cell % 4) << 14) | (0x2000 if cell & 4 else 0))
        if case in ("obj-rotation", "obj-wrap"):
            reg("screenEnabled[0]", 0x10)
            struct.pack_into("<HHHH", oam, 0, (12 << 8) | 20, 0x0000, (12 << 8) | 20, 0x3200)
            if case == "obj-rotation": reg("oamaddh", 128); reg("oamaddl", 2)
            else:
                struct.pack_into("<HH", oam, 0, (250 << 8) | 250, 0xf000)
                oam[512] |= 3  # x=-6, large object, vertically wraps to the top.
        if case in ("obj-overflow", "obj-unlimited"):
            reg("screenEnabled[0]", 16)
            for slot in range(33):
                struct.pack_into("<HH", oam, slot*4, (12 << 8) | (slot*7 % 200), 0x3000 | ((slot % 8) << 9))
                oam[512+slot//4] |= 2 << ((slot % 4)*2)
        if case == "obj-ownership":
            # Lower OAM owns a pixel even when the later sprite has higher
            # BG priority. Sorting whole SpriteRenderers would get this wrong.
            struct.pack_into("<HHHH", oam, 0, (40 << 8) | 55, 0x0000, (40 << 8) | 55, 0x3200)
        if case.startswith("obj-size-") or case == "obj-bank-wrap":
            reg("screenEnabled[0]", 16)
            reg("obsel", int(case[-1]) * 32 + 3 if case.startswith("obj-size-") else 0x1f)
            # Original, dense planar patterns exercise every sub-tile of the
            # large object, bank selection, row/column wrap and both flips.
            for word in range(32768): struct.pack_into("<H", vram, word*2, (word * 137 + 0x55aa) & 65535)
            struct.pack_into("<HHHH", oam, 0, (30 << 8) | 40, 0xf1ff, (120 << 8) | 135, 0x300f)
            oam[512] |= 2
        if case == "dim": reg("inidisp", 7)
        if case == "blank": reg("inidisp", 0x80)
        header = bytearray(original[:fmt.HEADER])
        struct.pack_into("<Q", header, 64, index+1)
        struct.pack_into("<I", header, 176, 9 if case == "obj-unlimited" else 0 if case == "legacy" else 1)
        struct.pack_into("<III", header, 180, 1, 1, 1)
        header[208:240] = bytes(32)
        lines = b"".join(struct.pack("<74I", line, 0, 0, 0, *regs) for line in range(225))
        payload = b"".join(struct.pack("<III", k, 0, len(mem)) + mem for k, mem in enumerate((vram, cgram, oam)))
        data = header + lines + bytes([0, 0, 0, 255]) * (256*224) + payload
        struct.pack_into("<I", data, 16, len(data)+32)
        path, rgba = output / (case + ".x2scan"), output / (case + ".rgba")
        path.write_bytes(digest(data))
        result = subprocess.run([str(replay), str(path), "--rgba-output", str(rgba)], capture_output=True, text=True)
        if result.returncode not in (0, 1): raise ValueError(result.stdout + result.stderr)
        pixels = rgba.read_bytes()
        if len(pixels) != fmt.RGBA_BYTES: raise ValueError("incomplete native oracle image")
        data[fmt.REFERENCE:fmt.MEMORY] = pixels
        path.write_bytes(digest(data)); rgba.unlink()
        subprocess.run([str(replay), str(path)], check=True, capture_output=True)
        records.append({"name": case, "reference_sha256": hashlib.sha256(pixels).hexdigest()})
    (output / "provenance.json").write_text(json.dumps({"source": "original synthetic graphics only", "source_sha256": hashlib.sha256(original).hexdigest(), "engine": info["engine_revision"], "cases": records}, indent=2)+"\n")
    return records


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--synthetic", type=Path, required=True)
    parser.add_argument("--replay", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try: print(json.dumps(generate(args.synthetic, args.replay.resolve(), args.output), indent=2))
    except (ValueError, OSError, subprocess.SubprocessError) as error: parser.exit(2, str(error)+"\n")
