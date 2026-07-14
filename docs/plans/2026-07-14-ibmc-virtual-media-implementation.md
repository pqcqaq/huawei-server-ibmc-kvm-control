# iBMC Virtual Media Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add all virtual floppy, virtual optical, local-directory, physical-drive, image-creation, media-change, and USB-reset capabilities exposed by the original client.

**Architecture:** Extend the existing KVM session only for VMM negotiation. Run the independent VMM protocol and SCSI command server in `IbmcKvm.Core`, backed by bounded random-access media abstractions. Keep WPF responsible only for user selection, confirmation, progress, and status.

**Tech Stack:** .NET 9, C# 13, async TCP, `System.Security.Cryptography`, Windows storage P/Invoke, WPF, xUnit, DiscUtils.Iso9660.

---

### Task 1: Record protocol vectors

**Files:**
- Create: `D:\Projects\ibmc-kvm-dec\reports\virtual-media-protocol.md`
- Create: `D:\Projects\ibmc-kvm-dec\tools\VmmOracle.java`

1. Extract all 12-byte frame layouts, constants, PBKDF2 inputs, AES padding, packet-size limits, and SCSI command tables.
2. Generate Java golden vectors for connect, device, close, data, completion, PBKDF2, and AES.
3. Run the oracle and record hashes/hex fixtures without credentials.

### Task 2: KVM-side VMM negotiation

**Files:**
- Modify: `src/IbmcKvm.Protocol/Session/KvmCommandBuilder.cs`
- Modify: `src/IbmcKvm.Core/Session/KvmClientSession.cs`
- Test: `tests/IbmcKvm.Protocol.Tests/Session/KvmCommandBuilderTests.cs`
- Test: `tests/IbmcKvm.Core.Tests/Session/KvmClientSessionTests.cs`

1. Add failing vectors for commands `0x31` and `0x35` and responses `0x32`, `0x36`, and `0x51`.
2. Implement bounded response parsing and a cancellable `GetVirtualMediaEndpointAsync` API.
3. Verify query timeout, denial, negotiated port, credential, salt, and selected PBKDF2 suite.

### Task 3: VMM framing and cryptography

**Files:**
- Create: `src/IbmcKvm.Protocol/VirtualMedia/VmmPacket.cs`
- Create: `src/IbmcKvm.Protocol/VirtualMedia/VmmPacketCodec.cs`
- Create: `src/IbmcKvm.Protocol/VirtualMedia/VmmCredentialDeriver.cs`
- Test: `tests/IbmcKvm.Protocol.Tests/VirtualMedia/VmmPacketCodecTests.cs`
- Test: `tests/IbmcKvm.Protocol.Tests/VirtualMedia/VmmCredentialDeriverTests.cs`

1. Add failing golden-vector and fragmented-stream tests.
2. Implement the 12-byte header codec with a strict maximum payload.
3. Implement PBKDF2 and AES-CBC zero padding from Java oracle vectors.
4. Verify malformed lengths, invalid suite data, cancellation, and encrypted partial blocks.

### Task 4: Media backends

**Files:**
- Create: `src/IbmcKvm.Core/VirtualMedia/IRandomAccessMedia.cs`
- Create: `src/IbmcKvm.Core/VirtualMedia/FileImageMedia.cs`
- Create: `src/IbmcKvm.Core/VirtualMedia/PhysicalDriveMedia.cs`
- Create: `src/IbmcKvm.Core/VirtualMedia/DirectoryIsoMedia.cs`
- Create: `src/IbmcKvm.Core/VirtualMedia/MediaImageCreator.cs`
- Test: `tests/IbmcKvm.Core.Tests/VirtualMedia/*Tests.cs`

1. Add executed tests for bounds, block size, write protection, file changes, cancellation, and cleanup.
2. Implement file images and physical-drive enumeration/raw access.
3. Add DiscUtils.Iso9660 and build temporary Joliet ISOs from directories.
4. Implement cancellable physical-media image copying with progress.

### Task 5: UFI and SFF command processors

**Files:**
- Create: `src/IbmcKvm.Core/VirtualMedia/Scsi/*`
- Test: `tests/IbmcKvm.Core.Tests/VirtualMedia/Scsi/*Tests.cs`

1. Add failing command tests for every opcode handled by the original processors.
2. Implement inquiry, sense, readiness, capacity, mode sense/select, read, floppy write/format, start/stop, prevent removal, TOC, and unsupported-command responses.
3. Verify transfer limits, LBA overflow, media change, write protection, and completion status.

### Task 6: Virtual-media session

**Files:**
- Create: `src/IbmcKvm.Core/VirtualMedia/VirtualMediaSession.cs`
- Create: `src/IbmcKvm.Core/VirtualMedia/VirtualMediaController.cs`
- Test: `tests/IbmcKvm.Core.Tests/VirtualMedia/VirtualMediaSessionTests.cs`

1. Build a loopback VMM server test with fragmented frames.
2. Implement authentication, device creation, simultaneous devices, SCSI dispatch, heartbeats, change/eject, reconnect, and graceful close.
3. Verify KVM independence, cleanup, bounded queues, timeout, and disconnect races.

### Task 7: WPF virtual-media experience

**Files:**
- Create: `src/IbmcKvm.App/VirtualMediaWindow.xaml`
- Create: `src/IbmcKvm.App/VirtualMediaWindow.xaml.cs`
- Modify: `src/IbmcKvm.App/MainWindow.xaml`
- Modify: `src/IbmcKvm.App/MainWindow.xaml.cs`
- Test: `tests/IbmcKvm.App.Tests/VirtualMedia/*Tests.cs`

1. Add a toolbar command and a focused, non-nested media-management window.
2. Add independent floppy and optical sections with source selector, path/drive picker, write-protect control, mount/change/eject, status, and progress.
3. Add create-image and confirmed USB-reset commands.
4. Verify long paths, disabled states, cancellation, disconnect cleanup, and text fit at desktop scaling.

### Task 8: Validation and delivery

**Files:**
- Modify: `README.md`
- Modify: `progress.md`
- Modify: `task_plan.md`

1. Query VMM capability on the target without mounting media or resetting USB.
2. Run Release build and all protocol/core/app tests.
3. Scan staged files for credentials and sensitive configuration.
4. Commit code, tests, reverse reports, and documentation together.
