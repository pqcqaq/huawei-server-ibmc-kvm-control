# Progress

Last updated: 2026-07-16

## In progress

- [ ] Legacy feature parity validation; all scoped software capabilities are implemented and remaining hardware gates are tracked in `docs/legacy-feature-roadmap.md`

## Completed

- [x] Legacy PE/JAR/native DLL extraction copied to `D:\Projects\ibmc-kvm-dec`
- [x] Root cause of legacy desktop hangs identified: 32-bit global `WH_KEYBOARD` hook installed from a Java UI thread
- [x] Architecture selected: .NET 9, WPF, Windows x64, async protocol pipeline
- [x] Design, ADR, and implementation plan written
- [x] .NET 9 solution scaffolded with WPF app, protocol/core libraries, and xUnit projects
- [x] Release/x64 build passes with zero warnings
- [x] Architecture tests prevent protocol/core layers from referencing desktop UI frameworks
- [x] HTTPS login request/response parsing with redacted diagnostics
- [x] CRC-16-H, legacy packet encoding, and incremental stream framing
- [x] Bounded asynchronous TCP connection with cancellation and loopback integration tests
- [x] Cipher-suite negotiation (`0x42`/`0x43`/`0x44`) and KVM session handshake
- [x] Certificate SHA-256 fingerprint probe and per-session pinning
- [x] Keyboard HID reports, absolute mouse packets, heartbeat, and confirmed power packets
- [x] WPF operational console with shared/exclusive sessions, screenshot, and full-screen controls
- [x] Video chunk assembly with firmware unchanged-frame marker handling
- [x] 64×64 JPEG/RLE block decoder with prior/above/left block reuse
- [x] All ten synthetic JPEG headers match the original JAR byte-for-byte
- [x] Hardware video verified at 720×400 with quantization-table index 6 and correct color/block layout
- [x] VMM `0x31`/`0x35` negotiation with bounded credential, salt, port, and privilege parsing
- [x] VMM 12-byte framing and Java-oracle PBKDF2/AES compatibility vectors
- [x] File image, raw physical drive, directory-to-Joliet ISO, and physical-media image creation backends
- [x] UFI floppy and SFF-8020i optical processors with original inquiry/capacity/TOC/sense behavior
- [x] Independent VMM TCP session with authentication, heartbeat, simultaneous devices, reconnect, and cleanup
- [x] WPF virtual-media window with image/physical/directory sources, change/eject, progress, and confirmed USB reset
- [x] Read-only target VMM capability query: valid 20-byte credential, 16-byte salt, suite 3, positive iterations, plain data mode
- [x] Opt-in DPAPI-encrypted restoration of the last successful connection and explicit local-settings removal
- [x] Modern input fix: code-key AES keyboard packets, acknowledged absolute mouse mode, focus-safe release, and four-color input readiness indicator
- [x] Non-destructive target input validation: encrypted Shift press/release, mouse movement, and live blue/green readiness transitions without clicks or characters
- [x] Redesigned two-window UX: independent login/loading/error window, full-area video console, pinnable auto-hide toolbar, and disconnect-to-login flow
- [x] Hardened input focus acquisition on window activation, pointer entry, and first mouse click
- [x] Published and reconnected the redesigned client; verified pinned toolbar, 720×400 video, green input readiness, absolute mouse movement, and Shift press/release without clicks, characters, media, or power actions
- [x] Added reduced-motion-aware toolbar slide/fade transitions and a persistent top-edge reveal handle for auto-hide mode
- [x] Encrypted KVM session path: suite negotiation, extended authenticator framing, PBKDF2/AES session material, encrypted video/VMM normalization, encrypted keyboard/mouse/power, and key-buffer zeroing
- [x] Capability-oriented modern, legacy iBMC, and iMana protocol profiles with managed RMCP+/RAKP discovery, OEM login/port/encryption commands, iMana session-ID framing, and source-oracle crypto vectors
- [x] Source-compatible `.rep` recording writer, bounded recording queue, original recording controls, and toolbar save workflow
- [x] Source-verified DQT clarity and 8/7/6/4-bit color-depth commands, `0x28` response handling, decoder reset/full-frame recovery, toolbar selectors, and executable tests
- [x] Full special-key presets, bounded six-key custom editor, guaranteed release reports, US/Japanese/French mappings, and remote Num/Caps/Scroll Lock indicators refreshed after each lock-key toggle
- [x] Forced `0x23` power cycle, login privilege propagation, Core permission enforcement, capability-aware toolbar state, and operation-specific `0x51` denial handling
- [x] MainWindow automatic KVM recovery with bounded retry progress, plain/encrypted reconnect-token handling, active-session replacement, and post-KVM virtual-media restoration
- [x] Relative and captured/single-mouse modes, Esc release, explicit local-pointer visibility, and original absolute/relative synchronization reports without global hooks
- [x] Standard Motion JPEG AVI export with bounded decoded-frame encoding, fixed-resolution normalization, disk-backed AVI indexing, and original `.rep` multi-index coverage
- [x] Source-verified 14-slot chassis discovery and state parsing, shared/exclusive queries, control/monitor/disconnect commands, bounded four-session coordination, selected-blade command routing, blade tabs, and read-only 2x2 split view
- [x] Chinese/English/Japanese/French localization for static and dynamic application text, maintained help, About/protocol compatibility view, and 150% DPI verification
- [x] Scoped DPAPI certificate trust management for server fingerprints and CAs, certificate inspection/revocation, changed-certificate rejection, and 150% DPI verification
- [x] Standard MJPEG AVI playback through Windows WPF MediaElement with advancing timeline and different decoded screen frames
- [x] Unexpected graceful KVM EOF is treated as a reconnectable failure; frame-channel faults, silent chassis probing, and reconnect/window-close cancellation races are handled without an unobserved desktop exception
- [x] Local-only 150% DPI desktop smoke: 56 executable UI Automation, wire, input, layout, permission, reconnect-progress/success/exhaustion, and screenshot checks across four scenarios; no power or USB-reset command is emitted

