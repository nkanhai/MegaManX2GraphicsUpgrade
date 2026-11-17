"""Cross-process compiled-out/on equivalence, real adapter, hostile containers."""
import hashlib
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
import scanout_format as fmt


def check(condition, message):
    if not condition:
        raise AssertionError(message)


def main():
    on, off, adapter, replay = sys.argv[1:]
    env = {k: v for k, v in os.environ.items() if not k.startswith(("HD2D_CAPTURE_", "TEST_"))}
    env.pop("SNESRECOMP_LAYER_MASK", None)

    def run(exe, *args, extra=None, success=True):
        result = subprocess.run([exe, *map(str, args)], env=env | (extra or {}),
                                text=True, capture_output=True)
        check((result.returncode == 0) == success,
              f"{exe} unexpected exit {result.returncode}: {result.stdout} {result.stderr}")
        return result

    with tempfile.TemporaryDirectory(prefix="hd2d-synthetic-") as temp:
        root = Path(temp)
        enabled = run(on, root / "on")
        disabled = run(off, root / "off")
        check(enabled.stdout == disabled.stdout, "compiled-out and armed pixels differ")
        path = root / "on/capture.x2scan"
        data = path.read_bytes()
        report = fmt.decode(data)
        check(report["memory_versions"] == [2, 3, 3], "versions")
        check(report["lines"][63]["registers"]["hScroll[0]"] == 0, "early scroll")
        check(report["lines"][64]["registers"]["hScroll[0]"] == 13, "late scroll")
        check(report["lines"][128]["memory_ids"][1] == 2, "ABA palette must be immutable")
        check(report["lines"][0]["registers"]["m7matrix[0]"] == -32768 and
              report["lines"][0]["registers"]["m7startX"] == -123456, "signed field encoding")
        print(run(replay, path).stdout.strip())
        config = {"HD2D_CAPTURE_DIR": str(root / "adapter"), "HD2D_CAPTURE_DRAW": "2",
                  "HD2D_CAPTURE_SESSION": "0123456789abcdef0123456789abcdef"}
        result = run(adapter, extra=config)
        check(result.stderr.count("[hd2d]") == 1, "adapter must attempt once in three draws")
        captured = fmt.read(root / "adapter/capture.x2scan")
        check(captured["draw"] == 2 and captured["epoch"] == 7 and captured["sequence"] == 1,
              "adapter identity")
        check(captured["reference_sha256"] == report["reference_sha256"], "adapter pixels")
        run(replay, root / "adapter/capture.x2scan")
        print(result.stderr.strip())
        cart_dest = root / "synthetic-cart"
        run(adapter, extra=config | {"HD2D_CAPTURE_DIR": str(cart_dest), "TEST_CART": "1"})
        check(fmt.read(cart_dest / "capture.x2scan")["runtime_cart_sha256"] ==
              hashlib.sha256(b"original synthetic identity").hexdigest(), "observed cart identity")
        for policy in ("TEST_RUNAHEAD", "TEST_NETPLAY", "TEST_ROLLBACK", "TEST_REWIND"):
            dest = root / policy
            rejected = run(adapter, extra=config | {"HD2D_CAPTURE_DIR": str(dest), policy: "1"})
            check(not dest.exists() and "rejected" in rejected.stderr, policy)
            check(rejected.stdout == disabled.stdout, "policy rejection changed pixels")
        for policy, reason in (("TEST_PPU_MOSAIC", "draw entry: mosaic: requires 0; observed 17"),
                               ("TEST_MIDFRAME_MOSAIC", "line 64: mosaic: requires 0; observed 17")):
            dest = root / policy
            rejected = run(adapter, extra=config | {"HD2D_CAPTURE_DIR": str(dest), policy: "1"})
            check(not dest.exists() and reason in rejected.stderr, "exact profile failure: " + policy)
            baseline = run(off, root / "unused", extra={policy: "1"})
            check(rejected.stdout == baseline.stdout, "unsupported capture altered drawing")
        for selection in ("-1", "0", "1x", "18446744073709551616"):
            dest = root / "bad-selection"
            rejected = run(adapter, extra=config | {"HD2D_CAPTURE_DIR": str(dest), "HD2D_CAPTURE_DRAW": selection})
            check(not dest.exists() and "rejected" in rejected.stderr, "invalid request")
        rejected = run(adapter, extra=config | {"HD2D_CAPTURE_SESSION": "invalid"})
        check("rejected" in rejected.stderr, "invalid session")
        check(run(adapter).stdout == disabled.stdout, "unarmed adapter changed pixels")
        # Short writes/failed close must not publish a discoverable final file.
        if os.name == "posix":
            import resource
            import signal
            def fail_writes():
                resource.setrlimit(resource.RLIMIT_FSIZE, (0, 0))
                signal.signal(signal.SIGXFSZ, signal.SIG_IGN)
            dest = root / "short-write"
            failed = subprocess.run([adapter], env=env | config | {"HD2D_CAPTURE_DIR": str(dest)},
                                    preexec_fn=fail_writes, capture_output=True, text=True)
            check(failed.returncode == 0 and "file-error" in failed.stderr, "short write status")
            check(not dest.exists(), "failed publication leaked partial record")
            check(failed.stdout == disabled.stdout, "write failure changed pixels")

        mutations = {
            "major version": (8, 2), "profile": (12, 2), "length": (16, len(data)+1),
            "line count": (20, 224), "dimensions": (24, 512), "register count": (32, 1),
            "source": (36, 3), "flags": (176, 16), "versions": (180, 226),
            "engine pin": (136, 0x30303030),
            "missing line": (fmt.HEADER+fmt.LINE_BYTES, 2),
            "bad reference": (fmt.HEADER+4, 224),
            "future version": (fmt.HEADER+8, 1),
            "bad memory kind": (fmt.MEMORY, 3),
            "bad memory id": (fmt.MEMORY+4, 10),
            "bad memory size": (fmt.MEMORY+8, 65535),
            "register width": (fmt.HEADER+16, 256),
            "unsupported mode": (fmt.HEADER+16+4*4, 7),
        }
        invalid = root / "invalid.x2scan"
        for name, (offset, value) in mutations.items():
            altered = bytearray(data)
            struct.pack_into("<I", altered, offset, value)
            altered[-32:] = hashlib.sha256(altered[:-32]).digest()
            invalid.write_bytes(altered)
            try:
                fmt.decode(altered)
            except ValueError:
                pass
            else:
                raise AssertionError(f"Python reader accepted {name}")
            run(replay, invalid, success=False)
        for name, altered in (("truncated", data[:-1]), ("extra", data+b"x"),
                              ("checksum", data[:-1]+bytes([data[-1]^1])),
                              ("oversize", bytes(fmt.MAX_BYTES+1))):
            invalid.write_bytes(altered)
            try:
                fmt.decode(altered)
            except ValueError:
                pass
            else:
                raise AssertionError(f"Python reader accepted {name}")
            run(replay, invalid, success=False)
        altered = bytearray(data)
        altered[fmt.REFERENCE] ^= 1
        altered[-32:] = hashlib.sha256(altered[:-32]).digest()
        fmt.decode(altered)  # Valid structure cannot establish pixel parity.
        invalid.write_bytes(altered)
        check("pixel mismatch" in run(replay, invalid, success=False).stdout, "parity oracle")
        print(f"cross-process/adapter/publication checks and {len(mutations)+5} malformed/parity cases: PASS")


if __name__ == "__main__":
    main()
