# ADR-0001: Use .NET 9 WPF and window-local input

## Status

Accepted

## Context

iBMC KVM is a Windows desktop application that needs low-latency video
presentation, focused keyboard and mouse input, cancellable network operations,
and testable protocol code.

## Decision

Use .NET 9 WPF targeting `win-x64`. Capture keyboard and mouse events only while
the remote viewer has focus. Run login, TCP I/O, packet parsing, decoding, and
recording asynchronously behind bounded queues. Keep protocol code independent
from WPF.

## Consequences

### Positive

- The application does not install system-wide keyboard or mouse hooks.
- Protocol and decoder logic can be tested without opening the desktop UI.
- Windows input, imaging, cancellation, and diagnostics APIs are available.
- Self-contained Windows x64 publishing is supported.

### Negative

- The desktop application is Windows-only.
- Operating-system key combinations require explicit toolbar commands.

## Alternatives considered

- Rust with egui or Tauri: strong systems tooling, but higher UI and imaging
  integration cost for this application.
- JavaFX: portable, but less direct integration with Windows input, DPAPI, and
  physical-drive APIs.
