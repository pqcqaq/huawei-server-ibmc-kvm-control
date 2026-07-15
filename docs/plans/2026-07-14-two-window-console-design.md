# Two-window KVM console design

## User flow

`LoginWindow` owns address, credentials, session mode, certificate trust, encrypted remembered settings, and the asynchronous connection attempt. The form is disabled while the attempt runs and a determinate status message is replaced by an indeterminate loading bar. A successful attempt creates the KVM session, then constructs and shows `MainWindow` before closing the login window. A failure keeps the form open and exposes the error inline so the user can correct it without reopening the application.

`MainWindow` receives an already-connected `KvmClientSession`; it does not contain login controls or a second login path. Its client area is the remote video surface. Closing the floating disconnect button disposes the session first, then shows a new login window populated from the encrypted settings store.

## Toolbar behavior

The toolbar is a sibling overlay above the video surface so its buttons never participate in video hit testing. The left toggle controls `FloatingToolbarState`: pinned mode keeps it visible, while unpinned mode hides it after the pointer leaves and a short delay elapses. Show and hide transitions use a 160 ms opacity and vertical-position animation; the client falls back to immediate state changes when Windows client-area animations are disabled. Once hidden, a centered 120 x 7 pixel green handle remains visible inside the top reveal zone and above remote-video overlays. The handle and the first 72 device-independent pixels of the console reveal the toolbar again. The rightmost button always disconnects; the middle buttons expose virtual media, screenshot, keyboard release/ Ctrl+Alt+Delete, full-screen, and confirmed power actions.

## Input safety

The video surface remains the only input target. On window activation, pointer entry, and the first pointer press it explicitly activates the window and requests keyboard focus. `RemoteInputAvailability` still requires a live frame, pointer inside the mapped image, viewer focus, active window, and no connection failure before sending reports. Losing that state releases held keyboard and mouse buttons and updates the lower-left four-color indicator.

## Verification

- `LoginPresentationTests` execute loading and failure transitions.
- `FloatingToolbarStateTests` execute pin, delayed-hide, reveal, and show/hide transition decisions.
- Existing input-state tests remain in the application suite.
- Release build and all solution tests run with an alternate output directory while an existing client process is left untouched.
