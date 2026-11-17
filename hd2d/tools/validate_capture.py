#!/usr/bin/env python3
"""Replay one .x2scan and produce private PNG evidence plus JSON/text reports.

Exit 0 = all pixels match; 1 = fidelity mismatch; 2 = invalid input/tool error.
Uses only Python's standard library and the pinned native scanout_replay tool.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import tempfile
import zlib

import scanout_format as fmt


def png_bytes(rgba, width=256, height=224):
    if len(rgba) != width * height * 4:
        raise ValueError("RGBA dimensions do not match byte length")
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    scanlines = b"".join(b"\0" + rgba[y*width*4:(y+1)*width*4] for y in range(height))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(scanlines)) + chunk(b"IEND", b""))


def pixel_diff(reference, reconstruction):
    if len(reference) != fmt.RGBA_BYTES or len(reconstruction) != fmt.RGBA_BYTES:
        raise ValueError("replay output is not a complete 256x224 RGBA image")
    count = total = maximum = 0
    rows = [0] * 224
    bbox = [256, 224, -1, -1]
    mask = bytearray(fmt.RGBA_BYTES)
    for offset in range(0, fmt.RGBA_BYTES, 4):
        deltas = [abs(reference[offset+c] - reconstruction[offset+c]) for c in range(4)]
        maximum = max(maximum, *deltas)
        total += sum(deltas)
        changed = any(deltas)
        x, y = (offset // 4) % 256, (offset // 4) // 256
        if changed:
            count += 1
            rows[y] += 1
            bbox = [min(bbox[0], x), min(bbox[1], y), max(bbox[2], x), max(bbox[3], y)]
        mask[offset:offset+4] = bytes((255, 0, 255, 255) if changed else (0, 0, 0, 255))
    return bytes(mask), {
        "different_pixels": count, "total_pixels": 57344,
        "different_fraction": count / 57344, "max_channel_error": maximum,
        "mean_absolute_channel_error": total / fmt.RGBA_BYTES,
        "difference_bbox_inclusive": bbox if count else None,
        "different_pixels_by_row": rows,
        "difference_image": "binary mask: magenta=different, black=identical; exact RGBA comparison",
    }


def validate(capture, replay, output):
    output = Path(output)
    output.mkdir(mode=0o700, parents=False, exist_ok=False)
    report = {"report_format": "x2-hd2d.fidelity-report", "version": 1, "status": "error",
              "oracle": "pinned upstream PPU; not SNES hardware or an independent emulator"}
    code = 2
    try:
        with Path(capture).open("rb") as stream:
            data = stream.read(fmt.MAX_BYTES + 1)
        metadata = fmt.decode(data)
        metadata.pop("lines")
        report["capture"] = metadata
        report["capture_file_sha256"] = hashlib.sha256(data).hexdigest()
        replay = Path(replay).resolve(strict=True)
        report["replay_executable_sha256"] = hashlib.sha256(replay.read_bytes()).hexdigest()
        # Replay exactly the bytes inspected above, even if the original changes.
        with tempfile.TemporaryDirectory(prefix=".replay-", dir=output) as scratch:
            snapshot, reconstructed = Path(scratch)/"input.x2scan", Path(scratch)/"reconstruction.rgba"
            snapshot.write_bytes(data)
            result = subprocess.run([str(replay), str(snapshot), "--rgba-output", str(reconstructed)],
                                    text=True, capture_output=True, timeout=60)
            report["replay_exit_code"] = result.returncode
            report["replay_diagnostic"] = (result.stdout + result.stderr).strip()[-4096:]
            if result.returncode not in (0, 1):
                raise ValueError("native replay rejected the capture; see replay_diagnostic")
            with reconstructed.open("rb") as stream:
                rgba = stream.read(fmt.RGBA_BYTES + 1)
        reference = data[fmt.REFERENCE:fmt.MEMORY]
        difference, metrics = pixel_diff(reference, rgba)
        code = int(metrics["different_pixels"] != 0)
        if result.returncode != code:
            raise ValueError("native replay status disagrees with independent pixel comparison")
        report["metrics"] = metrics
        report["reconstruction_sha256"] = hashlib.sha256(rgba).hexdigest()
        for name, pixels in (("reference", reference), ("reconstruction", rgba), ("difference", difference)):
            (output / f"{name}.png").write_bytes(png_bytes(pixels))
        report["status"] = "verified" if code == 0 else "mismatch"
        report["scope"] = "exact captured-frame reconstruction only; source/provenance is reported, not authenticated"
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        report["status"] = "error"
        report["error"] = str(error)
        code = 2
        for name in ("reference", "reconstruction", "difference"):
            (output / f"{name}.png").unlink(missing_ok=True)
    (output / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    if code in (0, 1):
        metrics = report["metrics"]
        summary = (f"{report['status'].upper()}: {metrics['different_pixels']}/57344 pixels differ; "
                   f"max channel error={metrics['max_channel_error']}.\n"
                   "Reference and reconstruction are canonical RGBA. Difference: magenta marks changed pixels.\n")
    else:
        summary = f"ERROR: {report['error']}\nNo fidelity verdict or images were produced.\n"
    (output / "report.txt").write_text(summary + report["oracle"] + "\n", encoding="utf-8")
    return code


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--replay", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True, help="new private output directory; parent must exist")
    args = parser.parse_args()
    try:
        code = validate(args.capture, args.replay, args.output)
    except OSError as error:
        parser.exit(2, f"Output directory: {error}\n")
    print((args.output / "report.txt").read_text(), end="")
    return code


if __name__ == "__main__":
    raise SystemExit(main())
