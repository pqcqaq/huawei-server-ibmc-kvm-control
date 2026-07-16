# ADR-0003: Use capability-oriented protocol profiles

## Status

Accepted

## Context

Supported iBMC and iMana firmware generations differ in authentication, packet
framing, encryption, reconnect behavior, input modes, virtual media, and chassis
features. Encoding those differences as WPF conditionals would couple the UI to
wire details and make cancellation, secret lifetime, and failure handling harder
to verify.

The solution uses a modular boundary: App owns presentation, Core owns session
workflows, and Protocol owns wire formats and cryptography.

## Decision

Represent firmware differences with protocol profiles composed from explicit
capabilities. A profile owns wire authentication, packet transforms, supported
input modes, reconnect behavior, and optional commands. Core exposes one stable,
asynchronous console API to WPF.

Disposable Protocol objects own secret-bearing cryptographic state. Core
coordinates handshakes without logging or persisting key material. App renders
capability snapshots and never parses wire packets.

The chassis coordinator deliberately limits the application to four concurrent
sessions. Each blade owns its transport, decoder, cancellation token, reconnect
replacement, media controller, and display state. Interactive commands target
the selected control session; monitor sessions and the 2x2 split view are
read-only.

## Consequences

### Positive

- Protocol variants share video, input, recording, and virtual-media code.
- Cryptographic state has an explicit lifetime and is cleared on disposal.
- Unsupported controls can be disabled from negotiated capabilities.
- Loopback and fixed-vector tests can exercise profiles without hardware or WPF.

### Negative

- Session construction requires profile selection and capability negotiation.
- The compatibility matrix must be maintained with fixtures and hardware
  validation across firmware versions.

## Alternatives considered

- Add firmware branches directly to `KvmClientSession`: rejected because wire
  transforms and feature policy would accumulate in one receive loop.
- Create unrelated sessions for each firmware generation: rejected because
  duplicated video, input, and media code would drift.
- Split profiles into separate processes: rejected because the added deployment
  and IPC complexity does not improve the current trust boundary.

## Relevant code

- `src/IbmcKvm.Protocol/Profiles/`
- `src/IbmcKvm.Core/Session/KvmClientSession.cs`
- `src/IbmcKvm.Core/Session/ChassisConsoleCoordinator.cs`
