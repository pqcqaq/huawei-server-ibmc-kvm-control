# Legacy Feature Parity Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement and verify every capability in `docs/legacy-feature-roadmap.md` without weakening the existing safety invariants.

**Architecture:** Add capability-oriented protocol profiles inside the existing Protocol/Core/App modular monolith. Protocol owns wire and cryptographic differences, Core owns bounded session state machines and multi-blade coordination, and WPF consumes capability and permission snapshots.

**Tech Stack:** .NET 9, C# 13, WPF, `System.Security.Cryptography`, bounded `Channel<T>`, xUnit, loopback TCP integration tests, original-JAR oracle vectors.

---

### Task 1: Freeze the encrypted KVM cryptographic contract

**Files:**
- Create: `src/IbmcKvm.Protocol/Session/KvmSessionCryptography.cs`
- Create: `tests/IbmcKvm.Protocol.Tests/Session/KvmSessionCryptographyTests.cs`
- Reference: `../ibmc-kvm-dec/decompiled/com/kvm/AESHandler.java`

**Steps:**

1. Generate synthetic original-JAR vectors for PBKDF2 derivation, four-byte word reversal, connect authentication, session-key decryption, video/VMM decryption, input encryption, and power encryption.
2. Write failing xUnit theories for SHA1 and SHA256 suites, exact lengths, invalid hex, invalid ciphertext blocks, and disposal.
3. Implement a disposable cryptographic owner that splits the login key, derives authenticators/session keys, performs AES-CBC/no-padding transforms, and zeroes buffers.
4. Run `dotnet test tests/IbmcKvm.Protocol.Tests/IbmcKvm.Protocol.Tests.csproj --configuration Release --filter KvmSessionCryptographyTests` and require all tests to pass.

### Task 2: Add encrypted connect framing and command codecs

**Files:**
- Modify: `src/IbmcKvm.Protocol/Wire/LegacyPacketEncoder.cs`
- Modify: `src/IbmcKvm.Protocol/Session/KvmCommandBuilder.cs`
- Create: `src/IbmcKvm.Protocol/Session/KvmEncryptedPayloadCodec.cs`
- Test: `tests/IbmcKvm.Protocol.Tests/Wire/LegacyPacketEncoderTests.cs`
- Test: `tests/IbmcKvm.Protocol.Tests/Session/KvmCommandBuilderTests.cs`

**Steps:**

1. Write failing exact-byte tests for the high-bit encrypted connect header and 24-byte authenticator.
2. Implement bounded encrypted-connect framing without changing plain framing.
3. Write failing tests for `0x40` key-material parsing, encrypted video normalization, VMM response normalization, session-key input reports, and `0x33` power wrapping.
4. Implement the codecs with exact block and real-length validation.
5. Run the Protocol test project and require zero failures.

### Task 3: Integrate encrypted KVM into the session

**Files:**
- Modify: `src/IbmcKvm.Core/Session/KvmClientSession.cs`
- Modify: `src/IbmcKvm.App/LoginWindow.xaml.cs`
- Test: `tests/IbmcKvm.Core.Tests/Session/KvmClientSessionTests.cs`

**Steps:**

1. Add the raw verification value and login decryption key to connection options with validation for encrypted sessions.
2. Write a loopback test that negotiates suite 3, asserts the encrypted connect bytes, returns fragmented `0x40` key material, streams encrypted video, and inspects encrypted input/power packets.
3. Make encrypted setup wait for valid session key material before input or VMM use.
4. Normalize encrypted video and VMM responses before existing parsers and frame assembly.
5. Pass login fields into the session and zero transient local buffers.
6. Run Core, Protocol, and App tests.

### Task 4: Introduce protocol profiles and legacy discovery

**Files:**
- Create: `src/IbmcKvm.Protocol/Profiles/IKvmProtocolProfile.cs`
- Create: `src/IbmcKvm.Protocol/Profiles/ModernKvmProtocolProfile.cs`
- Create: `src/IbmcKvm.Protocol/Profiles/ImanaKvmProtocolProfile.cs`
- Create: `src/IbmcKvm.Protocol/Login/IbmcProtocolDiscovery.cs`
- Create: `src/IbmcKvm.Protocol/Login/RmcpOemLoginClient.cs`
- Test: `tests/IbmcKvm.Protocol.Tests/Profiles/*`
- Test: `tests/IbmcKvm.Protocol.Tests/Login/*`

**Steps:**

1. Define immutable capabilities for encryption, video codec, input modes, reconnect, recording, blades, VMM, power, and quality controls.
2. Move modern plain/encrypted decisions behind one profile without changing behavior.
3. Port RMCP+ packet construction and response parsing as managed code; never place credentials on a process command line.
4. Port source-verified iMana packet and login differences behind its profile.
5. Add discovery fallback and exact synthetic response tests.
6. Run all Protocol and architecture tests.

### Task 5: Add bounded reconnect and media restoration

**Files:**
- Create: `src/IbmcKvm.Core/Session/KvmReconnectPolicy.cs`
- Create: `src/IbmcKvm.Core/Session/KvmSessionSupervisor.cs`
- Modify: `src/IbmcKvm.Core/VirtualMedia/VirtualMediaController.cs`
- Test: `tests/IbmcKvm.Core.Tests/Session/KvmSessionSupervisorTests.cs`

**Steps:**

1. Write state-machine tests for transient failure, authentication failure, cancellation, retry exhaustion, and successful recovery.
2. Parse and own the 128-byte reconnect token with zero-on-dispose semantics.
3. Reconnect within bounded attempts/time and publish progress snapshots.
4. Restore desired floppy/optical mounts only after KVM authentication succeeds.
5. Run Core integration tests with a reconnecting fake server.

