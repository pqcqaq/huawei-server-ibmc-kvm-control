# ADR-0002: Implement the virtual-media stack in managed .NET

## Status

Accepted

## Context

The legacy client exposes virtual floppy and optical media through a separate TCP service. The server sends UFI or SFF-8020i commands and the client returns blocks from a local image, directory image, or physical drive. The legacy implementation depends on Java threads and a JNI DLL for physical-device I/O.

The replacement must support the same media sources without loading the legacy x86 runtime or native hook DLL. Large media reads must not block the KVM video or WPF UI threads. Paths, offsets, lengths, and server commands are untrusted inputs.

## Decision

Implement the VMM framing, authentication, optional AES-CBC data protection, UFI/SFF command handling, and file-image backends in managed .NET. Put raw Windows drive access behind a small `SafeFileHandle` adapter using documented `CreateFile` and `DeviceIoControl` calls. Build directory media as a temporary Joliet ISO through `DiscUtils.Iso9660`, then serve it through the same read-only image backend.

Keep the VMM connection independent from the KVM video transport. Expose media operations through an asynchronous session API and keep UI state in the WPF application.

## Consequences

### Positive

- No Java/JNI or global hooks.
- One bounded, testable protocol path for every media source.
- File and network I/O stay off the UI and video receive loops.
- Directory mapping can reuse the stable ISO image path instead of reimplementing the legacy UDF library.

### Negative

- Physical floppy formatting is hardware-dependent and may require elevation.
- Directory images require temporary disk space equal to the generated ISO.
- Exact physical-drive behavior depends on Windows storage-driver support.

### Neutral

- Optical and directory media are always read-only.
- Floppy images default to write-protected and require explicit opt-in for writes.

## Alternatives Considered

**Reuse the legacy JNI DLL**

Rejected because it reintroduces architecture, distribution, and stability risks from the old runtime.

**Use only Redfish virtual media**

Rejected because the target firmware and original client use the VMM TCP protocol and must support floppy and directory sources.

**Implement an in-memory UDF filesystem**

Rejected because it adds substantial filesystem complexity without improving compatibility over a generated Joliet ISO.

## References

- `D:\Projects\ibmc-kvm-dec\decompiled\com\huawei\vm\console`
- `D:\Projects\ibmc-kvm-dec\reports\virtual-media-protocol.md`
