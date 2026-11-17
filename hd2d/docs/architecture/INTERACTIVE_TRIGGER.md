# Interactive trigger investigation and ownership

Source: merged fork master `2aaf98ee0be8238ca9feb57f7cedc7e8e65866b4`.
The five pins in `upstream-lock.json` remain unchanged.

## Observed input/debug infrastructure

- `snesrecomp/runner/src/desktop/host_main.c` pumps SDL events and resolves
  keyboard commands through `FindCmdForSdlKey`; `HandleInput` does not fall back
  to an unmodified key when a modified chord has no mapping.
- `desktop/host_main.h` has lifecycle/frame callbacks, but no per-title keyboard
  event callback. `desktop/mmx23_host_main.inc` owns the common X2/X3 loop.
  Patching that loop or adding a shared command would modify a pinned dependency.
- `desktop/mmx_config.c` binds F1–F12 to state/menu functions and right Shift to
  Select. Left Control + Left Shift + F10 is unbound in the default map.
- Controller input also reads raw SDL scancodes through `keybinds.c`, independent
  of the command map and keyboard modifiers. Conflict checking must cover both
  systems, including bindings for seats that are currently inactive.
- The debug server has `debug_server_set_game_command_handler`, but requires
  trace-enabled builds and exposes a broader debugging surface. A local file
  mailbox would avoid SDL integration but require external requests/polling.
  Neither is necessary for the available observer-based keyboard mechanism.

## Chosen implementation

`hd2d/bridge/hotkey.c` installs an SDL event watch on the first interactive draw.
The watch observes the focused game window's F10 events; it never edits an event,
consumes it, injects input, or calls simulation/debug APIs. SDL may invoke it on
an event producer thread, so it touches only C11 atomic pending/held/window flags.
It recognizes the left-modifier chord on a non-repeat keydown, with keyup/focus
loss releasing the held latch. Other modifier combinations and windows are ignored.

The existing draw owner calls `hd2d_hotkey_poll`, checks both live binding maps,
and consumes at most one pending request. This is a bounded, coalescing mailbox:
presses before consumption merge; a new press after consumption can queue the
following draw. No callback reads a PPU, allocates a record or writes a file.
The owner creates and finishes the same runtime-v1 recorder at the unchanged
`X2DrawPpuFrame` boundaries. `ppu_runLine` and HDMA order are untouched.

`src/mmx_diagnostics_host.h` only wraps its existing shutdown callback to remove
the observer and clear pending requests before SDL teardown, then calls the
original replay shutdown. Repeated shutdown is safe. Adapter sequence/draw state
survives an in-process host lifecycle, while each launcher invocation creates a
new UUID/private root. Epoch is still sampled from `RtlStateGeneration`.

## Identity, failure and storage policy

- Runtime format/version and pixel/replay semantics are unchanged.
- Every attempt increments sequence, including rejection and I/O failure.
  Reset/load can change epoch/guest counter without reusing capture identity.
- The new parent capture root must not exist. Each child includes sequence and
  draw ordinal and is created exclusively by the existing publication routine.
- Native recording is capped at 64 attempts and 4 MiB per container. The launcher
  places new UUID sessions under ignored `hd2d/local/interactive/`, with private
  session/root permissions. Manual environment users remain responsible for
  choosing private local paths and unique session IDs, as in Sprint 1/2.
- Unsupported frames are rejected once with the existing exact reason; no
  retry loop changes host settings or blocks the simulation on a queue.
- Synchronous capture/publication can add a wall-clock hitch. A background
  writer, configurable shortcut and continuous recording are deliberately deferred.

The old `HD2D_CAPTURE_DRAW` one-shot mode is retained. Interactive mode uses
`HD2D_CAPTURE_INTERACTIVE=1`; specifying both modes is rejected. Enabled-but-unarmed
and compiled-out hosts install no observer and perform no capture I/O.