### Task 6: Complete input modes, special keys, layouts, and LEDs

**Files:**
- Modify: `src/IbmcKvm.Core/Session/KvmClientSession.cs`
- Modify: `src/IbmcKvm.Core/Input/*`
- Create: `src/IbmcKvm.Core/Input/KeyboardLayoutProfile.cs`
- Modify: `src/IbmcKvm.App/MainWindow.xaml`
- Modify: `src/IbmcKvm.App/MainWindow.xaml.cs`
- Test: `tests/IbmcKvm.Core.Tests/Input/*`
- Test: `tests/IbmcKvm.App.Tests/Input/*`

**Steps:**

1. Add negotiated absolute and relative session input strategies.
2. Implement relative/captured input, synchronization, and local-pointer visibility without global hooks.
3. Add preset and custom HID combinations with guaranteed release reports.
4. Add US, Japanese, and French mapping profiles.
5. Parse command `0x04` into immutable Num/Caps/Scroll state and render indicators.
6. Run input/session tests and inspect capture/release behavior in the desktop app.

### Task 7: Implement recording

**Files:**
- Create: `src/IbmcKvm.Core/Recording/RepRecordingWriter.cs`
- Create: `src/IbmcKvm.Core/Recording/ConsoleRecorder.cs`
- Modify: `src/IbmcKvm.Core/Session/KvmClientSession.cs`
- Modify: `src/IbmcKvm.App/MainWindow.xaml*`
- Test: `tests/IbmcKvm.Core.Tests/Recording/*`

**Steps:**

1. Write byte-for-byte tests for `.rep` header, frame, timestamp, and sequence-index records.
2. Add `0x40`/`0x41` recording control commands distinct from encrypted-auth response handling by direction.
3. Feed normalized encoded frames into a bounded recording channel and surface dropped-frame/disk errors.
4. Add start/stop/save UI and standard export behind an asynchronous encoder.
5. Run long-recording, resolution-change, cancellation, and disk-failure tests.

### Task 8: Add chassis and multi-blade coordination

**Files:**
- Create: `src/IbmcKvm.Core/Chassis/ChassisConsoleCoordinator.cs`
- Create: `src/IbmcKvm.Core/Chassis/BladeConsoleState.cs`
- Modify: `src/IbmcKvm.App/MainWindow.xaml*`
- Test: `tests/IbmcKvm.Core.Tests/Chassis/*`
- Test: `tests/IbmcKvm.App.Tests/Ui/*`

**Steps:**

1. Implement blade present/state/monitor/disconnect/reply codecs and parsers.
2. Write coordinator tests for independent sessions, bounded concurrency, selected input target, monitor-only sessions, and cleanup.
3. Add blade tabs, refresh, status, and split-screen views with stable responsive dimensions.
4. Route power, quality, recording, and media operations to the selected blade.
5. Run coordinator and WPF behavior tests, then inspect desktop screenshots.

### Task 9: Add remaining console controls and permissions

**Files:**
- Modify: `src/IbmcKvm.Protocol/Session/KvmCommandBuilder.cs`
- Modify: `src/IbmcKvm.Core/Session/*`
- Modify: `src/IbmcKvm.App/MainWindow.xaml*`
- Test: `tests/IbmcKvm.Protocol.Tests/Session/*`
- Test: `tests/IbmcKvm.App.Tests/Ui/*`

**Steps:**

1. Add DQT `0x27/0x28`, color-depth connect/reconnect, and decoder reset behavior.
2. Expose forced power cycle `0x23` with a distinct confirmation.
3. Carry login and `0x51` permission state into capability snapshots.
4. Disable unavailable controls and surface operation-specific denials.
5. Run exact command tests and UI state tests.

### Task 10: Add localization, help, About, and persistent trust

**Files:**
- Create: `src/IbmcKvm.App/Resources/Strings.*.xaml`
- Create: `src/IbmcKvm.App/Help/*`
- Create: `src/IbmcKvm.App/Settings/CertificateTrustStore.cs`
- Modify: `src/IbmcKvm.App/LoginWindow.xaml*`
- Test: `tests/IbmcKvm.App.Tests/Settings/CertificateTrustStoreTests.cs`
- Test: `tests/IbmcKvm.App.Tests/Ui/LocalizationTests.cs`

**Steps:**

1. Extract all visible strings and add Chinese, English, Japanese, and French resources.
2. Add localized help and About views with compatibility information.
3. Store explicit CA/server trust records with DPAPI integrity, fingerprint scope, review, and revocation.
4. Require a new decision when identity or fingerprint changes.
5. Run resource completeness, trust-store corruption, replacement, and revocation tests.

### Task 11: Complete verification and evidence

**Files:**
- Modify: `README.md`
- Modify: `progress.md`
- Modify: `task_plan.md`
- Modify: `docs/legacy-feature-roadmap.md`

**Steps:**

1. Run `dotnet test IbmcKvm.slnx --configuration Release` and require zero warnings/failures.
2. Run original-JAR oracle comparisons for every new wire/crypto format.
3. Publish x64 and inspect login, console, recording, relative input, multi-blade, localization, and trust workflows at normal and high DPI.
4. Perform read-only hardware validation, then separately authorize and record input, media, and power validation.
5. Mark roadmap items complete only where source, automated tests, UI evidence, and applicable hardware evidence all agree.
6. Check `git status`, ensure no credential/token configuration is staged, then commit code, tests, and documentation together.
