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
19. [x] Add opt-in current-user DPAPI storage for the last successful connection, startup restoration, and explicit clearing.
20. [x] Correct modern iBMC keyboard encryption and mouse-mode negotiation, and add a four-color input readiness indicator.
21. [x] Split login and KVM windows, add a loading/error handoff, and move controls into a pinnable auto-hide overlay toolbar.
22. [x] Harden video-surface activation/focus before remote input and add executable UI state tests.
23. [x] Animate floating-toolbar transitions and retain a visible top-edge reveal handle while hidden.
24. [x] Implement the complete encrypted KVM session path, including encrypted video and KVM-side VMM negotiation; hardware verification remains a separate read-only gate.
25. [x] Add older iBMC/iMana variant detection and managed RMCP+/OEM compatibility adapters; authorized legacy hardware verification remains pending.
26. [x] Add bounded KVM reconnect, reconnect-token handling, and virtual-media restoration; desktop and authorized hardware failure injection remain pending.
27. [x] Add relative/captured mouse modes, cursor synchronization, and local-pointer controls; desktop/high-DPI and hardware inspection remain pending.
28. [x] Add local recording with original recording controls, `.rep` compatibility, and standard AVI export; Windows WPF MediaElement playback inspection passed.
29. [x] Add source-compatible 14-slot chassis discovery, bounded four-session management, monitoring, selected-command routing, tabs, and read-only split-screen viewing; authorized multi-blade hardware validation remains pending.
30. [x] Add DQT image-quality and selectable color-depth controls; authorized hardware and desktop/high-DPI verification remain pending.
31. [x] Add the complete special-key set, custom combinations, keyboard layouts, and remote lock-state indicators; desktop/high-DPI and hardware inspection remain pending.
32. [x] Expose forced power cycle and make KVM, power, and virtual-media controls privilege-aware; desktop/high-DPI and hardware inspection remain pending.
33. [x] Add Chinese, English, Japanese, and French resources, maintained help, and an About view; 303 keys and 150% DPI four-language checks pass.
34. [x] Add explicit persistent certificate trust import, inspection, review, and revocation; scoped DPAPI records reject changed certificates.

All scoped legacy-parity software capabilities are implemented and prioritized in `docs/legacy-feature-roadmap.md`; remaining entries are validation gates. Destructive virtual-media hardware operations are implemented but are not exercised while the target session is restricted to read-only verification.

Detailed test-first steps are in `docs/plans/2026-07-14-ibmc-kvm-implementation.md` and `docs/plans/2026-07-14-ibmc-virtual-media-implementation.md`.
