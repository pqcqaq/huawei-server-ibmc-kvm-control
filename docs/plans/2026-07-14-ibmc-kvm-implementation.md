# iBMC KVM Client Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build a Windows x64 iBMC client that logs in, displays remote KVM video, and sends keyboard/mouse input without global hooks.

**Architecture:** A WPF modular monolith separates UI, session orchestration, and wire protocol code. Independent bounded channels isolate network read/write, decoding, and frame presentation so a stalled BMC cannot block the UI or desktop input.

**Tech Stack:** .NET 9, C# 13, WPF, `System.Threading.Channels`, `System.IO.Pipelines`, `HttpClient`, xUnit.

---

### Task 1: Solution scaffold and invariant tests

**Files:**
- Create: `IbmcKvm.slnx`
- Create: `src/IbmcKvm.App/IbmcKvm.App.csproj`
- Create: `src/IbmcKvm.Core/IbmcKvm.Core.csproj`
- Create: `src/IbmcKvm.Protocol/IbmcKvm.Protocol.csproj`
- Create: `tests/IbmcKvm.Protocol.Tests/IbmcKvm.Protocol.Tests.csproj`
- Create: `tests/IbmcKvm.Core.Tests/IbmcKvm.Core.Tests.csproj`

**Steps:**
1. Generate projects and add references.
2. Add a failing architecture test that protocol assemblies do not reference WPF.
3. Add common build properties for nullable, analyzers, deterministic builds, and x64.
4. Run `dotnet test`; expect all scaffold tests to pass.
5. Commit after checking `git status` for secrets.

### Task 2: Address parsing and HTTPS login

**Files:**
- Create: `src/IbmcKvm.Protocol/Login/IbmcEndpoint.cs`
- Create: `src/IbmcKvm.Protocol/Login/LoginRequest.cs`
- Create: `src/IbmcKvm.Protocol/Login/LoginResponse.cs`
- Create: `src/IbmcKvm.Protocol/Login/LegacyLoginResponseParser.cs`
- Create: `src/IbmcKvm.Protocol/Login/IbmcLoginClient.cs`
- Test: `tests/IbmcKvm.Protocol.Tests/Login/*Tests.cs`

**Steps:**
1. Write table-driven tests for IPv4, bracketed IPv6, ports, and invalid input.
2. Port the observed response field order into a parser with named fields.
3. Test representative success and error response vectors.
4. Implement cancellable POST to `/bmc/php/processparameter.php` with explicit certificate policy.
5. Verify logs redact passwords, verify values, and keys.

### Task 3: Packet framing and session authentication

**Files:**
- Create: `src/IbmcKvm.Protocol/Wire/PacketReader.cs`
- Create: `src/IbmcKvm.Protocol/Wire/PacketWriter.cs`
- Create: `src/IbmcKvm.Protocol/Wire/KvmPacket.cs`
- Create: `src/IbmcKvm.Protocol/Session/KvmAuthenticator.cs`
- Test: `tests/IbmcKvm.Protocol.Tests/Wire/*Tests.cs`

**Steps:**
1. Extract packet constants and golden byte vectors from the decompiled `PackData`, `UnPackData`, and `Client` classes.
2. Write fragmented-read and coalesced-packet tests.
3. Implement span-based framing with maximum packet limits.
4. Implement authentication and heartbeat packets from golden vectors.
5. Test malformed lengths, cancellation, EOF, and authentication failure.

### Task 4: Asynchronous transport

**Files:**
- Create: `src/IbmcKvm.Protocol/Transport/KvmTransport.cs`
- Create: `src/IbmcKvm.Protocol/Transport/KvmConnectionOptions.cs`
- Test: `tests/IbmcKvm.Protocol.Tests/Transport/KvmTransportTests.cs`

**Steps:**
1. Write loopback tests for stalled readers/writers and cancellation.
2. Implement independent reader/writer tasks and a bounded outbound channel.
3. Add connect, idle, heartbeat, and shutdown timeouts.
4. Verify a saturated writer never blocks the caller or UI thread.

### Task 5: Video decode pipeline

**Files:**
- Create: `src/IbmcKvm.Protocol/Video/*`
- Test: `tests/IbmcKvm.Protocol.Tests/Video/*Tests.cs`

**Steps:**
1. Port frame metadata and block decoding from `UnPackData`, `DrawThread`, and image classes.
2. Produce golden decoded pixel buffers from captured/decompiled vectors.
3. Implement a bounded latest-frame channel and cancellation.
4. Fuzz malformed blocks and verify size limits.

### Task 6: Input encoding without global hooks

**Files:**
- Create: `src/IbmcKvm.Protocol/Input/*`
- Create: `src/IbmcKvm.App/Controls/KvmViewer.xaml`
- Test: `tests/IbmcKvm.Protocol.Tests/Input/*Tests.cs`

**Steps:**
1. Port HID key mapping and mouse packet formats into pure functions.
2. Test modifiers, key release, absolute/relative mouse, wheel, and focus loss.
3. Capture WPF preview events only inside the focused viewer.
4. On focus loss, send an all-keys-up packet and release mouse capture.
5. Verify no call to `SetWindowsHookEx` or input DLL exists in the published binary.

### Task 7: Session orchestration and WPF UI

**Files:**
- Create: `src/IbmcKvm.Core/Session/*`
- Create: `src/IbmcKvm.App/ViewModels/MainWindowViewModel.cs`
- Modify: `src/IbmcKvm.App/MainWindow.xaml`
- Test: `tests/IbmcKvm.Core.Tests/Session/*Tests.cs`

**Steps:**
1. Test state transitions: idle, connecting, authenticating, connected, reconnecting, failed, disconnected.
2. Implement one cancellable session owner and immutable snapshots.
3. Bind login, connect, disconnect, viewer, and diagnostics UI.
4. Coalesce frames onto the dispatcher without decoding on it.
5. Perform a UI smoke test and inspect a screenshot.

### Task 8: Feature parity controls

**Files:**
- Create: `src/IbmcKvm.Protocol/Power/*`
- Create: `src/IbmcKvm.Protocol/VirtualMedia/*`
- Create: `src/IbmcKvm.Core/Recording/*`
- Test: corresponding test directories

**Steps:**
1. Port and test power command packets.
2. Implement screenshot and recording from decoded frames.
3. Port virtual-media negotiation and bounded streaming.
4. Keep media I/O cancellable and off the UI thread.

### Task 9: End-to-end verification and packaging

**Files:**
- Create: `tests/IbmcKvm.IntegrationTests/*`
- Create: `scripts/verify.ps1`
- Modify: `README.md`, `progress.md`

**Steps:**
1. Run full tests and a loopback soak test.
2. Publish self-contained `win-x64` output.
3. Scan imports and source for global-hook APIs and leaked secrets.
4. Validate connect, video, input, reconnect, screenshot, power, and virtual media against hardware.
5. Record firmware/model compatibility and remaining limitations.
6. Check `git status`, exclude sensitive configuration, and commit the verified result.