## Pending

- [ ] P0: hardware verification of the completed encrypted KVM session path (automated implementation and oracle coverage are complete)
- [ ] P0: authorized hardware verification of the implemented older iBMC/iMana RMCP+ and KVM adapters
- [ ] P1: authorized hardware failure-injection validation of automatic reconnect and mounted-media restoration (desktop KVM recovery passes)
- [ ] P1: authorized hardware validation of captured mouse, synchronization, and pointer release (desktop/high-DPI passes)
- [ ] P1: authorized multi-blade chassis hardware validation (the available single-node target does not return chassis presence data)
- [ ] P2: authorized hardware validation of the implemented DQT and 8/7/6/4-bit color-depth controls (desktop/high-DPI passes)
- [ ] P2: authorized hardware inspection of the implemented special keys, layouts, and lock indicators (desktop/high-DPI passes)
- [ ] P2: authorized hardware validation of forced power cycle and privilege-aware controls (desktop menu/permission inspection passes without invoking power)
- [ ] Destructive target validation of mount/eject/USB reset (intentionally not run under the read-only constraint)

## Verification log

- Reverse artifact payload SHA-256: `238775201099AFFAF606DE26261A0057FD1E8613B2D0DC14CA9C687A969FEC1C`
- Toolchain: .NET SDK 9.0.304; Windows WPF template available
- `dotnet build IbmcKvm.slnx --configuration Release --no-restore`: passed, 0 warnings, 0 errors
- `dotnet build IbmcKvm.slnx --configuration Release --no-restore -p:OutputPath=artifacts/solution-verify/`: passed, 0 warnings, 0 errors
- `dotnet test IbmcKvm.slnx --configuration Release --no-restore`: passed, 218 tests before the final encrypted-session boundary additions
  - Protocol: 111
  - Core: 61
  - App/video/UI/settings/input: 46
