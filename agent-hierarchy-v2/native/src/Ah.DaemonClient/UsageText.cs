namespace Ah.DaemonClient;

/// <summary>The usage text <c>ahd --help</c> prints.</summary>
internal static class UsageText
{
    internal const string Block = """
agent-hierarchy — the `ahd` daemon's CLI: run, inspect and check the per-user daemon.

  ahd start | stop | restart            (start and restart start a daemon; `ahd ui` in every form but stop and status, and `ahd login run`, also start one when none is up)
  ahd drain [--require]                 (ask it to exit and return at once; --require: exit 3 when it is down; exit 1 when it refuses or does not answer)
  ahd status | health | version
  ahd logs [-n N]                       (the last N lines of ahd.log; 1-1000, default 50; never follows)
  ahd heal [--force]                    (replaces a daemon only when it is proven hung: no reply to four health probes over a minute; --force skips the restart cap, never the proof, and is the user's alone)
  ahd inspect boxes [--address A]
  ahd inspect entries --address A [--pending|--acked|--all] [-n N]    (default pending; N 1-200)
  ahd inspect registrations
  ahd inspect timers [--session S]      (a dry run: nothing is written, nothing is sent)
  ahd inspect events [--address A]
  ahd inspect status --dir D            (the live status document of a hierarchy dir)
  ahd check                             (conformance self-check in a throwaway sandbox)
  ahd contract                          (the participation contract for custom harnesses; --plain renders the guide)
  ahd tui [--team NAME] [--once]        (a read-only terminal view of every team and agent; never starts the daemon; --once prints one plain frame)
  ahd ui [--print] [--no-open]          (opens the browser page of the view, served on a loopback port that exists only while this command runs; the URL is printed only with --print or when there is no opener)
  ahd ui start [--no-open] [--json]     (keeps the page up in the background until `ahd ui stop`, prints the page's local address and opens it in the browser; run it again to print and open it again; text by default)
  ahd ui restart [--no-open] [--json]   (stop, then start)
  ahd ui stop [--json]                  (stops the background page)
  ahd ui status [--json]                (whether the background page runs, its address, and why it stopped when it did not; exit 3 when it does not run)
  ahd state DOMAIN OP [--hook] [--budget MS]        (one write of the state hub, run here; {"args","context","op_id"} on stdin, the route's body on stdout)
  ahd where [--plain]                   (the binary, the plugin and the login state the commands use; writes nothing and contacts no daemon)
  ahd login on|off [--dry-run] | status | run   (a login item that starts the daemon once at login; off by default; run is what the item runs)
  ahd home trust|untrust <path> [--hierarchy]   (adds or removes a state home, or with --hierarchy a hierarchy directory, from the trusted list; run it from a terminal)
  ahd new <name> [-ct|--cli-type claude] [-t|--team <team>] [-m|--model <model>] [--tab] [--dry-run] | ahd new -i|--interactive [<name>] ...
                                        (starts an Orchestrator session in this terminal, or with --tab in a new herdr tab; -i prompts for each field, with the flags given as its defaults)

Every verb takes `--cwd <abs path>` (default process.cwd()) and `--plain`. Output is one JSON object on
stdout, carrying `"source": "daemon"|"files"`; `--plain` renders the same object as text. Read-only verbs
answer from the files when no daemon is up. Errors are `{"ok": false, "error": {"code", "message"}}`.
Exit codes: 0 success, 1 the operation failed, 2 usage, 3 the daemon is down and the verb needs it,
4 `check` found a mismatch.
`ui` has its own exit codes: 0 a clean close, 2 the daemon is unreachable or a usage error, 3 the daemon refused the lease
(`AH_UI=off` in the daemon's environment refuses every lease).
`state DOMAIN OP` has its own exit codes: 0 success, 1 the operation failed, 2 a bad request or a body over the cap,
75 the domain lock did not clear, 70 any other failure.
""";
}
