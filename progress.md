# Progress

Last updated: 2026-07-14

## In progress

- [ ] Optional recording extension

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

## Pending

- [ ] Hardware keyboard/mouse injection validation (not run under the read-only constraint)
- [ ] Optional recording extension
- [ ] Destructive target validation of mount/eject/USB reset (intentionally not run under the read-only constraint)

## Verification log

- Reverse artifact payload SHA-256: `238775201099AFFAF606DE26261A0057FD1E8613B2D0DC14CA9C687A969FEC1C`
- Toolchain: .NET SDK 9.0.304; Windows WPF template available
- `dotnet build IbmcKvm.slnx --configuration Release --no-restore`: passed, 0 warnings, 0 errors
- `dotnet build IbmcKvm.slnx --configuration Release --no-restore -p:OutputPath=artifacts/solution-verify/`: passed, 0 warnings, 0 errors
- `dotnet test IbmcKvm.slnx --configuration Release --no-build -p:OutputPath=artifacts/solution-verify/`: passed, 164 tests
  - Protocol: 77
  - Core: 58
  - App/video/UI/settings: 29
- Legacy JPEG header oracle: all 10 table indexes match at 698 bytes each
- Read-only hardware capture: 720×400, table index 6, 0 assembled-frame errors; visual inspection passed
- Read-only VMM query: capability available; credential/salt lengths valid; suite 3; data encryption disabled by login flag
