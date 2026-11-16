using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy.StateCalls;

namespace Ah.Commands;

/// <summary>
/// <c>close-approve</c>: the managing orchestrator's green light for a sub-orchestrator to close its child team. Two phases like the close
/// it approves: without <c>--plan-token</c> it prints the plan the close would run, its release status and the token and writes nothing;
/// with the current token it records the grant in the managing team's file.
/// </summary>
internal static class CloseApproveVerb
{
    private const int KeepGrants = 20;

    private const int Attempts = 3;

    private const int ValidMinutes = 30;

    private static readonly string[] Flags = ["verb", "plan-token", "team", "cwd"];

    [Verb("roster:close-approve")]
    internal static int Approve(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, Run);

    private sealed record Plan(string Token, List<string> Targets, JsonArray Release, int Held);

    private static int Run(RosterContext c)
    {
        if (c.Services.Launch is null)
        {
            c.Stderr.WriteLine("ah: roster close-approve is not implemented natively: the launch layer is not composed");
            return 70;
        }

        StreamSupport.RejectUnknownFlags(c, Flags, "close-approve");
        var verb = c.Opts.Text("verb");
        if (verb is not ("dismiss" or "disband"))
        {
            throw new RosterFailure("close-approve: --verb must be dismiss or disband");
        }

        if (c.TeamArg is null)
        {
            throw new RosterFailure("close-approve: name the child team with --team <child>");
        }

        var child = c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile);
        if (child.IsNull || child.Member("parent") is not { IsMap: true } link
            || MemberPlanning.Text(link.Member("team")) is not { } managerName || MemberPlanning.Text(link.Member("stream")) is not { } stream)
        {
            throw new RosterFailure($"close-approve: {c.TeamFile} is not a sub-orchestrator's team (it has no parent); nothing was changed");
        }

        var manager = SubOrchestratorCarve.ReadTeam(c, managerName);
        if (manager.Member("streams")?.Member(stream) is not { IsMap: true })
        {
            throw new RosterFailure($"close-approve: {managerName} has no stream {stream}; nothing was changed");
        }

        var childName = c.TeamFile!;
        var planned = PlanOf(c, verb, childName);
        var given = c.Opts.Text("plan-token");
        if (given is null)
        {
            c.Out(new JsonObject
            {
                ["approve"] = false,
                ["team"] = childName,
                ["verb"] = verb,
                ["targets"] = Strings(planned.Targets),
                ["release"] = planned.Release,
                ["held"] = planned.Held,
                ["close_token"] = planned.Token,
                ["next"] = planned.Targets.Count == 0 ? JsonValue.Null : (JsonValue)$"ah roster close-approve --team {childName} --verb {verb} --plan-token {planned.Token}{MemberSuffix(c, verb)}",
            });
            return 0;
        }

