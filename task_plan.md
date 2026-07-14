# Task Plan

1. Scaffold the solution and enforce x64, nullable reference types, warnings, and test coverage.
2. Port login response parsing and endpoint construction with redacted structured diagnostics.
3. Implement TCP packet framing, authentication packets, heartbeats, cancellation, and bounded queues.
4. Port the legacy packet unpacker and video decompression into span-based C# with golden-vector tests.
5. Render decoded frames through a reusable WPF viewer with frame coalescing and no UI-thread decoding.
6. Encode window-local keyboard and mouse input without global hooks.
7. Add connection state, reconnect policy, exclusive/shared mode, and user-facing error handling.
8. Add power actions, screenshots, recording, and virtual-media protocol support.
9. Run unit, integration, soak, cancellation, and UI smoke tests.
10. Validate against the target iBMC and record firmware-specific compatibility evidence.

Detailed test-first steps are in `docs/plans/2026-07-14-ibmc-kvm-implementation.md`.

