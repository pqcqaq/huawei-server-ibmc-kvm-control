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
11. [x] Reverse the active virtual-media UI, VMM framing, PBKDF2/AES, and UFI/SFF command tables.
12. [x] Add KVM-side virtual-media credential, port, and privilege negotiation.
13. [x] Implement managed VMM framing, authentication, heartbeat, and dual-device lifecycle.
14. [x] Add file images, raw physical drives, directory ISO generation, and image creation.
15. [x] Implement and test all UFI/SFF opcodes handled by the original client.
16. [x] Add the WPF virtual-media manager, reconnect, progress, status, and confirmed USB reset.
17. [x] Validate target VMM capability without mounting, ejecting, resetting USB, or changing power state.
18. [x] Run Release builds, protocol/core/app tests, sensitive-file checks, and documentation updates.

Deferred compatibility extensions: recording and hardware injection tests for keyboard/mouse. Destructive virtual-media hardware operations are implemented but are not exercised while the target session is restricted to read-only verification.

Detailed test-first steps are in `docs/plans/2026-07-14-ibmc-kvm-implementation.md` and `docs/plans/2026-07-14-ibmc-virtual-media-implementation.md`.
