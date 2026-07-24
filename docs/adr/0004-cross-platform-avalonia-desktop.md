# ADR-0004: Add an Avalonia desktop client for Linux

## Status

Accepted

## Context

The existing desktop client is a .NET 9 WPF application. Its protocol and core projects already target platform-neutral .NET, but the desktop project depends on WPF, Windows DPAPI, Windows file dialogs, `user32.dll`, WPF image codecs, and Windows physical-drive APIs. WPF cannot run on Linux.

The initial Linux target is a Kali Rolling x86-64 laptop running GNOME on Xorg. Its active user session exposes X11, a user D-Bus, GNOME Keyring Secret Service, the standard X11 libraries, and sufficient disk and memory. The target must run as the logged-in user without installing a system-wide .NET runtime.

## Decision

Add `IbmcKvm.Desktop`, a .NET 9 Avalonia desktop application that targets Windows and Linux while reusing `IbmcKvm.Protocol` and `IbmcKvm.Core`. Keep `IbmcKvm.App` as the released WPF client until the new client reaches feature parity.

Move UI-independent video decoding and AVI frame encoding out of WPF-specific code. Put OS integration behind narrow services:

- Windows settings continue to use DPAPI in the WPF application.
- Linux settings use an AES-GCM envelope whose random master key is stored in the GNOME Secret Service.
- Linux pointer confinement and recentering use X11 through a small native boundary.
- Linux physical media enumeration uses `/sys/class/block` and opens `/dev` paths with the permissions of the current user.
- File selection uses Avalonia storage-provider APIs.

Publish a self-contained `linux-x64` directory so the target machine does not depend on its installed .NET 6 runtime.

## Consequences

### Positive

- Protocol, cryptography, session, virtual-media, and input state machines remain shared and tested.
- Linux receives a native desktop process without Wine, Java, or a browser-local service.
- The WPF release remains stable while the cross-platform UI is validated.
- Platform capabilities and permission failures can be surfaced explicitly instead of silently disabling features.

### Negative

- Two desktop front ends coexist during the migration.
- UI behavior needs separate Windows WPF and Avalonia smoke coverage.
- X11 pointer capture differs from Wayland; Wayland requires a later portal-based implementation.
- Raw physical media remains subject to Linux device permissions and available hardware.

### Neutral

- Linux package contents are larger because the .NET and Avalonia runtimes are self-contained.
- The first validated Linux target is X11; Wayland is detected and reported as a capability limitation.

## Alternatives Considered

### Electron or a browser shell

Rejected because it would require a second application runtime, a local service boundary, and a rewrite of desktop input and image handling while still calling the existing .NET protocol stack.

### Wine

Rejected because WPF and .NET compatibility under Wine would be an unsupported runtime dependency and would not provide a maintainable Linux implementation for Secret Service, device access, or input capture.

### Replace WPF in place

Rejected because it would make the already published Windows client regress while the Linux port is incomplete. A parallel desktop project makes parity measurable and rollback straightforward.

## References

- `docs/adr/0001-dotnet-wpf-x64.md`
- Avalonia desktop documentation: https://docs.avaloniaui.net/
- Secret Service API: https://specifications.freedesktop.org/secret-service-spec/latest/
