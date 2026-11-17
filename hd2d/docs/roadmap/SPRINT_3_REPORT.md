# Sprint 3 report — interactive gameplay capture

Date: 2026-11-17. Canonical repository: `nkanhai/MegaManX2GraphicsUpgrade`.
Branch: `feature/hd2d-sprint-3`. Base: merged master
`2aaf98ee0be8238ca9feb57f7cedc7e8e65866b4` (Sprint 2 PR #3).

## Outcome and evidence boundary

Interactive capture is implemented with **Left Control + Left Shift + F10**,
using the existing draw-thread recorder and runtime v1 format. One session can
produce multiple independently replayable captures without scripted controller
inputs, predicted frame numbers or restarting between requests. New private
session/capture directories are created automatically by the launcher/adapter.

The owner reports these **local Apple Silicon observations** before this sprint:
exact USA ROM SHA-256 verification, successful native ARM64 ROM-backed build,
working keyboard gameplay, and successful real draw-1800 recording/reconstruction
with **0 of 57,344 pixels different**. The image was the opening narrative,
not a playable stage. These are owner-provided observations, not cloud CI or
independently inspected private artifacts. No ROM or capture was transferred here.

This sprint's SDL and PPU tests use original synthetic graphics. The final
Mac acceptance exercise—two captures at owner-chosen playable-stage moments
in one run—remains local work. The code and procedure are ready for that check;
this report does not claim it has happened already.

## Implementation and review scope

- `hotkey.c` observes SDL events without changing delivery. Its event callback
  only updates atomic flags; the draw owner consumes a bounded single pending
  request. Holding F10, OS repeat and a burst before the next draw coalesce.
- Conflict checks cover both raw controller scancodes (`keybinds.ini`, all seats)
  and host commands (`[KeyMap]`, including the modifier keys). The stock left
  modifiers avoid Right Shift/Select. Custom conflicting bindings disable the
  capture shortcut without changing the user's mappings.
- `x2_adapter.c` retains the one-shot DRAW mode and adds INTERACTIVE=1. Each
  attempt consumes a monotonic sequence and records current draw/epoch/guest
  counter/master cycles. Failures do not reuse identities or prevent later
  requests. The existing per-line PPU observation/render/HDMA order is unchanged.
- Exclusive directories include sequence and draw ordinal beneath the new
  session root. The same immutable v1 record and atomic final-file publication
  are used. Unsupported profiles and file failures retain precise diagnostics.
- The only host-source change wraps the existing title diagnostic shutdown to
  remove the event observer before SDL teardown, then runs the original replay
  shutdown. Root CMake adds the new opt-in source. No shared framework code,
  dependency gitlink, game logic, generated source, licence or attribution changed.
- `interactive_capture.py` verifies the private ROM and executable capability,
  plans or launches normal keyboard gameplay, and creates a UUID session under
  ignored `hd2d/local/interactive/`. It records hashes/configuration/commands and
  the exit outcome. There is no script, fixed draw target or game run timeout.
  A marker only detects an older/capture-disabled binary; it is not authentication.
- Existing `validate_capture.py` remains unchanged. The new end-to-end test runs
  it independently on both sequential captures and requires zero pixel errors.

The [trigger investigation](../architecture/INTERACTIVE_TRIGGER.md) explains why
an SDL observer is preferable to editing the pinned input loop or requiring a
trace-enabled TCP server. The [Mac guide](../guides/INTERACTIVE_CAPTURE.md) gives
reproducible private build, launch and two-file validation commands.

## Automated checks

Environment: Linux x86_64, GCC/G++ 14.2.0, Python 3.12.14, CMake 3.31.10,
SDL2 2.32.4. SDL event tests use the dummy video driver and no proprietary input.

| Check | Result |
|---|---|
| Python unit tests | 20 pass; unique sessions, argument/path preservation, no script/draw selector, private permissions, override filtering, old-binary rejection and launch failures |
| Native Release CTest | 6 pass; existing parity/limits/publication tests plus SDL trigger and interactive scenarios |
| Native ASan/UBSan CTest | 6 pass with the existing upstream-only shift-base exception |
| Focused pinned upstream tests | 8 pass |
| Complete Linux setup host, capture ON and OFF | Both build successfully; enabled host `--help` runs |
| Enabled setup-host diagnostics | 2 pass |
| Source/pins | Five recursive pins unchanged; whitespace checks pass |
| Actual Sprint 3 Mac shortcut/gameplay | Not run here; requires owner-local ROM/executable and keyboard |

Trigger tests cover edge/repeat/release behavior, wrong modifier combinations,
wrong window, focus loss, raw binding/command conflicts, producer-thread handoff,
coalescing, unchanged event delivery, pending-request cleanup and reinstall.
The native integration injects real SDL keyboard events into the real adapter
and title draw loop; host policy functions remain explicit test stand-ins.

Two supported requests produce draws 2 and 4, sequences 1 and 2 in one session,
with a synthetic epoch change 7→8 and guest counter rewind 42→0. Both capture
images differ from each other and each reconstructs with **0/57,344** different
pixels. All intermediate PPU/CPU/DMA state and framebuffer hashes match a
separately compiled-out process under identical synthetic inputs/events.

Additional scenarios reject the first profile or collide with its output path,
then successfully publish sequence 2; refuse a reused session root; reject mixed
DRAW/interactive configuration; disable conflicting bindings; and exhaust the
64-attempt limit while gameplay output continues unchanged. Every successfully
published synthetic file passes native reconstruction.

The sanitizer exception remains limited to signed packed-bitplane shift-base
instrumentation in pinned upstream `ppu.c`; all HD-2D code is instrumented.
No wholly unsuppressed sanitizer or physical-SNES-accuracy claim is made.
Linux/macOS Actions now install SDL2 before running the expanded native tests.
Cloud workflow outcomes must be read from the PR checks, separately from the
owner's earlier local ARM64 result.

## Limits and remaining acceptance

- The mailbox has capacity one. A request selects the next draw, not the frame
  already on screen. Paused requests wait for drawing to resume; active gameplay
  is the intended trigger context. Unsupported host/profile states are rejected.
- Capture, hashing and writing remain synchronous and can produce a brief hitch.
  Memory/file size stays bounded to 4 MiB per capture and 64 attempts per process
  session (at most 256 MiB of containers). A full session does not stop gameplay.
- No in-game notification UI or configurable/global shortcut is added. Terminal
  logs show the saved path or failure; macOS function-key settings may require Fn.
- Real SDL2 event injection is covered, but the Mac's physical keyboard/window
  behavior and two real interactive captures remain owner acceptance. Windows,
  SDL3, arbitrary Mode-1 scenes, Cx4 coverage and hardware fidelity are unqualified.
- A capture `complete` message is not a fidelity verdict. The owner must visually
  confirm both gameplay moments and run the existing validator on each, expecting
  zero differences. Keep reports, ROMs, generated code and images private.

Unity, gameplay changes, ROM patches and asset replacement/cataloguing remain
outside scope. The Sprint 3 PR is for review and is not automatically merged.
