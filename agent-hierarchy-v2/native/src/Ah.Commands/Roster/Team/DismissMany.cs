using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// <c>dismiss &lt;a&gt; &lt;b&gt; … --close</c>: one plan and one confirm for several members of a team. The plan runs the release check for
/// each name and issues a token only when every one may close; the token names the team and the sorted set of panes, so a different
/// set is refused. The confirm closes the members one at a time and stops at the first failure.
/// </summary>
internal static class DismissMany
{
    /// <summary>What dropping a closed member reports about itself, as a single-name close does.</summary>
    private static readonly string[] DroppedKeys = ["team_removed", "config"];

    internal static int Run(RosterContext c, IReadOnlyList<string> names)
    {
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
        {
            throw new RosterFailure("dismiss: a member name was given twice — nothing was changed");
        }

        var dir = c.Dir;
        var launch = c.Services.Launch!;
        var teardown = launch.Teardown;
        var warnings = new List<string>();
        var read = TeardownSupport.TeardownTeam(c, dir, warnings);
        if (read.Team.IsNull)
        {
            throw new RosterFailure("dismiss: several names need a team file, and none was found — nothing was changed");
        }

        var team = TeardownSupport.MergedTeam(read.Team, read.Extra);
        var members = DismissVerb.Members(team);
        var targets = new List<(string Name, JsonValue Member)>();
        foreach (var name in names)
        {
            var rows = members.Where(m => MemberPlanning.Text(m.Member("name")) == name).ToList();
            var quoted = JsonText.Quote(name);
            if (rows.Count == 0)
            {
                var has = string.Join(", ", members.Select(m => MemberPlanning.JsConvertText(m.Member("name"))));
                throw new RosterFailure($"dismiss: no member named {quoted} in team {MemberPlanning.JsConvertText(team.Member("team_id"))} — it has: {(has.Length > 0 ? has : "(none)")}; nothing was changed");
            }

            if (rows.Count > 1)
            {
                throw new RosterFailure($"dismiss: {quoted} names {rows.Count} rows in team {MemberPlanning.JsConvertText(team.Member("team_id"))} — it is ambiguous, so nothing was changed");
            }

            if (TeardownSupport.IsInvokerOrOrchestrator(c, rows[0]))
            {
                throw new RosterFailure($"dismiss: {quoted} is the session running this command or an orchestrator — it is never closed by dismiss");
            }

            targets.Add((name, rows[0]));
        }

        var plans = targets.Select(t => (t.Name, t.Member, Plan: DismissVerb.PlanMember(c, dir, t.Name, team, t.Member))).ToList();
        var closable = plans.SelectMany(p => p.Plan.Closable).ToList();
        var verdicts = plans.SelectMany(p => p.Plan.Release.Verdicts).ToList();
        var combined = new ReleaseGate.Outcome(closable, verdicts);
        var teamId = team.Member("team_id") ?? JsonValue.Null;
        var transport = MemberPlanning.Text(team.Member("transport"));
        var sources = teardown.SourcesField(dir, TeardownSupport.ScopeOf(c), team, c.LaunchContext());

        if (!c.Opts.IsTrue("close"))
        {
            return Plan(c, dir, team, teamId, plans.Select(p => (p.Name, p.Plan)).ToList(), closable, combined, warnings, sources);
        }

        var unclosable = plans.Where(p => p.Plan.Closable.Count == 0).Select(p => p.Name).ToList();
        if (unclosable.Count > 0)
        {
            throw new RosterFailure($"dismiss --close: {string.Join(", ", unclosable)} may not close now (held by the release check or no addressable pane) — re-run `dismiss` for a fresh plan");
        }

        TeardownSupport.GateClose(c, "dismiss", closable, Subject(dir, team, plans.Select(p => (p.Name, p.Plan)).ToList()));
        var results = new JsonArray();
        var closedCount = 0;
        var stopped = false;
        foreach (var p in plans)
        {
            var row = new JsonObject { ["member"] = p.Name };
            if (stopped)
            {
                row["result"] = "not-attempted";
                results.Add(row);
                continue;
            }

            var verified = TeardownSupport.CloseAndVerify(c, p.Plan.Closable, transport, warnings);
            var ok = verified.Results.All(r => r.Get("closed") is { IsTruthy: true } && r.Get("appserver_unverified") is not { IsTruthy: true });
            if (ok)
            {
                closedCount++;
                row["result"] = "closed";
                var dropped = new JsonObject();
                DismissVerb.DropClosedMember(c, dir, p.Name, read, p.Member, warnings, dropped);
                foreach (var key in DroppedKeys)
                {
                    if (dropped.Get(key) is { } value)
                    {
                        row[key] = value;
                    }
                }
            }
            else
            {
                stopped = true;
                row["result"] = "failed";
                row["detail"] = DisbandVerb.ResultsOf(verified);
            }

            results.Add(row);
        }

        var summary = new JsonObject
        {
            ["closed"] = closedCount == plans.Count,
            ["expected"] = plans.Count,
            ["closed_count"] = closedCount,
            ["results"] = results,
            ["warnings"] = TeardownSupport.Strings(warnings),
            ["sources"] = sources,
        };
        ReleaseGate.Annotate(summary, combined);
        c.Out(summary);
        return closedCount == plans.Count ? 0 : 1;
    }

