#!/usr/bin/env python3
"""ROM-independent static Mode 1 PPU snapshot spike; see schemas/FRAME_V1.md."""
import argparse
import base64
import binascii
import hashlib
import json
from pathlib import Path
import struct

FORMAT = "x2-hd2d.synthetic-ppu"
MAX_DOCUMENT_BYTES = 256 * 1024
MEMORY_SIZES = {"vram": 65536, "cgram": 512, "oam": 544}
SCALARS = {
    "inidisp": 255, "obsel": 255, "bgmode": 255, "mosaic": 255,
    "bgTileAdr": 65535, "setini": 255, "windowsel": 0xFFFFFF,
    "wbgobjlog": 65535, "window1left": 255, "window1right": 255,
    "window2left": 255, "window2right": 255, "cgadsub": 255,
    "cgwsel": 255, "fixedColor": 32767,
}
ARRAYS = {"bgXsc": (4, 255), "hScroll": (4, 65535),
          "vScroll": (4, 65535), "screenEnabled": (2, 31),
          "screenWindowed": (2, 31)}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def fields(value, expected, label):
    require(type(value) is dict and set(value) == set(expected),
            f"{label}: missing or unknown fields")


def integer(value, maximum, label):
    require(type(value) is int and 0 <= value <= maximum,
            f"{label}: expected integer in [0, {maximum}]")


def pack_memory(data):
    return {"encoding": "base64", "byte_length": len(data),
            "sha256": hashlib.sha256(data).hexdigest(),
            "data": base64.b64encode(data).decode("ascii")}


def unpack_memory(block, size, label):
    fields(block, ("encoding", "byte_length", "sha256", "data"), label)
    require(block["encoding"] == "base64", f"{label}: unsupported encoding")
    integer(block["byte_length"], size, f"{label}.byte_length")
    require(block["byte_length"] == size, f"{label}: incorrect byte length")
    encoded = block["data"]
    require(type(encoded) is str and len(encoded) == 4 * ((size + 2) // 3),
            f"{label}: incorrect encoded length")
    try:
        data = base64.b64decode(encoded, validate=True)
    except (ValueError, binascii.Error) as exc:
        raise ValueError(f"{label}: invalid base64") from exc
    require(len(data) == size and base64.b64encode(data).decode("ascii") == encoded,
            f"{label}: noncanonical base64 or incorrect decoded length")
    require(block["sha256"] == hashlib.sha256(data).hexdigest(),
            f"{label}: checksum mismatch")
    return data


def validate(frame):
    """Validate the bounded experimental profile; return decoded memory bytes."""
    fields(frame, ("format", "version", "source", "sequence", "profile",
                   "viewport", "ppu", "memory"), "frame")
    require(frame["format"] == FORMAT, "unknown format")
    integer(frame["version"], 1, "version")
    require(frame["version"] == 1, "unsupported version")
    require(frame["source"] == "synthetic", "v1 accepts synthetic data only")
    require(frame["profile"] == "static-mode1-native", "unsupported profile")
    integer(frame["sequence"], 2**53 - 1, "sequence")
    fields(frame["viewport"], ("width", "height"), "viewport")
    for key, expected in (("width", 256), ("height", 224)):
        integer(frame["viewport"][key], expected, key)
        require(frame["viewport"][key] == expected, "unsupported viewport")
    ppu = frame["ppu"]
    fields(ppu, set(SCALARS) | set(ARRAYS), "ppu")
    for key, maximum in SCALARS.items():
        integer(ppu[key], maximum, key)
    for key, (count, maximum) in ARRAYS.items():
        require(type(ppu[key]) is list and len(ppu[key]) == count,
                f"{key}: expected {count} entries")
        for item in ppu[key]:
            integer(item, maximum, key)
    require(ppu["bgmode"] == 1, "only Mode 1 with 8x8 BG tiles is supported")
    for key in ("mosaic", "setini", "windowsel", "wbgobjlog", "cgadsub", "cgwsel"):
        require(ppu[key] == 0, f"{key}: unsupported feature in static profile")
    require(ppu["screenEnabled"][1] == 0 and ppu["screenWindowed"] == [0, 0],
            "subscreens/windows are outside this profile")
    require(ppu["screenEnabled"][0] & ~0x13 == 0,
            "only BG1, BG2 and OBJ may be enabled")
    fields(frame["memory"], MEMORY_SIZES, "memory")
    memory = {key: unpack_memory(frame["memory"][key], size, key)
              for key, size in MEMORY_SIZES.items()}
    require(all(word <= 0x7FFF for (word,) in struct.iter_unpack("<H", memory["cgram"])),
            "CGRAM must contain RGB15 words with bit 15 clear")
    return memory


def dumps(frame):
    validate(frame)
    return (json.dumps(frame, sort_keys=True, separators=(",", ":"),
                       allow_nan=False) + "\n").encode("utf-8")


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, f"duplicate JSON key: {key}")
        result[key] = value
    return result