- `dotnet test tests/IbmcKvm.Core.Tests/IbmcKvm.Core.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~KvmClientSessionTests`: passed, 8 encrypted/plain session tests
- Encrypted KVM loopback: suite 3 negotiation, exact 24-byte authenticator, fragmented `0x40` key material, encrypted video, keyboard, mouse, power, and VMM credential/port vectors all matched the original-JAR oracle
- `dotnet test IbmcKvm.slnx --configuration Release --no-restore`: passed, 247 tests after protocol-profile and RMCP+/iMana integration
  - Protocol: 137
  - Core: 64
  - App/video/UI/settings/input: 46
- Original V1 JAR oracle: iMana PBKDF2 session ID, word-reversed data/input/IV keys, encrypted keyboard report, and encrypted video payload match managed output
- iMana loopback: 24-byte session-ID framing, V1 connect payload, mouse-mode acknowledgement, encrypted video normalization, and encrypted keyboard send path passed
- DQT/color-depth loopback: source-compatible `0x27` quality request and `0x28` confirmation, `0x1B` depth change, decoder reset, and full-frame requests passed
- Keyboard loopback: `0x04` lock-state query/response, post-toggle Caps/Num refresh, source-compatible preset reports, bounded custom usages, cancellation-safe release reports, and layout mappings passed
- Privilege loopback: callback/user/operator/admin policy, client-side enforcement, power denial state 1, virtual-media denial states 2/3, and dynamic toolbar rules passed
- Reconnect loopback: bounded transient retry, authentication stop, cancellation, same-endpoint token reuse, encrypted 128-byte token decryption, mouse/color preservation, and media-after-KVM ordering passed
- Mouse loopback: relative reports, absolute `FFFF/FFFF` synchronization, fifteen `-127/-127` relative synchronization reports, capture state, pointer visibility, and Esc release rules passed
- Recording verification: `.rep` linked indexes beyond 20 I-frames, bounded drop behavior, RIFF/MJPG/`idx1` bytes, valid WPF JPEG chunks, and resolution normalization passed
- Chassis verification: 14-slot reserved-bit mapping, absent/reset/unsupported/KVM busy/SOL/loading/available states, shared/exclusive queries, monitor/stop commands, four-session limit, cleanup, replacement ownership, selected-control routing, and read-only split rules passed
- Chassis desktop inspection: connected 720x400 console and complete toolbar inspected at 150% DPI; UI Automation found zero button or selector bounds outside the 1920x1200 window. The target exposed no chassis bitmap, so live tabs/split remain a multi-blade hardware gate.
- Current full suite: 378 passed (Protocol 166, Core 122, App 90); Release build 0 warnings, 0 errors
- Dynamic localization behavior: 304 keys in each language, parameterized status/certificate/reconnect formatting, and no canonical-key translation collisions passed
- Desktop loopback smoke: 64 checks across administrator controls, user permissions, graceful-EOF reconnect success, and retry exhaustion at 150% DPI; Caps/Num emit HID `0x39`/`0x53` and re-query remote lock state, UI Automation found no required control missing or interactive control outside its window, all six custom key slots are visible, and the captured wire contains no `0x20`/`0x21`/`0x22`/`0x23`/`0x25`/`0x30` command
- Desktop localization verification: 48 UI Automation/screenshot checks across zh-CN, en-US, ja-JP, and fr-FR at 150% DPI; no untranslated text, interactive overflow, or trust-header overlap
- External AVI playback: Windows WPF MediaElement opened the generated 320x240/2-second MJPEG sample; playback positions 0.422s and 1.433s yielded different screen-frame SHA-256 hashes
- Java input oracle: AES-CBC keyboard payload vectors match `com.kvm.AESHandler.encry` from the original JAR
- Live target input verification: 720×400 video, absolute-mode acknowledgement, encrypted key release and Shift press/release, mouse movement, blue inactive state, green ready state, no connection failure
- Redesigned UI verification: high-DPI login layout and connected full-video console inspected; pinned toolbar visible; 720×400 video stable; final input probe remained green after mouse movement and Shift press/release
- Legacy JPEG header oracle: all 10 table indexes match at 698 bytes each
- Read-only hardware capture: 720×400, table index 6, 0 assembled-frame errors; visual inspection passed
- Read-only VMM query: capability available; credential/salt lengths valid; suite 3; data encryption disabled by login flag
