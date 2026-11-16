#!/usr/bin/env python3
"""The trust verbs on a pseudo-terminal they own, against the real binary.

usage: trust-pty.py <path to ahd> <project dir>   (the environment, HOME and AH_HOME included, is the caller's sandbox in test mode)

In test mode the trust files live in AH_HOME, so every write here lands in the sandbox. Each scenario runs the binary on a fresh pty
(stdin, stdout and stderr all the terminal), reads its output to the end and checks the exit status and the files.
"""
import os
import pty
import select
import stat
import sys
import time

failures = 0


def report(name, ok, detail=""):
    global failures
    print(("ok   " if ok else "FAIL ") + name + ("" if ok else "  " + detail))
    if not ok:
        failures += 1


def run(argv, env_extra=None, env_drop=()):
    """Runs argv on a new pty; returns (exit status, output)."""
    env = dict(os.environ)
    for key in env_drop:
        env.pop(key, None)
    env.update(env_extra or {})
    pid, fd = pty.fork()
    if pid == 0:
        os.execve(argv[0], argv, env)
    out = b""
    deadline = time.time() + 20
    while time.time() < deadline:
        ready, _, _ = select.select([fd], [], [], 0.5)
        if ready:
            try:
                chunk = os.read(fd, 4096)
            except OSError:
                break
            if not chunk:
                break
            out += chunk
    _, status = os.waitpid(pid, 0)
    os.close(fd)
    code = os.waitstatus_to_exitcode(status)
    return code, out.decode("utf-8", "replace")


def read(path):
    try:
        with open(path) as handle:
            return handle.read()
    except OSError:
        return None


def main():
    ahd, proj = sys.argv[1], os.path.realpath(sys.argv[2])
    ah_home = os.environ["AH_HOME"]
    homes = os.path.join(ah_home, "trusted-homes")
    hier = os.path.join(ah_home, "trusted-hierarchy-dirs")
    drop = ("AH_SPAWN_DEPTH",)

    code, out = run([ahd, "home", "trust", proj], env_drop=drop)
    report("trust on a terminal exits 0", code == 0, f"exit {code}: {out!r}")
    report("trusted-homes lists the realpath", read(homes) == proj + "\n", repr(read(homes)))
    mode = stat.S_IMODE(os.stat(homes).st_mode) if os.path.exists(homes) else None
    report("trusted-homes is mode 0600", mode == 0o600, oct(mode) if mode is not None else "absent")
    report("the other file is not made", not os.path.exists(hier))

    code, out = run([ahd, "home", "trust", proj], env_drop=drop)
    report("trusting twice exits 0 and keeps one line", code == 0 and read(homes) == proj + "\n", f"exit {code}: {out!r}")

    code, out = run([ahd, "home", "trust", proj, "--hierarchy"], env_drop=drop)
    report("--hierarchy writes trusted-hierarchy-dirs", code == 0 and read(hier) == proj + "\n", f"exit {code}: {out!r}")

    before = read(homes)
    code, out = run([ahd, "home", "trust", "/usr"], env_drop=drop)
    report("a directory the user does not own is refused", code == 1 and read(homes) == before, f"exit {code}: {out!r}")

    code, out = run([ahd, "home", "trust", proj], env_extra={"AH_SPAWN_DEPTH": "1"})
    report("a spawned session is refused with exit 2", code == 2 and "agent session" in out, f"exit {code}: {out!r}")
    code, out = run([ahd, "home", "untrust", proj], env_extra={"AH_SPAWN_DEPTH": "0"})
    report("even depth 0 is refused", code == 2 and read(homes) == before, f"exit {code}: {out!r}")

    code, out = run(["/bin/sh", "-c", f'exec "$0" home untrust "$1" 2>/dev/null', ahd, proj], env_drop=drop)
    report("standard error that is not a terminal is refused", code == 2 and read(homes) == before, f"exit {code}: {out!r}")
    code, out = run(["/bin/sh", "-c", f'exec "$0" home untrust "$1" </dev/null', ahd, proj], env_drop=drop)
    report("standard input that is not a terminal is refused", code == 2 and read(homes) == before, f"exit {code}: {out!r}")

    link = proj + ".link"
    try:
        os.symlink(proj, link)
        code, out = run([ahd, "home", "untrust", link], env_drop=drop)
        report("untrust through a symlink removes the target line", code == 0 and read(homes) == "", f"exit {code}: {out!r} {read(homes)!r}")
        report("untrust leaves the file in place", os.path.exists(homes))
    finally:
        try:
            os.unlink(link)
        except OSError:
            pass

    print("failures:", failures)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
