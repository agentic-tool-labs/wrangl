#!/usr/bin/env python3
"""
The terminal client on a pseudo-terminal of its own: entering sets the alternate screen, hides the cursor and turns mouse reporting
(presses and the wheel, SGR coordinates) on while the line discipline is raw; q, Ctrl-C, SIGINT, SIGTERM and SIGHUP each turn mouse
reporting off, leave the alternate screen, leave the cursor visible, restore the line discipline and exit 0; a resize redraws at the new
size. Terminal replies the client did not ask for (cursor position, colour, keyboard protocol, device attributes) are consumed without
acting on them. The terminal is a pty this script creates; nothing here touches the user's terminals.

usage: tui-pty.py <path to ahd-tui>        (the environment, HOME and AH_HOME included, is the caller's sandbox)
"""
import fcntl
import os
import pty
import select
import signal
import struct
import sys
import termios
import time

BIN = sys.argv[1]
ENTER = ["\x1b[?1049h", "\x1b[?25l", "\x1b[?1000h", "\x1b[?1006h"]
MOUSE_OFF_THEN_NORMAL_SCREEN = ["\x1b[?1006l", "\x1b[?1000l", "\x1b[?1049l"]
PROBE_REPLIES = b"\x1b[12;40R\x1b]10;rgb:ffff/ffff/ffff\x1b\\\x1b[?0u\x1b[?62;4c"

failures = 0
children = {}


def report(name, ok, detail=""):
    global failures
    if not ok:
        failures += 1
    print(("PASS: " if ok else "FAIL: ") + name + ((" -- " + detail) if (detail and not ok) else ""), flush=True)


def spawn(cols=100, rows=30, label=""):
    pid, fd = pty.fork()
    if pid == 0:
        os.execv(BIN, [BIN])
    fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", rows, cols, 0, 0))
    children[pid] = label
    return pid, fd


def read_until(fd, pred, timeout=6.0):
    buf = b""
    end = time.time() + timeout
    while time.time() < end:
        r, _, _ = select.select([fd], [], [], 0.1)
        if r:
            try:
                chunk = os.read(fd, 65536)
            except OSError:
                break
            if not chunk:
                break
            buf += chunk
        if pred(buf.decode("utf-8", "replace")):
            break
    return buf.decode("utf-8", "replace")


def drain(fd, quiet=0.4, timeout=3.0):
    """Everything the child writes until it has been quiet for `quiet` seconds or closed its end."""
    buf = b""
    last = time.time()
    end = time.time() + timeout
    while time.time() < end:
        r, _, _ = select.select([fd], [], [], 0.1)
        if r:
            try:
                chunk = os.read(fd, 65536)
            except OSError:
                break
            if not chunk:
                break
            buf += chunk
            last = time.time()
        elif time.time() - last > quiet:
            break
    return buf.decode("utf-8", "replace")


def wait_exit(pid, fd, timeout=5.0):
    """The exit code, reading the terminal meanwhile: a client whose pending paint fills the pty cannot see the key until it is read."""
    end = time.time() + timeout
    while time.time() < end:
        done, status = os.waitpid(pid, os.WNOHANG)
        if done:
            children.pop(pid, None)
            return os.waitstatus_to_exitcode(status)
        r, _, _ = select.select([fd], [], [], 0.05)
        if r:
            try:
                os.read(fd, 65536)
            except OSError:
                pass
    return None


def cooked(fd):
    lflag = termios.tcgetattr(fd)[3]
    return bool(lflag & termios.ICANON) and bool(lflag & termios.ECHO)


def cursor_visible_at_end(text):
    return text.rfind("\x1b[?25h") > text.rfind("\x1b[?25l")


def ordered(text, parts):
    at = 0
    for p in parts:
        i = text.find(p, at)
        if i < 0:
            return False
        at = i + len(p)
    return True


def session(name, stop):
    pid, fd = spawn(label=name)
    out = read_until(fd, lambda t: all(p in t for p in ENTER) and "ah" in t)
    entered = all(p in out for p in ENTER)
    raw = not cooked(fd)
    report(f"{name}: entering sets alternate screen, hides the cursor, turns the mouse on", entered, repr(out[:80]))
    report(f"{name}: the line discipline is raw while running (no ICANON, no ECHO)", raw)
    stop(pid, fd)
    tail = out + drain(fd)
    code = wait_exit(pid, fd)
    report(f"{name}: exit code 0", code == 0, f"exit={code}")
    left = ordered(tail, MOUSE_OFF_THEN_NORMAL_SCREEN) and cursor_visible_at_end(tail)
    report(f"{name}: leaving turns the mouse off, leaves the normal screen and shows the cursor", left, repr(tail[-80:]))
    report(f"{name}: the line discipline is cooked again afterwards", cooked(fd) if code is not None else False)
    os.close(fd)


def main():
    session("q", lambda pid, fd: os.write(fd, b"q"))
    session("ctrl-c", lambda pid, fd: os.write(fd, b"\x03"))
    session("SIGINT", lambda pid, fd: os.kill(pid, signal.SIGINT))
    session("SIGTERM", lambda pid, fd: os.kill(pid, signal.SIGTERM))
    session("SIGHUP", lambda pid, fd: os.kill(pid, signal.SIGHUP))

    # an unbound key does nothing and is not forwarded anywhere: the client keeps running
    pid, fd = spawn(label="unbound")
    read_until(fd, lambda t: all(p in t for p in ENTER))
    os.write(fd, b"z")
    time.sleep(0.3)
    done, _ = os.waitpid(pid, os.WNOHANG)
    report("an unbound key is ignored and the client keeps running", done == 0)
    os.write(fd, b"q")
    leftover = drain(fd)
    report("after an unbound key, q still quits with exit 0", wait_exit(pid, fd) == 0, repr(leftover[-60:]))
    os.close(fd)

    # the answers a terminal sends to the entry probes are consumed: nothing quits, nothing is typed
    pid, fd = spawn(label="probe-replies")
    read_until(fd, lambda t: all(p in t for p in ENTER))
    os.write(fd, PROBE_REPLIES)
    time.sleep(0.3)
    done, _ = os.waitpid(pid, os.WNOHANG)
    report("the terminal's replies to the entry probes are ignored and the client keeps running", done == 0)
    os.write(fd, b"q")
    report("after the probe replies, q still quits with exit 0", wait_exit(pid, fd) == 0)
    os.close(fd)

    # a resize redraws at the new size: with no daemon the screen is one line, cut to a narrow window
    pid, fd = spawn(cols=100, rows=30, label="winch")
    read_until(fd, lambda t: all(p in t for p in ENTER))
    drain(fd, quiet=0.3)
    fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", 24, 10, 0, 0))
    os.kill(pid, signal.SIGWINCH)
    after = read_until(fd, lambda t: "ahd is no…" in t)
    report("SIGWINCH: the screen is redrawn at the new size", "ahd is no…" in after, repr(after[-80:]))
    fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", 30, 100, 0, 0))
    os.kill(pid, signal.SIGWINCH)
    back = read_until(fd, lambda t: "ahd is not running" in t)
    report("SIGWINCH: and again at the restored size", "ahd is not running" in back, repr(back[-80:]))
    os.write(fd, b"q")
    report("SIGWINCH session still quits with exit 0", wait_exit(pid, fd) == 0)
    os.close(fd)

    report("no child of the suite remains", not children, f"left={children}")


try:
    main()
finally:
    for p in list(children.keys()):
        try:
            os.kill(p, signal.SIGKILL)
            os.waitpid(p, 0)
        except OSError:
            pass

print(f"failures={failures}")
sys.exit(1 if failures else 0)
