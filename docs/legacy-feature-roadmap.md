# Legacy Feature Parity Roadmap

Status: software implementation complete; authorized hardware and destructive-operation gates remain explicitly pending

Architecture and execution details are recorded in
`docs/adr/0003-capability-oriented-protocol-adapters.md`,
`docs/plans/2026-07-15-legacy-feature-parity-design.md`, and
`docs/plans/2026-07-15-legacy-feature-parity-implementation.md`.

This roadmap records source-verified capabilities in the Huawei client under
`../ibmc-kvm-dec`, the implementation evidence in this project, and the gates
that still require target hardware or explicit authorization. Priority controls
the remaining validation order; it does not make lower-priority validation
optional.

The active reverse-engineering oracle is the non-`V1` `com.kvm` implementation.
The parallel `com.kvmV1` and iMana packages remain compatibility references.

## P0: Protocol compatibility

### Complete encrypted KVM sessions

Implementation status (2026-07-15): automated implementation, original-JAR
vectors, malformed-input checks, and fragmented loopback coverage are complete;
authorized hardware verification remains pending.

- Consume the login decryption key and extended verification value without
  retaining either in diagnostics or settings.
- Implement the encrypted connect and authentication path, including cipher
  negotiation, command `0x40` key material, PBKDF2 key derivation, and key
  byte-order compatibility.
- Decrypt encrypted video chunks and encrypted KVM-side VMM credential and port
  responses before parsing them.
- Encode encrypted control and power messages where the firmware requires them.
- Zero transient key material and cover all transforms with original-JAR golden
  vectors and fragmented-stream tests.

### Older iBMC and iMana variants

Implementation status (2026-07-15): profile detection, managed RMCP+/RAKP,
source-verified OEM commands, legacy iBMC discovery, iMana session-ID framing,
and encrypted iMana KVM loopback coverage are complete; authorized hardware
verification on both firmware families remains pending.

- Detect the firmware/protocol variant instead of assuming the current HTTPS
  generation.
- Support the older RMCP+/OEM login and KVM/VMM discovery path represented by
  `LoginAuthentication` and `SendIPMIInDiffOS`.
- Port the required `com.kvmV1` and iMana packet, video, input, and virtual-media
  differences behind protocol interfaces.
- Preserve the current cancellation, timeout, certificate, and secret-handling
  invariants for every variant.

## P1: Session resilience and core console parity

### KVM and virtual-media recovery

Implementation status (2026-07-15): the reconnect token, bounded supervisor,
automatic MainWindow recovery, progress/final states, active-session replacement,
current mouse/color preservation, and post-KVM virtual-media restoration are
implemented with plain/encrypted token and retry loopback tests. A local-only
150% DPI desktop scenario now covers graceful EOF, visible attempt progress,
successful same-endpoint recovery, bounded exhaustion, and reconnect/close
races. Authorized hardware failure injection and mounted-media restoration
remain pending.

- Parse and retain the server-issued 128-byte reconnect token only for the
  lifetime of the active connection.
- Re-establish KVM after a bounded transient failure without returning to the
  login window.
- Restore previously mounted floppy and optical media after KVM recovery.
- Expose reconnect progress, cancellation, retry limits, and a final actionable
  failure state.

### Relative and captured mouse modes

Implementation status (2026-07-15): relative protocol negotiation, captured
single-mouse interaction, Esc release, explicit local-pointer visibility,
absolute/relative switching, and source-compatible synchronization reports are
implemented without global hooks and covered by state, command, and loopback
tests. Real desktop interaction at 150% DPI covers captured-pointer activation,
hidden local pointer, Esc release, pointer visibility, and fifteen synchronization
reports; authorized hardware inspection remains pending.

- Add a session path that does not require absolute-mode acknowledgement.
- Support relative mouse reports, mouse synchronization, single/captured mouse,
  and explicit absolute/relative mode switching.
- Add show/hide-local-pointer behavior while preserving window-local input and
  never installing a system-wide hook.
- Select or negotiate a usable input mode for older firmware.

### Local video recording

Implementation status (2026-07-15): original controls, bounded `.rep` recording,
linked multi-block I-frame indexes, decoded-frame Motion JPEG AVI export,
resolution normalization, disk/error reporting, and bounded producer behavior
are implemented with byte-level and real WPF JPEG encoding tests. Windows WPF
`MediaElement` opened a generated 320×240/2-second MJPEG AVI and advanced from
0.422s to 1.433s with different screen-frame hashes; no ffprobe installation is
required for this evidence.

- Implement the original `0x40`/`0x41` recording controls and key-frame behavior.
- Record frame metadata, timestamps, resolution changes, quantization selection,
  full frames, and differential frames without blocking receive or decode loops.
- Support the original `.rep` format and define a standard playable export path.
- Bound the recording queue and report disk, permission, and cancellation errors.

### Chassis and multi-blade operation

Implementation status (2026-07-15): the source-verified 14-slot presence
bitmap, state parser, shared/exclusive queries, control/monitor/disconnect
commands, four-session coordinator, selected-command routing, WPF blade panel,
tabs, and read-only 2x2 split view are implemented. Protocol malformed-input,
loopback query/monitor, coordinator ownership, and UI-state tests pass. The
single-node target does not return chassis presence data, so authorized
multi-blade hardware validation remains pending.

