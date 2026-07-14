# iBMC Virtual Media Design

## Requirements

### Functional

- Mount, change, eject, and reconnect a floppy image.
- Mount a local physical floppy and optionally expose it writable.
- Mount, change, eject, and reconnect an ISO image.
- Mount a local physical optical drive.
- Convert a local directory to a temporary optical image and mount it.
- Create an image file from a physical floppy or optical drive with progress and cancellation.
- Enumerate eligible local drives and report media/session state.
- Support simultaneous floppy and optical devices when the BMC permits it.
- Send the original USB-reset command only after explicit confirmation.

### Non-Functional

- No Java/JNI or system-wide hooks.
- No credentials, negotiated keys, media contents, or target addresses in logs.
- Bounded frame sizes, checked arithmetic, cancellation, and timeouts on every network and file operation.
- Optical and directory sources are read-only. Floppy sources default to write-protected.
- Closing the KVM window disconnects VMM sessions and deletes generated temporary images.
- Media streaming must not block KVM video, input, or the WPF dispatcher.

## Architecture

```text
MainWindow / VirtualMediaWindow
        |
        v
VirtualMediaController
        |
        +--> KvmClientSession: query VMM key, salt, port, privilege
        |
        +--> VirtualMediaSession: auth, device lifecycle, heartbeat
                  |
                  +--> VmmPacketCodec / VmmCrypto
                  +--> ScsiCommandProcessor
                             |
                             +--> FileImageMedia
                             +--> PhysicalDriveMedia
                             +--> GeneratedDirectoryMedia
```

The KVM connection supplies commands `0x31` and `0x35`; responses `0x32` and `0x36` contain the VMM credential/salt and little-endian port. The VMM socket uses a fixed 12-byte header. Authentication derives a 24-byte session ID, 16-byte data key, and 16-byte IV with the negotiated PBKDF2 suite.

After authentication, the BMC requests device type 1 (floppy/UFI) or 2 (optical/SFF). Server requests contain a 12-byte SCSI command. The client returns one or more framed data chunks followed by a command-complete frame. Reads are capped at 4 KiB for floppy and 32 KiB for optical media, matching the original client.

## Media Backends

`IRandomAccessMedia` exposes capacity, block size, write protection, random reads, optional writes, and flush. Session/controller state owns change and eject lifecycle. Image files use asynchronous random access handles. Physical drives use raw Windows handles and storage IOCTLs. Directory sources are first built into a temporary Joliet ISO so they share the optical image behavior and can be deleted reliably on disconnect.

## Failure Modes

| Failure | Behavior |
|---|---|
| VMM privilege denied | Disable mount controls and show the server rejection. |
| Auth/version rejection | Close only VMM; keep KVM video connected. |
| Media removed or unreadable | Return NOT READY / MEDIUM NOT PRESENT sense data. |
| Out-of-range server read | Return ILLEGAL REQUEST without touching the file. |
| Write to protected media | Return DATA PROTECT. |
| Network timeout | Cancel VMM tasks, close media handles, preserve KVM. |
| Directory build cancelled | Delete the partial temporary ISO. |

## Verification

- Golden vectors from Java `ProtocolProcessor` and AES/PBKDF2 oracles.
- Executed UFI/SFF tests for inquiry, capacity, read, write protection, sense data, media change, and chunk framing.
- Loopback VMM server tests for auth, device creation, SCSI exchange, heartbeat, cancellation, and cleanup.
- Local directory-to-ISO mount/read tests and image-copy cancellation tests.
- Read-only hardware query of VMM key, port, and privilege. No target mount/eject/reset under the existing read-only hardware constraint.