def loads(data):
    require(type(data) is bytes and len(data) <= MAX_DOCUMENT_BYTES,
            "input must be bytes within the 256 KiB limit")
    try:
        frame = json.loads(data.decode("utf-8"), object_pairs_hook=unique_object)
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError) as exc:
        raise ValueError("invalid UTF-8 JSON document") from exc
    validate(frame)
    return frame


def synthetic_frame():
    """Original stripes/checkerboard graphics; no game data or ROM access."""
    vram = bytearray(65536)
    # Tile 1: vertical stripes of color indices 1,2. SNES plane pairs at 0/16.
    stripe = bytes([0xAA, 0x55] * 8 + [0, 0] * 8)
    # Tile 1 in BG2's separate bank: alternating transparent/color-3 squares.
    checker = bytes([v for y in range(8) for v in ([0xAA if y % 2 == 0 else 0x55] * 2)]
                    + [0, 0] * 8)
    vram[32:64] = stripe
    vram[0x2020:0x2040] = checker  # BG2 char base 0x1000 WORDS.
    # Two 32x32 tilemaps at word addresses 0x2000 and 0x2400.
    for i in range(1024):
        struct.pack_into("<H", vram, 0x4000 + 2 * i, 1 | (0x4000 if i % 2 else 0))
        struct.pack_into("<H", vram, 0x4800 + 2 * i, 1 | (1 << 10) | (1 << 13))
    palette = [0] * 256
    palette[1:3] = [0x001F, 0x03E0]  # full red, full green
    palette[19] = 0x7C00             # BG2 palette 1, color 3: blue
    palette[129:131] = [0x7FFF, 0x03FF]
    cgram = struct.pack("<256H", *palette)
    oam = bytearray(544)
    for slot in range(128):
        oam[slot * 4 + 1] = 240  # offscreen in this noninterlaced fixture
    # Three hardware pieces: two adjacent, one partially offscreen with X bit 8.
    oam[0:12] = bytes([24, 32, 1, 0x30, 32, 32, 1, 0x70, 252, 48, 1, 0xB0])
    oam[512] = 1 << 4  # slot 2 x=508 => signed screen x=-4, small 8x8 size
    ppu = {key: 0 for key in SCALARS}
    ppu.update(inidisp=15, bgmode=1, bgTileAdr=0x10,
               bgXsc=[0x20, 0x24, 0, 0], hScroll=[3, 11, 0, 0],
               vScroll=[5, 7, 0, 0], screenEnabled=[0x13, 0],
               screenWindowed=[0, 0])
    return {"format": FORMAT, "version": 1, "source": "synthetic", "sequence": 0,
            "profile": "static-mode1-native", "viewport": {"width": 256, "height": 224},
            "ppu": ppu, "memory": {key: pack_memory(data) for key, data in
                                    (("vram", vram), ("cgram", cgram), ("oam", oam))}}


def summary(frame):
    memory = validate(frame)
    return {"format": frame["format"], "version": frame["version"],
            "source": frame["source"], "sequence": frame["sequence"],
            "profile": frame["profile"], "viewport": frame["viewport"],
            "hScroll": frame["ppu"]["hScroll"], "vScroll": frame["ppu"]["vScroll"],
            "memory_bytes": {key: len(value) for key, value in memory.items()},
            "memory_sha256": {key: hashlib.sha256(value).hexdigest()
                              for key, value in memory.items()}}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("demo", "validate"))
    parser.add_argument("path", type=Path)
    args = parser.parse_args()
    try:
        if args.command == "demo":
            frame = synthetic_frame()
            args.path.parent.mkdir(parents=True, exist_ok=True)
            # Do not overwrite existing local captures or fixtures.
            with args.path.open("xb") as output:
                output.write(dumps(frame))
        else:
            with args.path.open("rb") as source:
                frame = loads(source.read(MAX_DOCUMENT_BYTES + 1))
        print(json.dumps(summary(frame), indent=2))
    except (OSError, ValueError) as exc:
        parser.exit(1, f"frame_format: {exc}\n")


if __name__ == "__main__":
    main()