        return Record(c, managerName, stream, childName, verb, given, planned);
    }

    private static int Record(RosterContext c, string managerName, string stream, string childName, string verb, string given, Plan planned)
    {
        var current = planned;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            if (current.Targets.Count == 0)
            {
                throw new RosterFailure($"close-approve: nothing to close; use untrack; nothing was changed.");
            }

            if (given != current.Token)
            {
                throw new RosterFailure("close-approve: the close plan changed; run it again without --plan-token for the current token; nothing was written.");
            }

            var now = c.Services.Clock.UtcNow;
            var entry = new JsonObject
            {
                ["verb"] = verb,
                ["team"] = childName,
                ["plan_token"] = current.Token,
                ["targets"] = Strings(current.Targets),
                ["granted_at"] = c.Services.Hierarchy.Messages.LocalIso(),
                ["expires_at"] = now.AddMinutes(ValidMinutes).ToString("yyyy-MM-dd'T'HH:mm:ss.fffK", CultureInfo.InvariantCulture),
            };
            string? missing = null;
            var outcome = TryWrite(c, managerName, stream, entry, ref missing);
            if (missing is not null)
            {
                throw new RosterFailure($"close-approve: {missing}; nothing was written.");
            }

            if (outcome)
            {
                c.Out(new JsonObject
                {
                    ["approve"] = true,
                    ["team"] = childName,
                    ["verb"] = verb,
                    ["close_token"] = current.Token,
                    ["targets"] = Strings(current.Targets),
                    ["expires_at"] = entry["expires_at"],
                });
                return 0;
            }

            if (attempt < Attempts)
            {
                current = PlanOf(c, verb, childName);
            }
        }

        throw new RosterFailure("close-approve: team file changed under close-approve; retry; nothing was written.");
    }

    /// <summary>One try at appending the grant. False when the file changed under it; <paramref name="missing"/> says why the manager cannot give one.</summary>
    private static bool TryWrite(RosterContext c, string managerName, string stream, JsonObject entry, ref string? missing)
    {
        string? gap = null;
        try
        {
            var result = c.Services.Hierarchy.StateCalls.UpdateTeam(
                c.Dir,
                managerName,
                doc =>
                {
                    if (!doc.IsMap)
                    {
                        gap = $"the team file of {managerName} is gone or unreadable";
                        return null;
                    }

                    var orchestrator = doc.Member("orchestrator");
                    var session = MemberPlanning.Text(orchestrator?.Member("session_id"));
                    var start = MemberPlanning.Text(orchestrator?.Member("pid_start"));
                    if (string.IsNullOrEmpty(session) || orchestrator?.Member("pid") is not { IsNumber: true } pid || string.IsNullOrEmpty(start))
                    {
                        gap = $"the orchestrator record of {managerName} has no session id, pid and pid start to bind a green light to";
                        return null;
                    }

                    var next = doc.AsObject().Clone();
                    var streams = next.Get("streams") is { IsMap: true } existing ? existing.AsObject().Clone() : new JsonObject();
                    var record = streams.Get(stream) is { IsMap: true } current ? current.AsObject().Clone() : new JsonObject();
                    var grants = record.Get("close_grants") is { IsArray: true } list ? list.AsArray().ToList() : [];
                    var written = entry.Clone();
                    written["by"] = new JsonObject { ["session_id"] = session, ["pid"] = pid, ["pid_start"] = start };
                    grants.Add(written);
                    var kept = new JsonArray();
                    foreach (var g in grants.Skip(Math.Max(0, grants.Count - KeepGrants)))
                    {
                        kept.Add(g);
                    }

                    record["close_grants"] = kept;
                    streams[stream] = record;
                    next["streams"] = streams;
                    return TeamFileOutcome.Write(next);
                });
            missing = gap;
            return gap is null && result.Changed && !(result.Result.Ok && JsonText.Parse(result.Result.ResultJson.Span).Member("conflict") is { IsTruthy: true });
        }
        catch (Exception e) when (e.Data["code"] is "conflict")
        {
            return false;
        }
    }

    private static Plan PlanOf(RosterContext c, string verb, string childName)
    {
        var dir = c.Dir;
        var warnings = new List<string>();
        var read = TeardownSupport.TeardownTeam(c, dir, warnings);
        if (read.Team.IsNull)
        {
            throw new RosterFailure($"close-approve: team {childName} has no team file to plan a close of; nothing was changed");
        }

        if (verb == "disband")
        {
            var plan = DisbandVerb.PlanTeam(c, dir, read, warnings);
            return Describe(plan.Token, plan.Release);
        }

        var member = c.Opts.Positionals.Count == 1
            ? c.Opts.Positionals[0]
            : throw new RosterFailure("close-approve --verb dismiss needs exactly one member name");
        var merged = TeardownSupport.MergedTeam(read.Team, read.Extra);
        var rows = merged.Member("members") is { IsArray: true } list ? list.AsArray().ToList() : [];
        var target = rows.Cast<JsonValue?>().FirstOrDefault(m => MemberPlanning.Text(m!.Value.Member("name")) == member)
            ?? throw new RosterFailure($"close-approve: no member named {member} in team {childName}; nothing was changed");
        var memberPlan = DismissVerb.PlanMember(c, dir, member, merged, target);
        return Describe(memberPlan.Token, memberPlan.Release);
    }

    private static Plan Describe(string token, ReleaseGate.Outcome release)
    {
        var statuses = new JsonArray();
        foreach (var v in release.Verdicts)
        {
            statuses.Add(new JsonObject { ["member"] = v.Member, ["status"] = v.Status, ["detail"] = v.Detail, ["closable"] = v.Closable });
        }

        var names = release.Closable.Select(m => MemberPlanning.JsConvertText(m.Member("name"))).ToList();
        return new Plan(token, names, statuses, release.Held.Count);
    }

    private static string MemberSuffix(RosterContext c, string verb) =>
        verb == "dismiss" && c.Opts.Positionals.Count == 1 ? " " + TeardownSupport.ShellWord(c.Opts.Positionals[0]) : string.Empty;

    private static JsonArray Strings(IEnumerable<string> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(item);
        }

        return array;
    }
}