- Discover blade presence and state, refresh chassis information, and surface
  absent, busy, SOL, loading, KVM, and unavailable states.
- Connect, monitor, switch, and disconnect individual blades with independent
  session state.
- Add blade tabs and split-screen viewing with a bounded simultaneous-connection
  limit matching the server capability.
- Route input, power, quality, recording, and virtual-media commands to the
  selected blade.

## P2: Console controls and status

### Video quality and color depth

Implementation status (2026-07-15): source-verified DQT and color-depth wire
commands, `0x28` response processing, decoder reset/full-frame recovery, toolbar
selectors, protocol loopback coverage, and UI option tests are complete;
the 150% DPI desktop selector/wire scenario passes, and authorized hardware
validation remains pending.

- Expose the original DQT image-clarity control using command `0x27` and process
  its `0x28` response.
- Support the original 8-bit, 7-bit, 6-bit, and 4-bit color-depth choices during
  connect and reconnect.
- Reset decoder history and request a full frame after changes that invalidate
  differential-frame state.

### Special keys and keyboard layouts

Implementation status (2026-07-15): all original presets, a bounded six-usage
custom editor, guaranteed release reports, focused-viewer US/Japanese/French
mapping, `0x04` lock-state processing, and N/C/S indicators are implemented
with core, loopback, and UI-option tests. The 150% DPI desktop scenario verifies
all presets, layout switching, release reports, lock indicators, and six visible
custom-key slots; authorized hardware inspection remains pending.

- Add presets for Ctrl+Shift, Ctrl+Esc, Ctrl+Alt+Delete, Alt+Tab, Ctrl+Space, and
  keyboard reset.
- Add a custom combination editor for up to six HID usages and always send the
  matching release report.
- Support US, Japanese, and French keyboard mappings while retaining the focused
  viewer input boundary.
- Process command `0x04` and display remote Num Lock, Caps Lock, and Scroll Lock
  state.

### Remaining power and privilege behavior

Implementation status (2026-07-15): the `0x23` forced power-cycle action,
source privilege levels, login-to-session propagation, Core enforcement,
capability-aware toolbar disabling, and operation-specific `0x51` denial events
are implemented with parser, session-loopback, and UI-rule tests. Administrator
and user control availability plus every power-menu entry pass 150% DPI UI
Automation inspection without invoking a power action; authorized hardware
validation remains pending.

- Expose the existing `0x23` forced power-cycle command with an explicit warning
  distinct from reset and graceful shutdown.
- Carry login privilege into the console and disable unavailable KVM, power, and
  virtual-media controls.
- Process server privilege responses, including command `0x51`, and show the
  operation-specific denial instead of silently ignoring it.

## P3: Product completeness

### Localization and help

Implementation status (2026-07-15): 304 resource keys cover static and dynamic
application text in Chinese, English, Japanese, and French. Help covers
connection, input, recording, power, virtual media, and chassis workflows;
About documents protocol compatibility and security boundaries. 48 UI
Automation checks across four languages passed at 150% DPI, including a real
empty-credential error state.

- Move user-facing strings into resources and provide Chinese, English, Japanese,
  and French UI selections.
- Add maintained help content for connection, input modes, recording, power, and
  virtual-media workflows.
- Add an About view with application and protocol compatibility information.

### Persistent certificate trust management

Implementation status (2026-07-15): current-user DPAPI storage supports scoped
server fingerprints and CA imports, displays subject/issuer/validity/SHA-256,
handles review and revocation, rejects changed certificates, and keeps strict
default validation. English, Japanese, French, and Chinese trust-management
windows passed 150% DPI UI Automation and screenshot inspection.

- Let users explicitly import or persist a verified CA or server trust decision.
- Show certificate subject, issuer, validity, SHA-256 fingerprint, and trust scope
  before persistence.
- Provide review and revocation of stored trust without weakening strict default
  validation or silently trusting a changed certificate.

## Verification gates

- Protocol additions require synthetic fixtures, original-client oracle vectors,
  malformed-input coverage, cancellation tests, and bounded-length checks.
- UI additions require executable behavior tests and desktop/high-DPI inspection.
- Hardware validation must separate read-only probes from input, power, and media
  operations that can alter the target.
- No parity feature may reintroduce global hooks, UI-thread network I/O, unbounded
  queues, unbounded waits, or logs containing credentials and session keys.
- A capability is complete only after implementation, tests, documentation, and
  applicable hardware verification are recorded in `progress.md`.

## Current software verification baseline

- Release build: 0 warnings, 0 errors.
- Protocol tests: 166 passed; Core tests: 116 passed; App tests: 90 passed;
  total 372 passed.
- Local-only desktop smoke: 61 checks across four real WPF scenarios at 150%
  DPI, including the single-row icon toolbar at minimum width, capture/Esc
  release, DQT/color wire commands, six custom-key slots, privilege-aware
  controls, graceful-EOF recovery, progress, success, and retry exhaustion. No
  power or USB-reset command is emitted.
- Four-language desktop verification: 48 checks at 150% DPI, with no
  untranslated user-visible text, interactive-control overflow, or trust-header
  overlap.
- External playback: Windows WPF MediaElement opened the generated AVI and
  produced different screen-frame hashes at two playback positions.

The remaining items in `progress.md` are target-dependent or destructive
validation gates, not unimplemented software paths. Power, media mount/eject,
USB reset, and other state-changing operations require explicit authorization.
