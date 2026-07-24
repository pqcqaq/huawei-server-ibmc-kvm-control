#!/usr/bin/env python3
import ctypes
import sys
import time


class ClientMessageData(ctypes.Union):
    _fields_ = [
        ("bytes", ctypes.c_byte * 20),
        ("shorts", ctypes.c_short * 10),
        ("longs", ctypes.c_long * 5),
    ]


class ClientMessageEvent(ctypes.Structure):
    _fields_ = [
        ("type", ctypes.c_int),
        ("serial", ctypes.c_ulong),
        ("send_event", ctypes.c_int),
        ("display", ctypes.c_void_p),
        ("window", ctypes.c_ulong),
        ("message_type", ctypes.c_ulong),
        ("format", ctypes.c_int),
        ("data", ClientMessageData),
    ]


class XEvent(ctypes.Union):
    _fields_ = [("client", ClientMessageEvent), ("padding", ctypes.c_long * 24)]


def main() -> int:
    if len(sys.argv) < 4 or len(sys.argv) > 8:
        raise SystemExit(
            "usage: x11_smoke.py WINDOW_ID CENTER_X CENTER_Y "
            "[KEYSYM[,KEYSYM...]] [--ctrl] [--alt] [--keyboard-only] [--pointer-only]"
        )

    window = int(sys.argv[1], 0)
    center_x = int(sys.argv[2])
    center_y = int(sys.argv[3])
    options = sys.argv[4:]
    keysym_names = (options[0] if options and not options[0].startswith("--") else "a").split(",")
    use_control = "--ctrl" in options
    use_alt = "--alt" in options
    keyboard_only = "--keyboard-only" in options
    pointer_only = "--pointer-only" in options
    x11 = ctypes.CDLL("libX11.so.6")
    xtst = ctypes.CDLL("libXtst.so.6")
    x11.XOpenDisplay.restype = ctypes.c_void_p
    x11.XRaiseWindow.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
    x11.XDefaultScreen.argtypes = [ctypes.c_void_p]
    x11.XRootWindow.argtypes = [ctypes.c_void_p, ctypes.c_int]
    x11.XRootWindow.restype = ctypes.c_ulong
    x11.XInternAtom.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int]
    x11.XInternAtom.restype = ctypes.c_ulong
    x11.XSendEvent.argtypes = [
        ctypes.c_void_p,
        ctypes.c_ulong,
        ctypes.c_int,
        ctypes.c_long,
        ctypes.POINTER(XEvent),
    ]
    x11.XQueryTree.argtypes = [
        ctypes.c_void_p,
        ctypes.c_ulong,
        ctypes.POINTER(ctypes.c_ulong),
        ctypes.POINTER(ctypes.c_ulong),
        ctypes.POINTER(ctypes.POINTER(ctypes.c_ulong)),
        ctypes.POINTER(ctypes.c_uint),
    ]
    x11.XFree.argtypes = [ctypes.c_void_p]
    x11.XSetInputFocus.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
    x11.XFlush.argtypes = [ctypes.c_void_p]
    x11.XStringToKeysym.argtypes = [ctypes.c_char_p]
    x11.XStringToKeysym.restype = ctypes.c_ulong
    x11.XKeysymToKeycode.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
    x11.XKeysymToKeycode.restype = ctypes.c_uint
    x11.XCloseDisplay.argtypes = [ctypes.c_void_p]
    xtst.XTestFakeMotionEvent.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_ulong]
    xtst.XTestFakeButtonEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_bool, ctypes.c_ulong]
    xtst.XTestFakeKeyEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_bool, ctypes.c_ulong]
    display = x11.XOpenDisplay(None)
    if not display:
        raise RuntimeError("unable to open DISPLAY")

    try:
        screen = x11.XDefaultScreen(display)
        root_window = x11.XRootWindow(display, screen)
        active_window = x11.XInternAtom(display, b"_NET_ACTIVE_WINDOW", 0)
        activate = XEvent()
        activate.client.type = 33
        activate.client.send_event = 1
        activate.client.display = display
        activate.client.window = window
        activate.client.message_type = active_window
        activate.client.format = 32
        activate.client.data.longs[0] = 1
        x11.XSendEvent(
            display,
            root_window,
            0,
            (1 << 20) | (1 << 19),
            ctypes.byref(activate),
        )
        root = ctypes.c_ulong()
        parent = ctypes.c_ulong()
        children = ctypes.POINTER(ctypes.c_ulong)()
        child_count = ctypes.c_uint()
        if x11.XQueryTree(
            display,
            ctypes.c_ulong(window),
            ctypes.byref(root),
            ctypes.byref(parent),
            ctypes.byref(children),
            ctypes.byref(child_count),
        ):
            x11.XRaiseWindow(display, parent)
            if children:
                x11.XFree(children)
        else:
            x11.XRaiseWindow(display, ctypes.c_ulong(window))
        x11.XSetInputFocus(display, ctypes.c_ulong(window), 1, 0)
        x11.XFlush(display)
        time.sleep(0.1)
        if not keyboard_only:
            xtst.XTestFakeMotionEvent(display, -1, center_x, center_y, 0)
            xtst.XTestFakeButtonEvent(display, 1, True, 0)
            xtst.XTestFakeButtonEvent(display, 1, False, 0)
        x11.XFlush(display)
        time.sleep(0.25)

        if use_control and not pointer_only:
            control_keysym = x11.XStringToKeysym(b"Control_L")
            control_keycode = x11.XKeysymToKeycode(display, control_keysym)
            xtst.XTestFakeKeyEvent(display, control_keycode, True, 0)
        if use_alt and not pointer_only:
            alt_keysym = x11.XStringToKeysym(b"Alt_L")
            alt_keycode = x11.XKeysymToKeycode(display, alt_keysym)
            xtst.XTestFakeKeyEvent(display, alt_keycode, True, 0)

        if not pointer_only:
            for keysym_name in keysym_names:
                keysym = x11.XStringToKeysym(keysym_name.encode("ascii"))
                keycode = x11.XKeysymToKeycode(display, keysym)
                xtst.XTestFakeKeyEvent(display, keycode, True, 0)
                xtst.XTestFakeKeyEvent(display, keycode, False, 0)
        if use_control and not pointer_only:
            xtst.XTestFakeKeyEvent(display, control_keycode, False, 0)
        if use_alt and not pointer_only:
            xtst.XTestFakeKeyEvent(display, alt_keycode, False, 0)
        if not keyboard_only:
            xtst.XTestFakeMotionEvent(display, -1, center_x + 30, center_y + 20, 0)
            xtst.XTestFakeMotionEvent(display, -1, center_x - 20, center_y - 15, 0)
            xtst.XTestFakeButtonEvent(display, 3, True, 0)
            xtst.XTestFakeButtonEvent(display, 3, False, 0)
        x11.XFlush(display)
        time.sleep(0.5)
    finally:
        x11.XCloseDisplay(display)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
