# Legacy Feature Parity Design

## Requirements

The client will implement every capability in `docs/legacy-feature-roadmap.md`:
complete encrypted KVM, older iBMC/iMana variants, reconnect, relative/captured
mouse modes, recording, chassis and multi-blade operation, quality/color controls,
special keys and layouts, remote lock indicators, complete power/privilege
behavior, localization/help, and persistent certificate trust management.

All additions preserve the existing safety invariants: no global hooks, no UI
thread I/O or decoding, bounded queues and lengths, cancellable bounded waits,
explicit destructive-operation confirmation, and no credentials or session keys
in logs, fixtures, settings, or exception messages.

## Architecture

```text
WPF windows and localized resources
        |
        v
Console coordinator ---- capability snapshots ----> controls/status
        |
        +---- bounded blade session collection
                    |
                    v
            KVM session orchestrator
                    |
          +---------+----------+
          |                    |
 protocol profile       virtual-media controller
          |
  +-------+--------+----------------+
  |                |                |
plain modern  encrypted modern  legacy iBMC/iMana
  |                |                |
  +------- shared framing/video/input/VMM codecs
```

`IbmcKvm.Protocol` owns packet layouts, cryptographic derivation and transforms,
variant detection results, and protocol capability descriptions. Secret-bearing
objects implement `IDisposable` and zero their buffers. `IbmcKvm.Core` owns
connection/reconnect state machines, bounded frame and recording queues, selected
blade state, and virtual-media restoration. `IbmcKvm.App` renders immutable state
and invokes Core operations.

## Encrypted KVM data flow

The login result supplies the decimal verification value, a 32-byte hexadecimal
decryption key, and an optional extended verification value. The decryption key
is split into a 16-byte user key and 16-byte IV. Cipher-suite negotiation chooses
PBKDF2-HMAC-SHA256, SHA1, or the legacy fallback and derives the 24-byte connect
authenticator from the extended verification value when present.

Encrypted connect uses the original high-bit length field and replaces the normal
four-byte code key with the 24-byte authenticator. Command `0x40` returns 48 bytes
encrypted by the user key/IV. Its plaintext contains a 32-character password and
16-byte salt used to derive 48 bytes of KVM material: data key, input key, and IV.
Each four-byte word is reversed to match the original client.

Metadata video chunks remain plain. Nonzero-index chunks carry a real-length byte
and AES-CBC ciphertext; the profile decrypts and normalizes them before the shared
frame assembler. VMM credential and port responses use the data key. Keyboard and
mouse reports use the input key. Encrypted power commands use the data key and the
original `0x33` wrapper.

## Variant and reconnect model

Connection discovery returns a profile identifier plus endpoints and capabilities.
HTTPS is attempted for modern iBMC. Recognized old-version results select the
managed RMCP+/OEM adapter rather than shelling credentials into a command line.
iMana differences live in a dedicated profile that reuses shared codecs.

Each blade session publishes a reconnect policy. A transient transport failure may
retry with exponential backoff inside a fixed attempt/time budget. The reconnect
token exists only in memory. Successful KVM recovery asks the virtual-media
controller to recreate desired mounts. Authentication, permission, protocol, and
explicit disconnect failures never loop automatically.

## Multi-blade and feature state

The console coordinator owns a bounded dictionary of blade sessions. Each blade
has connection, video, input, permission, recording, quality, and media state.
Only the selected interactive blade receives input; monitor and split-screen
sessions are view-only. UI controls resolve from the selected blade's capability
and permission snapshots.

The active `com.kvm` protocol exposes 14 addressable slots in a two-byte bitmap:
the outer bit in each byte is reserved. State command `0x15` distinguishes
absence, BMC reset, unsupported KVM, KVM busy, SOL, firmware loading, and an
available direct or chassis-relayed endpoint. The coordinator enforces the
original maximum of four simultaneous sessions.

Recording consumes normalized encoded frames through a drop-aware bounded queue.
The `.rep` writer preserves original metadata and indexes; standard export is a
separate encoder consumer so it cannot delay video rendering.

## Failure and security behavior

- Malformed or oversized encrypted fields fault only the owning session and never
  expose decrypted bytes in messages.
- All authentication and reconnect waits have explicit timeouts.
- Key buffers are cloned at ownership boundaries and zeroed during replacement and
  disposal.
- Persistent certificate trust is opt-in, fingerprint-visible, scoped, reviewable,
  and revocable. Changed certificates require a new decision.
- Power, USB reset, writable media, and destructive validation retain confirmation
  gates.

## Verification

Protocol primitives use original-JAR golden vectors plus boundary and malformed
tests. Session behavior uses loopback TCP servers that fragment every handshake
field and assert exact outgoing packets. Core state machines use fake clocks and
transports. WPF state and commands receive executable tests, followed by high-DPI
desktop inspection. Hardware verification is staged as read-only, input, media,
and power tiers, with destructive tiers requiring explicit authorization.
