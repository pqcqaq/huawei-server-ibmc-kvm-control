# iBMC KVM

A clean-room Windows x64 client for Huawei iBMC remote console access.

The application is being rebuilt from observed behavior and protocol analysis. It does not reuse the legacy Java runtime or its global keyboard hook. The first compatibility target is the currently used iBMC generation, with protocol version detection kept behind interfaces for later expansion.

## Safety invariants

- No system-wide keyboard or mouse hooks.
- No network I/O, decoding, or blocking waits on the WPF UI thread.
- Passwords and session keys are never written to logs or project configuration.
- Every connection can be cancelled and has bounded timeouts.
- Self-signed iBMC certificates require an explicit per-connection trust decision.

See `task_plan.md` and `progress.md` for the active implementation state.

## Implemented console functions

- HTTPS login with explicit per-session certificate fingerprint confirmation.
- Shared or exclusive KVM sessions with negotiated cipher-suite selection.
- Huawei 64×64 JPEG/RLE video blocks, differential frames, and resolution changes.
- Window-local USB HID keyboard input and absolute mouse input.
- Ctrl+Alt+Delete, release-all-keys, screenshots, and full-screen viewing.
- Power commands with a separate confirmation dialog. Hardware verification never invokes them.

## Run

```powershell
dotnet run --project src/IbmcKvm.App/IbmcKvm.App.csproj --configuration Release
```

The built executable is under `src/IbmcKvm.App/bin/Release/net9.0-windows/win-x64/`.
Credentials are entered at runtime and are not saved.
