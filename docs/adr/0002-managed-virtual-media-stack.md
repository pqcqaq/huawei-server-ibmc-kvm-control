# ADR-0002: Implement virtual media in managed .NET

## Status

Accepted

## Context

iBMC KVM exposes virtual floppy and optical media over a dedicated TCP
connection. The application must serve local images, directories, and physical
drives without blocking KVM video or the WPF UI. Paths, offsets, lengths, and
remote commands are treated as untrusted input.

## Decision

Implement VMM framing, authentication, optional AES-CBC protection, UFI and
SFF-8020i command handling, and image backends in managed .NET. Put physical
Windows drive access behind a small `SafeFileHandle` adapter using documented
`CreateFile` and `DeviceIoControl` calls.

Build directory media as a temporary Joliet ISO with `DiscUtils.Iso9660` and
serve it through the read-only image backend. Keep the VMM connection independent
from KVM video transport and expose media operations through asynchronous Core
APIs.

## Consequences

### Positive

- One bounded, testable protocol path supports every media source.
- File and network I/O stay off the UI and video receive loops.
- Directory mapping reuses the same ISO path as image-backed optical media.
- Optical and directory media remain read-only.

### Negative

- Physical floppy operations depend on Windows storage drivers and may require
  elevation.
- Directory images require temporary disk space.

## Alternatives considered

- Redfish-only virtual media: rejected because the required floppy, directory,
  and physical-drive workflows are not consistently available through Redfish.
- An in-memory UDF implementation: rejected because it adds filesystem
  complexity without improving the supported workflows.

## Relevant code

- `src/IbmcKvm.Protocol/VirtualMedia/`
- `src/IbmcKvm.Core/VirtualMedia/`
- `src/IbmcKvm.App/VirtualMediaWindow.xaml`
