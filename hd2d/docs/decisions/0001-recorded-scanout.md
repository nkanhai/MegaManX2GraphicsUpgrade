# ADR 0001 — Begin with recorded X2 scanout observations

Date: 2026-11-17. Status: accepted for the reversible Sprint 0 prototype and
recommended for Sprint 1; live runtime and Unity architecture remain open.

## Context

The authoritative runtime is LLE-first. Its X2-specific draw loop consumes
PPU state line by line and performs HDMA between lines. Existing TCP probes
mix live reads, pre-raster history and copied pixels. Shared raster-journal
APIs exist but are not used by X2's current draw loop.

## Decision

Preserve raw graphics facts and explicit capture phase. Start with local
recorded frames observed at existing render boundaries; derive normalized
render instructions later. Keep simulation independent. Use an intentionally
restricted synthetic format to test serialization without touching the runtime.
Do not create a Unity skeleton or native ABI in Sprint 0.

## Consequences

This costs storage and copy time but makes failures reproducible and keeps
unknown semantic mappings out of the first interface. A runtime format needs
per-line memory association, configuration and completeness rather than an
end-of-frame struct dump. It must pair state and reference pixels itself.

Reject separate TCP polling as production frame capture; consider TCP only as
a request/status control plane. Defer native-library vs IPC and URP 2D vs
Universal Renderer decisions until recorded fidelity and platform tests exist.
Those decisions materially affect maintenance and require owner review with
measured evidence. No approval is needed to complete the isolated prototype.

See [bridge proposal](../architecture/BRIDGE_PROPOSAL.md) for the precise
capture contract, estimates and acceptance boundaries.
