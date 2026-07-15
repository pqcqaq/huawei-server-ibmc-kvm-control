# ADR-0003: Use capability-oriented protocol adapters

## Status

Accepted

## Context

The current client has one `KvmClientSession` optimized for the verified modern,
unencrypted iBMC path. The legacy Huawei client also supports encrypted KVM,
older iBMC and iMana variants, reconnect tokens, relative input, recording, and
multi-blade chassis sessions. Adding those behaviors as UI conditionals or as
unstructured branches in the session receive loop would couple protocol details
to WPF and make secret lifetime, cancellation, and failure handling difficult to
verify.

The existing modular-monolith boundary remains valid: WPF owns presentation,
Core owns session use cases, and Protocol owns wire formats and cryptography.

## Decision

Represent firmware differences through protocol profiles composed from explicit
capabilities. A profile owns wire authentication, packet transforms, supported
input modes, reconnect behavior, and optional features. Core sessions consume
the selected profile and expose one stable, asynchronous console API to WPF.

Secret-bearing cryptographic state is held by disposable Protocol objects. Core
coordinates handshake ordering and never logs or persists key material. UI code
uses capability snapshots to expose or disable controls and never parses wire
commands.

The migration is incremental:

1. Extract modern plain/encrypted session cryptography and handshake transforms.
2. Move reconnect and input-mode differences behind profile capabilities.
3. Add older iBMC/iMana adapters without changing the application-facing session
   contract.
4. Generalize the console coordinator from one blade to a bounded collection of
   independently owned sessions.

The chassis coordinator owns at most four sessions, matching the original
client's server-limit handling. The management session performs the 14-slot
presence and state refresh. Each connected blade owns its transport, decoder,
cancellation token, reconnect replacement, media controller, and display state.
Only one slot is selected; interactive commands resolve through that slot.
Monitor slots and the 2x2 split presentation are read-only.

Connecting a fifth slot fails before opening a transport. Disconnect removes and
disposes only the addressed slot, then selects a remaining slot deterministically.
Reconnect atomically replaces coordinator ownership before the failed transport
is disposed. A failure in one secondary slot does not dispose other blade
sessions. Closing the console cancels all consumers, closes media controllers,
then disposes every coordinator-owned session.

## Consequences

### Positive

- The already verified modern path remains available while encrypted and legacy
  variants are added.
- Cryptographic state has an explicit lifetime and can be zeroed on disposal.
- UI and virtual-media code depend on capabilities rather than firmware checks.
- Protocol variants can share framing, video, SCSI, and input implementations.
- Loopback and golden-vector tests can exercise adapters without WPF or hardware.

### Negative

- Session construction gains an explicit profile-selection step.
- Some current `KvmClientSession` responsibilities must move into smaller
  protocol components before multi-blade support is added.
- Exact support matrices must be maintained as reverse-engineering evidence
  grows.

### Neutral

- The solution remains a modular monolith; no new process or service boundary is
  introduced.
- Windows remains the first application target while protocol and Core libraries
  stay desktop-framework independent.

## Alternatives Considered

**Add firmware branches directly to `KvmClientSession`**

Rejected because encryption, reconnect, mouse modes, recording, and multi-blade
state would accumulate in one receive loop and be hard to test independently.

**Create unrelated session implementations for every firmware generation**

Rejected because the variants share most framing, video, input, and virtual-media
behavior. Full duplication would make fixes drift between generations.

**Mechanically port the Java class hierarchy**

Rejected because it would reproduce global state, synchronous I/O, native hooks,
and UI/protocol coupling that this replacement exists to remove.

## References

- `docs/legacy-feature-roadmap.md`
- `docs/plans/2026-07-15-legacy-feature-parity-design.md`
- `../ibmc-kvm-dec/decompiled/com/kvm/AESHandler.java`
- `../ibmc-kvm-dec/decompiled/com/kvm/PackData.java`
- `../ibmc-kvm-dec/decompiled/com/kvm/BladeThread.java`
