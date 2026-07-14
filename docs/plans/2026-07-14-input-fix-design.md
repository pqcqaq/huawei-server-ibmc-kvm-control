# Remote Input Fix

## Problem

The first implementation copied the legacy plain HID layout but missed the current iBMC's `isNew=true` keyboard path. The server expects the 8-byte boot-protocol report encrypted to a 16-byte AES-CBC block derived from the session code key. The client also sent mouse-mode value `2`, which the original program uses to query mode, while assuming it had selected absolute mode. Keyboard packets were therefore rejected and mouse behavior depended on pre-existing server state.

## Design

`IbmcKvm.Protocol` now owns the original code-key AES algorithm and exposes explicit legacy-plain and code-key-encrypted keyboard encodings. The current connection defaults to the encrypted modern encoding. Session setup sends mouse-mode value `1` and does not report the connection ready until command `0x25` confirms absolute mode. Original-JAR oracle vectors and a loopback TCP session test verify the exact outgoing bytes.

The WPF layer resolves one input availability state from connection failure, active video, pointer position over the rendered image, viewer focus, and window activation. The same state gates actual keyboard/mouse events and drives the lower-left indicator: gray disconnected, red failed, blue connected but inactive, and green ready. Transitioning away from green sends encrypted all-keys-up and mouse-button release reports, including when the pointer moves into letterboxing, focus moves to another control, or the window deactivates.

## Verification

Pure tests cover state precedence, every inactive condition, readiness, and exact RGB mappings. Protocol tests compare AES output with `com.kvm.AESHandler.encry`; integration tests require absolute-mode acknowledgement and inspect emitted keyboard/mouse packets. Target verification uses only a Shift press/release and mouse movement without characters or clicks, and confirms live blue-to-green readiness transitions. The complete Release suite, WPF XAML compilation, UI Automation tree, published startup, and dependency scan form the final verification set.
