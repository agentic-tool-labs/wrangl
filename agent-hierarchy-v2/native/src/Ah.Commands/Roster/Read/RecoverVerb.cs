using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// <c>roster recover show|hold|release|now &lt;member&gt;</c>: what the daemon's bare-member recovery did and what it may do next.
/// Owner only; members ask the Orchestrator. Every operation, <c>show</c> included, is a request to the daemon, which proves from the
/// sender's process tree that it is the user or the team's Orchestrator, and keeps all of the state itself. <c>now</c> skips the
/// idle-stability and hold rungs; it never skips "idle", "no open work", "matched", the attempts cap, or the rule that a process is
/// signalled once.
/// </summary>
internal static class RecoverVerb
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "cwd", "team", "orchestrator-pid", "session", "plain", "json", "relink-lane" };

    private static readonly string[] Ops = ["show", "hold", "release", "now"];

    private static readonly string[] GlobalOps = ["enable", "disable", "status"];

    internal static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure($"recover: unrecognized flag --{key} (use --team or --cwd)");
            }
        }

        var words = c.Opts.Positionals.Where(static p => p != "recover").ToList();
        if (words.Count == 0 && c.TeamArg is not null)
        {
            return TeamRecover.Run(c);
        }

        var global = words.Count == 1 && GlobalOps.Contains(words[0]);
        if (!global && (words.Count != 2 || !Ops.Contains(words[0])))
        {
            throw new RosterFailure("recover: usage: ah roster recover show|hold|release|now <member>, or enable|disable|status (disable: the user or the team's Orchestrator; enable: the user only; status: anyone)");
        }

        var (op, member) = (words[0], global ? "*" : words[1]);
        var ask = c.Services.BareRecover ?? throw new RosterFailure("recover: not available here");
        var reply = ask.Ask(c.TeamHome, c.TeamFile ?? string.Empty, member, op);
        if (reply.Body is not { } body)
        {
            var failure = reply.Failure ?? "no answer from the daemon";
            c.Stderr.WriteLine($"ah: roster recover {op}: {failure}");
            return failure.StartsWith("daemon-unavailable", StringComparison.Ordinal) ? 70 : 2;
        }

        c.Stdout.WriteLine(JsonText.Stringify(JsonValue.FromObject(body), 2));
        return 0;
    }
}