    private static CleanClose.Subject Subject(string dir, JsonValue team, List<(string Name, DismissVerb.MemberPlan Plan)> plans) =>
        new(dir, team, [.. plans.Select(p => CleanClose.MemberOf(p.Name, p.Plan.HealedTarget, p.Plan.Release))]);

    private static int Plan(
        RosterContext c, string dir, JsonValue team, JsonValue teamId, List<(string Name, DismissVerb.MemberPlan Plan)> plans, List<JsonValue> closable,
        ReleaseGate.Outcome combined, List<string> warnings, JsonObject sources)
    {
        var rows = new JsonArray();
        foreach (var (name, plan) in plans)
        {
            var target = plan.HealedTarget;
            var row = new JsonObject { ["name"] = name };
            Put(row, "role", target.Member("role"));
            Put(row, "route", target.Member("route"));
            Put(row, "transport_id", target.Member("transport_id"));
            row["closable"] = plan.Closable.Count > 0;
            rows.Add(row);
        }

        var failures = new JsonArray();
        foreach (var v in combined.Held)
        {
            failures.Add(new JsonObject { ["member"] = v.Member, ["status"] = v.Status, ["detail"] = v.Detail });
        }

        foreach (var (name, plan) in plans.Where(p => p.Plan.Closable.Count == 0 && !combined.Held.Any(v => v.Member == p.Name)))
        {
            failures.Add(new JsonObject { ["member"] = name, ["status"] = "no-pane", ["detail"] = "no addressable pane" });
        }

        var output = new JsonObject { ["members"] = rows, ["team_id"] = teamId };
        if (failures.Count == 0)
        {
            var normal = c.Services.Launch!.Teardown.CloseToken(teamId, closable);
            var clean = CleanClose.Read(c, Subject(dir, team, plans), normal);
            var token = clean.Token ?? normal;
            output["close_token"] = token;
            output["clean_close"] = DismissVerb.CleanJson(clean);
            output["next"] = Command(c, plans.Select(p => p.Name), token);
        }
        else
        {
            output["close_token"] = JsonValue.Null;
            output["failures"] = failures;
        }

        output["expected"] = failures.Count == 0 ? closable.Count : 0;
        output["warnings"] = TeardownSupport.Strings(warnings);
        output["sources"] = sources;
        ReleaseGate.Annotate(output, combined);
        c.Out(output);
        return 0;
    }

    private static string Command(RosterContext c, IEnumerable<string> names, string token)
    {
        var words = new List<string> { "dismiss" };
        words.AddRange(names);
        words.AddRange(["--close", "--confirm", "--plan-token", token]);
        if (c.Opts.IsTrue(ReleaseGate.IgnoreFlag))
        {
            words.Add("--" + ReleaseGate.IgnoreFlag);
        }

        foreach (var named in c.Opts.All(ReleaseGate.IgnoreFlag))
        {
            words.AddRange(["--" + ReleaseGate.IgnoreFlag, named]);
        }

        if (c.Opts.Text("team") is { } team)
        {
            words.AddRange(["--team", team]);
        }

        if (c.Opts.Text("cwd") is { } cwd)
        {
            words.AddRange(["--cwd", cwd]);
        }

        return c.SelfCommand + " " + string.Join(' ', words.Select(TeardownSupport.ShellWord));
    }

    private static void Put(JsonObject target, string key, JsonValue? value)
    {
        if (value is { } v)
        {
            target[key] = v;
        }
    }
}
