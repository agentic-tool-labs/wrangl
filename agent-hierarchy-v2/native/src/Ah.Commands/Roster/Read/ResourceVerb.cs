using System.Globalization;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Commands;

/// <summary>
/// <c>roster resource claim|release|list</c> (ah2-509, adr-36): one session at a time holds a named live thing (an editor, a device, a
/// port). A claim on a held name waits, polling, and is told who holds it; it is never refused. A hold belongs to the claimant's harness
/// process (<c>CLAUDE_PID</c>, else this process's parent) and ends when that process is gone.
/// </summary>
internal static class ResourceVerb
{
    private const string Usage =
        "usage: ah roster resource claim <name> [--wait <secs>] [--note <text≤120>] [--json] | release <name> [--force] [--json] | list [--json]";

    /// <summary>The exit of a claim whose wait ran out (R6), the same as <c>ah test run</c>.</summary>
    internal const int TimedOut = 75;

    private const double DefaultWaitSecs = 3600;

    private const int NoteMax = 120;

    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan LineEvery = TimeSpan.FromSeconds(60);

    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "cwd", "team", "wait", "note", "json", "force" };

    internal static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure($"resource: unrecognized flag --{key}; {Usage}");
            }
        }

        var words = c.Opts.Positionals;
        return words switch
        {
            ["claim", var name] => Claim(c, NameOf(name)),
            ["release", var name] => Release(c, NameOf(name)),
            ["list"] => List(c),
            _ => throw new RosterFailure(Usage),
        };
    }

    private static string NameOf(string raw) =>
        HierarchyModule.ResourceNameOf(raw) ?? throw new RosterFailure($"resource: the name must be {HierarchyModule.ResourceNameShape}; {Usage}");

    private static JsonObject Owner(RosterContext c, long pid) => new()
    {
        ["pid"] = pid,
        ["start"] = c.Services.Callers?.ProcessStartText((int)pid) is { } start ? start : JsonValue.Null,
    };

    /// <summary>R1: the holder is the harness process, <c>CLAUDE_PID</c> when set, else the process that started this one.</summary>
    private static JsonObject Holder(RosterContext c) =>
        Owner(c, c.EnvText("CLAUDE_PID") is { } text && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 1 ? pid : c.Services.ParentPid);

    /// <summary>The display identity (R1): the resolved address, else <c>AH_MEMBER_NAME</c>, else <c>user</c>.</summary>
    private static string Who(RosterContext c)
    {
        var found = c.Services.Roster.LocateSession(c.Dir, c.Homes, c.TeamArg, c.TeamDefaultExplicit, c.SessionPaneId(null));
        if (found.Get("answeredBy") is { IsTruthy: true } && found.Get("member") is { IsMap: true } member && member.AsObject().Get("name") is { IsText: true } name)
        {
            var team = found.Get("teamName") is { IsText: true } t && t.AsString().Length > 0 ? t.AsString() : "_default";
            return $"{name.AsString()}@{team}";
        }

        return c.EnvText("AH_MEMBER_NAME") ?? "user";
    }

    private static JsonValue Call(RosterContext c, string op, JsonObject args)
    {
        try
        {
            return c.Services.Hierarchy.StateCalls.Resources(op, args);
        }
        catch (InvalidOperationException e)
        {
            throw new RosterFailure($"resource {op}: {e.Message}", null, 1);
        }
    }

    private static double NowMs(RosterContext c) => c.Services.Clock.UtcNow.ToUnixTimeMilliseconds();

    private static string Since(RosterContext c, JsonValue row) =>
        HierarchyModule.ResourceSince(row.Member("since_ms")!.Value.AsNumber(), NowMs(c));

    private static string Waited(double fromMs, double nowMs) => Ah.Core.Time.Age.Format((nowMs - fromMs) / 1000);

    /// <summary>The live rows of every name (<c>resources.rows</c>, a read that writes nothing).</summary>
    private static JsonArray Rows(RosterContext c) => Call(c, "rows", new JsonObject()).Member("rows") is { IsArray: true } rows ? rows.AsArray() : [];

    private static JsonValue? HoldOf(RosterContext c, string name)
    {
        foreach (var row in Rows(c))
        {
            if (row.Member("role")?.AsString() == "hold" && row.Member("name")?.AsString() == name)
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>The waiter line (R5): who holds the name, since when, the caller's place in the queue, and how long it has waited.</summary>
    private static string HolderLine(RosterContext c, string name, JsonValue answer, double startedMs)
    {
        var waiters = answer.Member("waiting") is { IsNumber: true } n ? (int)n.AsNumber() : 0;

        // Amendment 2 X3: the caller's place only when it is in the queue
        var place = answer.Member("position") is { IsNumber: true } p ? $"you are {(int)p.AsNumber()} of {waiters} waiting" : $"{waiters} waiting";
        var queue = $"{place}; waited {Waited(startedMs, NowMs(c))}";
        return HoldOf(c, name) is { } hold
            ? $"resource {name} held by {hold.Member("who")!.Value.AsString()} since {Since(c, hold)}; {queue}"
            : $"resource {name} is free; {queue}";
    }

    private static int Claim(RosterContext c, string name)
    {
        var wait = DefaultWaitSecs;
        // Amendment 2 X2: a wait the clock cannot add is a usage error, not a crash
        if (c.Opts.Has("wait") && !(double.TryParse(c.Opts.Text("wait"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out wait)
            && double.IsFinite(wait) && wait < (DateTimeOffset.MaxValue - c.Services.Clock.UtcNow).TotalSeconds))
        {
            throw new RosterFailure($"resource claim: --wait must be a number of seconds; {Usage}");
        }

        var note = c.Opts.Text("note");
        if (note is { Length: > NoteMax })
        {
            throw new RosterFailure($"resource claim: --note is at most {NoteMax} characters; {Usage}");
        }

        var waiter = Owner(c, Environment.ProcessId);
        var args = new JsonObject
        {
            ["name"] = name,
            ["holder"] = Holder(c),
            ["waiter"] = waiter,
            ["who"] = Who(c),
            ["session"] = c.EnvText("CLAUDE_CODE_SESSION_ID") is { } session ? session : JsonValue.Null,
            ["note"] = note is null ? JsonValue.Null : note,
            ["queue"] = wait > 0,
        };
        var started = NowMs(c);
        var deadline = c.Services.Clock.UtcNow + TimeSpan.FromSeconds(Math.Max(0, wait));
        DateTimeOffset? lastLine = null;
        while (true)
        {
            var answer = Call(c, "claim", args);
            switch (answer.Member("state")?.AsString())
            {
                case "mine":
                    return Done(c, answer, HoldOf(c, name) is { } mine ? $"resource {name} is already yours since {Since(c, mine)}" : $"resource {name} is already yours");
                case "granted":
                    var from = answer.Member("waited_since_ms") is { IsNumber: true } since ? since.AsNumber() : started;
                    return Done(c, answer, $"resource {name} is yours (waited {Waited(Math.Min(from, started), NowMs(c))})");
            }

            var now = c.Services.Clock.UtcNow;
            if (now >= deadline)
            {
                if (wait > 0)
                {
                    _ = Call(c, "leave", new JsonObject { ["name"] = name, ["waiter"] = waiter });
                }

                c.Stderr.Write(HolderLine(c, name, answer, started) + "\n");
                if (c.Opts.IsTrue("json"))
                {
                    c.Stdout.Write(JsonText.Stringify(answer, 0) + "\n");
                }

                return TimedOut;
            }

            if (lastLine is null || now - lastLine >= LineEvery)
            {
                c.Stderr.Write(HolderLine(c, name, answer, started) + "\n");
                lastLine = now;
            }

            c.Services.Sleep(Poll);
        }
    }

    private static int Done(RosterContext c, JsonValue answer, string line)
    {
        c.Stdout.Write((c.Opts.IsTrue("json") ? JsonText.Stringify(answer, 0) : line) + "\n");
        return 0;
    }

    private static int Release(RosterContext c, string name)
    {
        var before = HoldOf(c, name);
        var answer = Call(c, "release", new JsonObject { ["name"] = name, ["holder"] = Holder(c), ["force"] = c.Opts.Has("force") });
        var line = answer.Member("state")?.AsString() switch
        {
            "released" when c.Opts.Has("force") => $"resource {name}: released {before?.Member("who")?.AsString() ?? "its holder"} (forced by {Who(c)})",
            "released" => $"resource {name}: released",
            "held" when HoldOf(c, name) is { } hold => $"resource {name} held by {hold.Member("who")!.Value.AsString()} since {Since(c, hold)}; not yours, nothing changed",
            _ => $"resource {name} is free",
        };
        return Done(c, answer, line);
    }

    private static int List(RosterContext c)
    {
        _ = Call(c, "reap", new JsonObject());
        var rows = Rows(c);
        if (c.Opts.IsTrue("json"))
        {
            c.Stdout.Write(JsonText.Stringify(new JsonObject { ["rows"] = rows }, 0) + "\n");
            return 0;
        }

        static string Field(JsonValue row, string key) => row.Member(key)!.Value.AsString();
        var holds = rows.Where(static r => Field(r, "role") == "hold").ToList();
        if (holds.Count == 0)
        {
            c.Stdout.Write("no resource is held\n");
        }

        void Waits(string name)
        {
            foreach (var wait in rows.Where(r => Field(r, "role") == "wait" && Field(r, "name") == name))
            {
                c.Stdout.Write($"  waiting: {Field(wait, "who")} {Waited(wait.Member("since_ms")!.Value.AsNumber(), NowMs(c))}\n");
            }
        }

        foreach (var hold in holds)
        {
            var name = Field(hold, "name");
            var note = hold.Member("note") is { IsText: true } n ? $" — {n.AsString()}" : string.Empty;
            c.Stdout.Write($"{name}: {Field(hold, "who")} since {Since(c, hold)}{note}\n");
            Waits(name);
        }

        // Amendment 2 X4: the waits of a name whose holder has just gone, until a waiter's next poll takes it
        var held = holds.Select(r => Field(r, "name")).ToHashSet(StringComparer.Ordinal);
        foreach (var name in rows.Where(r => Field(r, "role") == "wait").Select(r => Field(r, "name")).Where(n => !held.Contains(n)).Distinct(StringComparer.Ordinal))
        {
            c.Stdout.Write($"{name}: free\n");
            Waits(name);
        }

        return 0;
    }
}
