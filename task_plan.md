# Task Plan

1. [x] Scaffold the solution and enforce x64, nullable reference types, warnings, and test coverage.
2. [x] Port login response parsing and endpoint construction with redacted diagnostics.
3. [x] Implement TCP packet framing, authentication, cipher negotiation, heartbeat, cancellation, and bounded queues.
4. [x] Port frame assembly and Huawei 64×64 JPEG/RLE block decoding with legacy-oracle tests.
5. [x] Render decoded frames in WPF without decoding or network waits on the UI thread.
6. [x] Encode window-local keyboard and absolute mouse input without global hooks.
7. [x] Add connection state, shared/exclusive mode, certificate confirmation, and user-facing errors.
8. [x] Add confirmed power actions, screenshots, full-screen viewing, and key-release controls.
9. [x] Run protocol, core, desktop, cancellation, and hardware video tests.
10. [x] Validate read-only video against the target iBMC and record compatibility evidence.

Deferred compatibility extensions: recording, virtual media, and hardware injection tests for keyboard/mouse. They are isolated from the completed view/control transport and are not exercised while the hardware session is restricted to read-only verification.

Detailed test-first steps are in `docs/plans/2026-07-14-ibmc-kvm-implementation.md`.
