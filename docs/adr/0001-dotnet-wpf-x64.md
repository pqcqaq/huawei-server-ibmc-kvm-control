# ADR-0001: Use .NET 9 WPF and a window-local input model

## Status

Accepted

## Context

The legacy client is a 32-bit Java 8 application. It installs a desktop-wide `WH_KEYBOARD` hook from a Swing event thread. On 64-bit Windows, stalled message pumping can block unrelated 64-bit applications. The replacement is Windows-only and needs low-latency rendering, input, cancellation, and maintainable protocol code.

## Decision

Use .NET 9 WPF targeting `win-x64`. Capture keyboard and mouse events only when the viewer control has focus. Put login, TCP I/O, packet parsing, decoding, and recording on asynchronous workers behind bounded channels. Keep protocol code independent from WPF.

## Consequences

### Positive

- Native Windows x64 process with no cross-bitness hook callback path.
- Mature desktop input, imaging, cancellation, and diagnostics APIs.
- Protocol and decoder logic can be tested without the UI.
- Self-contained publishing is available.

### Negative

- First release is Windows-only.
- Special operating-system key combinations require explicit UI commands rather than interception.
- Legacy video and virtual-media formats still require protocol porting.

## Alternatives considered

- Rust with egui/Tauri: excellent systems safety, but higher UI and imaging integration cost for this recovery project.
- Modern Java/JavaFX: fastest mechanical port, but retains runtime/native baggage and makes it easier to reproduce the old threading design.
- Reuse the legacy DLL hook: rejected because it is the proven cross-process failure mechanism.

