# ADR-0005: Add a Rust Linux remote-desktop agent

## Status

Accepted

## Context

The desktop client can control Huawei iBMC hardware but cannot directly control a Linux desktop. Reimplementing an iBMC server would couple a general remote-desktop agent to an undocumented hardware protocol and would inherit commands such as power and virtual media that the Linux agent must not expose.

## Decision

Add an independent Agent Protocol v1 over TLS. Implement the Linux service in Rust and add a separate Agent session and console to the Avalonia client. The first supported host is a logged-in X11 session using X11 capture and XTEST input. The protocol carries bounded JPEG tile frames, USB HID keyboard reports, normalized absolute mouse reports, capabilities, heartbeats, and keyframe requests.

Authentication uses a high-entropy pairing token inside the authenticated TLS channel. The client must explicitly confirm or previously trust the server certificate fingerprint. The Agent exposes no power, USB reset, arbitrary command execution, virtual media, clipboard, file transfer, or shell API.

## Consequences

- Existing iBMC behavior and wire compatibility remain unchanged.
- Rust owns Linux capture/input integration and can later add PipeWire or DRM implementations behind traits.
- The first release controls only a running X11 desktop session and cannot operate firmware, a powered-off host, or an unattended Wayland consent dialog.
- Full-frame JPEG tile comparison is simpler than a hardware encoder but uses more CPU and bandwidth; the protocol can add another negotiated codec later.
