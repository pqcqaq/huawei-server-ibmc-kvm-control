# Progress

Last updated: 2026-07-14

## In progress

- [ ] Phase 4: Video packet reconstruction and WPF frame rendering

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

## Pending

- [x] HTTPS login and session negotiation
- [x] KVM packet framing and asynchronous transport
- [ ] KVM authentication packet exchange
- [ ] Video decode and frame rendering
- [ ] Window-local keyboard and mouse input
- [ ] Reconnect, diagnostics, and cancellation
- [ ] Power controls, screenshots, recording, and virtual media
- [ ] Hardware-in-the-loop validation against the target iBMC

## Verification log

- Reverse artifact payload SHA-256: `238775201099AFFAF606DE26261A0057FD1E8613B2D0DC14CA9C687A969FEC1C`
- Toolchain: .NET SDK 9.0.304; Windows WPF template available
- `dotnet build IbmcKvm.slnx --configuration Release --no-restore`: passed, 0 warnings, 0 errors
- `dotnet test IbmcKvm.slnx --configuration Release --no-build`: passed, 2 tests
- `dotnet test tests/IbmcKvm.Protocol.Tests/IbmcKvm.Protocol.Tests.csproj --configuration Release`: passed, 42 tests
