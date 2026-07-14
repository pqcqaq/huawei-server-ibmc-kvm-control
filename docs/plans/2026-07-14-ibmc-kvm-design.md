# iBMC KVM Client Design

## Requirements

The client must log in to the current Huawei iBMC, negotiate a KVM session, display remote video, and send keyboard and mouse input. It must support shared and exclusive connection modes, cancellation, reconnect, screenshots, recording, power actions, and virtual media as compatibility work progresses. The Windows desktop must remain responsive even if the BMC, network, decoder, or native interop stalls.

Non-functional requirements are stricter than the legacy client: Windows x64 only for the first release; no global hooks; bounded connection and read timeouts; asynchronous writes through a bounded queue; no credentials or session material in logs; explicit handling of self-signed certificates; deterministic protocol tests; and clean shutdown within five seconds.

## Architecture

```text
WPF App / MVVM
    | commands + immutable state
    v
Session Orchestrator
    |-- HTTPS login adapter
    |-- KVM TCP transport (reader + bounded writer)
    |-- packet codec / authentication / heartbeat
    |-- video decoder -> latest-frame channel
    `-- input encoder <- focused viewer events only
```

The solution is a modular monolith: `IbmcKvm.App` owns WPF and composition; `IbmcKvm.Core` owns session state and use cases; `IbmcKvm.Protocol` owns wire formats, login parsing, crypto, video and input packets; tests contain golden vectors and fake transports. The UI consumes only immutable connection snapshots and the newest decoded frame. Slow frames are dropped instead of building latency. Network readers, writers, and decoders run independently and communicate through bounded channels. Cancellation and connection loss flow through one session lifetime token.

The first protocol adapter targets the observed iBMC behavior. Variant selection is interface-based, so old iMana support does not contaminate the core path. Hardware validation is an explicit gate because credentials and a real BMC are not stored in the repository.

