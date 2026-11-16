using System.Globalization;
using Ah.Core.Json;
using Ah.Watch;

namespace Ah.Commands;

/// <summary>
/// <c>roster job start|end|extend</c>: the record of a long-running run (a test, a build, a slot hold, a background command) that the
/// watch times against its eta. A caller starts, ends and extends only its own jobs; the record is a plain file the daemon and the
/// failsafe read.
/// </summary>
internal static class JobVerb
{
    private const string Usage =
        "usage: ah roster job start --kind test|build|slot|smoke|bg --label <text> [--eta small|medium|large] [--pid N] [--tool-use-id ID] [--background] [--cap-secs N] | end <id> [--exit <code>] | extend <id> --eta small|medium|large";

    private static readonly string[] Kinds = ["test", "build", "slot", "smoke", "bg"];

    private static readonly string[] Etas = ["small", "medium", "large"];

    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "cwd", "team", "orchestrator-pid", "kind", "label", "eta", "pid", "tool-use-id", "background", "cap-secs", "exit",
    };

    internal static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure($"job: unrecognized flag --{key}; {Usage}");
            }
        }

        var words = c.Opts.Positionals.Where(static p => p != "job").ToList();
        if (words.Count == 0)
        {
            throw new RosterFailure(Usage);
        }

        var caller = Caller(c);
        var dir = Path.Combine(c.MainDir ?? c.Dir, "jobs", caller.Team);
        return words[0] switch
        {
            "start" when words.Count == 1 => Start(c, caller, dir),
            "end" when words.Count == 2 => Change(c, caller, dir, words[1], static (c, o, now) => WatchModule.JobEnded(o, now, c.Opts.Text("exit"))),
            "extend" when words.Count == 2 => Extend(c, caller, dir, words[1]),
            _ => throw new RosterFailure(Usage),
        };
    }

    private static string? Text(JsonObject o, string key) => o.Get(key) is { IsText: true } v ? v.AsString() : null;

    private sealed record JobCaller(string Owner, string Session, string Team);

    private static JobCaller Caller(RosterContext c)
    {
        var session = c.EnvText("CLAUDE_CODE_SESSION_ID") ?? throw new RosterFailure("job: this session's id is unknown (CLAUDE_CODE_SESSION_ID is unset); nothing was changed");
        var found = c.Services.Roster.LocateSession(c.Dir, c.Homes, c.TeamArg, c.TeamDefaultExplicit, c.SessionPaneId(null));
        if (found.Get("answeredBy") is { IsTruthy: true } && found.Get("member") is { IsMap: true } member && member.AsObject().Get("name") is { IsText: true } name)
        {
            var memberTeam = found.Get("teamName") is { IsText: true } t && t.AsString().Length > 0 ? t.AsString() : "_default";
            return new JobCaller(name.AsString(), session, memberTeam);
        }

        var owned = c.Services.Roster.OwnedTeams(c.Dir, c.Invoker());
        var team = c.TeamFile is { } named && owned.Contains(named) ? named : c.TeamFile is null && owned.Count == 1 ? owned[0] : null;
        if (team is null && !(c.TeamFile is null && owned.Contains(null)))
        {
            throw new RosterFailure("job: this session is not a member or an Orchestrator of a team; nothing was changed");
        }

        return new JobCaller("orchestrator", session, team ?? "_default");
    }

    private static int Start(RosterContext c, JobCaller caller, string dir)
    {
        var kind = c.Opts.Text("kind");
        if (kind is null || !Kinds.Contains(kind))
        {
            throw new RosterFailure($"job start: --kind must be one of {string.Join(", ", Kinds)}");
        }

        var label = c.Opts.Text("label") is { Length: > 0 } l ? l : throw new RosterFailure("job start: --label <text> is required");
        var eta = c.Opts.Text("eta") ?? (kind == "test" ? "large" : "medium");
        if (!Etas.Contains(eta))
        {
            throw new RosterFailure($"job start: --eta must be one of {string.Join(", ", Etas)}");
        }

        var pid = c.Opts.Text("pid");
        var asked = 0;
        if (pid is not null && !int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out asked))
        {
            throw new RosterFailure("job start: --pid must be a process id");
        }

        if (pid is not null && !c.PidAlive(asked))
        {
            throw new RosterFailure($"job start: --pid {pid} is not a running process; nothing was changed");
        }

        var toolUse = c.Opts.Text("tool-use-id");
        var now = c.Services.Hierarchy.Messages.LocalIso();
        var pidNumber = pid is null ? (int?)null : int.Parse(pid, CultureInfo.InvariantCulture);
        var pidStart = pidNumber is { } started ? OrchestratorSession.StartOf(c, started) : null;
        IReadOnlyList<JobRecordFile> records;
        try
        {
            // b416: a read that cannot answer must not look like "no open job", or start makes a second record
            records = WatchModule.JobRecordFiles(c.Services.Hierarchy.Status, dir, mustAnswer: true);
        }
        catch (InvalidOperationException e)
        {
            throw new RosterFailure($"job start: {e.Message}; nothing was changed");
        }

        if (WatchModule.JobFindOpen(records, caller.Owner, caller.Session, toolUse, pidNumber) is { } known)
        {
            c.Services.Hierarchy.StateCalls.PutJob(c.MainDir ?? c.Dir, known.Team, known.Id, WatchModule.JobPatch(known.Record, WatchModule.JobMerged(known.Record, pidNumber, toolUse, pidStart)), ifOpen: true);
            c.Stdout.Write(Text(known.Record, "id") + "\n");
            return 0;
        }

        string? capIso = null;
        if (c.Opts.Text("cap-secs") is { } cap)
        {
            capIso = double.TryParse(cap, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                ? WatchModule.JobCapIso(now, seconds)
                : throw new RosterFailure("job start: --cap-secs must be a number of seconds");
        }

        var id = c.Services.Hierarchy.Core.NewId();
        var spec = new JobSpec(kind, eta, label, c.Opts.IsTrue("background") || kind == "bg");
        var job = WatchModule.JobMerged(WatchModule.JobNewRecord(id, caller.Team, caller.Owner, caller.Session, spec, pidNumber, now, toolUse, capIso), null, null, pidStart);
        c.Services.Hierarchy.StateCalls.PutJob(c.MainDir ?? c.Dir, Path.GetFileName(dir), id, job);
        c.Stdout.Write(id + "\n");
        return 0;
    }

    private static int Extend(RosterContext c, JobCaller caller, string dir, string id)
    {
        var eta = c.Opts.Text("eta");
        if (eta is null || !Etas.Contains(eta))
        {
            throw new RosterFailure($"job extend: --eta must be one of {string.Join(", ", Etas)}");
        }

        return Change(c, caller, dir, id, (c, o, now) => WatchModule.JobExtended(o, eta, now));
    }

    private static int Change(RosterContext c, JobCaller caller, string dir, string id, Func<RosterContext, JsonObject, string, JsonObject> apply)
    {
        if (WatchModule.JobRecordFiles(c.Services.Hierarchy.Status, dir).FirstOrDefault(f => f.Id == id) is not { } found)
        {
            throw new RosterFailure($"job: no job {id} in {caller.Team}; nothing was changed");
        }

        var record = found.Record.Clone();
        if (Text(record, "owner") != caller.Owner || Text(record, "session_id") != caller.Session)
        {
            throw new RosterFailure($"job: {id} is not this session's job; nothing was changed");
        }

        // b423: only the verb's own fields, merged onto the row as stored, ended or not (end/extend of an ended job writes as before)
        c.Services.Hierarchy.StateCalls.PutJob(c.MainDir ?? c.Dir, found.Team, found.Id, WatchModule.JobPatch(found.Record, apply(c, record, c.Services.Hierarchy.Messages.LocalIso())), merge: true);
        c.Stdout.Write($"job {id}: ok\n");
        return 0;
    }
}
