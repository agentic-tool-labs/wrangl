using Ah.Core.Json;
using Ah.Hierarchy.StateCalls;

namespace Ah.Commands;

/// <summary>
/// <c>untrack</c>: the only way to drop a tracking record without closing a session. It is its own verb so that a live guard fronts it
/// for every caller, and so that an agent reaching for "remove the member" is not handed a tracking-only removal by a flag.
/// </summary>
internal static class UntrackVerb
{
    // A new flag that changes what --commit does must also be carried into the plan's commit command in PlanOnly.
    private static readonly string[] Flags = ["all", "plan", "commit", "keep-sessions", "also-config", "level", "cwd", "team"];

    private static List<JsonValue> Members(JsonValue team) => team.Member("members") is { IsArray: true } m ? m.AsArray().ToList() : [];

    internal static int Run(RosterContext c)
    {
        if (c.Services.Launch is null)
        {
            c.Stderr.WriteLine("ah: roster untrack is not implemented natively: the launch layer is not composed");
            return 70;
        }

        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure($"untrack: unrecognized flag --{key} (use <name>|--all [--plan|--commit] [--keep-sessions] [--also-config] [--level L])");
            }
        }

        if (c.Opts.IsTrue("plan") && c.Opts.IsTrue("commit"))
        {
            throw new RosterFailure("untrack --plan cannot be combined with --commit");
        }

        var all = c.Opts.IsTrue("all");
        var name = c.Opts.Positionals.Count > 0 ? c.Opts.Positionals[0] : null;
        if (all && name is not null)
        {
            throw new RosterFailure("untrack: pass a member name or --all, not both");
        }

        if (!all && name is null)
        {
            throw new RosterFailure("untrack needs a member name or --all: ah roster untrack <name>|--all [--plan|--commit] [--keep-sessions]");
        }

        var alsoConfig = c.Opts.IsTrue("also-config") || c.Opts.Text("level") is not null;
        if (alsoConfig && all)
        {
            throw new RosterFailure("untrack: --also-config and --level apply to a single member, not --all");
        }

        var services = c.Services;
        var launch = services.Launch!;
        var roster = services.Roster;
        var dir = c.Dir;
        var team = roster.ReadTeamRecord(dir, c.TeamFile);
        var commit = c.Opts.IsTrue("commit");
        var keep = c.Opts.IsTrue("keep-sessions");
        if (team.IsNull)
        {
            c.Out(new JsonObject { ["untracked"] = false, ["already_untracked"] = true, ["reason"] = "no team file to forget" });
            return 0;
        }

        var teamId = team.Member("team_id") ?? JsonValue.Null;
        var members = Members(team);
        var teamPath = roster.TeamPath(dir, c.TeamFile);
        if (SubOrchestratorClose.Applies(c))
        {
            TeardownSupport.CarvedUntrackGuard(c, dir, all ? members : members.Where(m => MemberPlanning.Text(m.Member("name")) == name));
        }

        if (all)
        {
            if (!commit)
            {
                var rows = new JsonArray();
                foreach (var m in members)
                {
                    var row = new JsonObject();
                    Put(row, "role", m.Member("role"));
                    Put(row, "name", m.Member("name"));
                    RosterJson.Spread(row, TeardownSupport.LiveField(c, dir, m));
                    rows.Add(row);
                }

                var allPlan = new JsonObject { ["plan"] = "untrack-all", ["team_id"] = teamId, ["members"] = rows, ["removes"] = teamPath };
                PlanOnly(c, allPlan, null);
                c.Out(allPlan);
                return 0;
            }

            if (!keep)
            {
                TeardownSupport.UntrackLiveGuard(c, dir, members, "disband --close");
            }

            var known = new HashSet<string>(members.Select(KeyOf), StringComparer.Ordinal);
            services.Hierarchy.StateCalls.UpdateTeam(
                dir,
                c.TeamFile,
                doc =>
                {
                    if (doc.IsNull)
                    {
                        return null;
                    }

                    if (!Members(doc).All(m => known.Contains(KeyOf(m))))
                    {
                        var conflict = new InvalidOperationException("the team changed while it was being untracked; run it again");
                        conflict.Data["code"] = "conflict";
                        throw conflict;
                    }

                    return TeamFileOutcome.Clear();
                });
            launch.Links.RevokeLinksFor(teamPath, null);
            var gone = new JsonArray();
            foreach (var m in members)
            {
                var row = new JsonObject();
                Put(row, "role", m.Member("role"));
                Put(row, "name", m.Member("name"));
                Put(row, "transport_id", m.Member("transport_id"));
                gone.Add(row);
            }

            c.Out(new JsonObject { ["untracked"] = true, ["team_id"] = teamId, ["removed"] = teamPath, ["members"] = gone });
            return 0;
        }

        var rowsNamed = members.Count(m => MemberPlanning.Text(m.Member("name")) == name);
        if (rowsNamed > 1)
        {
            throw new RosterFailure(
                $"untrack: {JsonText.Quote(name!)} names {rowsNamed} rows in team {MemberPlanning.JsConvertText(teamId)} — it is ambiguous, so nothing was changed. Remove the duplicate rows from the team file first");
        }

        var target = members.Cast<JsonValue?>().FirstOrDefault(m => MemberPlanning.Text(m!.Value.Member("name")) == name);
        if (target is not { } utTarget)
        {
            var peers = TeardownSupport.DedupPeers(launch.Teardown.PeerFallbackMembers(dir, TeardownSupport.ScopeOf(c), c.LaunchContext()));
            var asPeer = TeardownSupport.ResolvePeer(c, peers, name!, MemberPlanning.Text(team.Member("transport")));
            if (asPeer is { } peer)
            {
                throw new RosterFailure(
                    $"untrack: {JsonText.Quote(name!)} is not tracked in team {MemberPlanning.JsConvertText(teamId)} — it is a LIVE untracked session " +
                    $"({JsonText.Stringify(TeardownSupport.PeerIdentifiers(peer), 0)}). There is no record to forget; `dismiss {MemberPlanning.JsConvertText(peer.Member("name"))} --close` closes it.");
            }

            c.Out(new JsonObject
            {
                ["untracked"] = false,
                ["already_untracked"] = true,
                ["team_id"] = teamId,
                ["member"] = name!,
            });
            return 0;
        }

        if (!commit)
        {
            var member = new JsonObject();
            Put(member, "role", utTarget.Member("role"));
            Put(member, "name", utTarget.Member("name"));
            RosterJson.Spread(member, TeardownSupport.LiveField(c, dir, utTarget));
            var plan = new JsonObject
            {
                ["plan"] = "untrack",
                ["team_id"] = teamId,
                ["member"] = member,
                ["remaining_after_commit"] = TeardownSupport.Strings(
                    members.Where(m => MemberPlanning.Text(m.Member("name")) != name).Select(m => MemberPlanning.JsConvertText(m.Member("name")))),
            };
            PlanOnly(c, plan, name);
            c.Out(plan);
            return 0;
        }

        if (!keep)
        {
            TeardownSupport.UntrackLiveGuard(c, dir, [utTarget], $"dismiss {MemberPlanning.JsConvertText(utTarget.Member("name"))} --close");
        }

        c.Out(UntrackMember(c, dir, team, utTarget, name!, teamPath));
        return 0;
    }

    /// <summary>
    /// The three fields a plan carries so it cannot be mistaken for a done untrack: that it is only a plan, that nothing changed, and the exact
    /// command that commits it (the same arguments plus <c>--commit</c>).
    /// </summary>
    private static void PlanOnly(RosterContext c, JsonObject plan, string? name)
    {
        var words = new List<string> { "untrack", name ?? "--all" };
        foreach (var flag in new[] { "keep-sessions", "also-config" })
        {
            if (c.Opts.IsTrue(flag))
            {
                words.Add("--" + flag);
            }
        }

        foreach (var key in new[] { "level", "team" })
        {
            if (c.Opts.Text(key) is { } value)
            {
                words.AddRange(["--" + key, value]);
            }
        }

        words.AddRange(["--cwd", c.Cwd, "--commit"]);
        plan["plan_only"] = true;
        plan["changed"] = false;
        plan["note"] = "PLAN ONLY: nothing was changed and the team file still lists everything shown. Run the command in `commit` to untrack.";
        plan["commit"] = c.SelfCommand + " " + string.Join(' ', words.Select(TeardownSupport.ShellWord));
    }

    private static string KeyOf(JsonValue m) =>
        $"{MemberPlanning.JsConvertText(m.Member("name"))}|{MemberPlanning.JsConvertText(m.Member("transport_id"))}";

    private static void Put(JsonObject target, string key, JsonValue? value)
    {
        if (value is { } v)
        {
            target[key] = v;
        }
    }

    /// <summary>
    /// Forget one member's team row, touching no session. A commit on a member that may still be live is allowed, but never silent
    /// about it: an indeterminate answer warns too, because "herdr did not answer" is not "it is dead".
    /// </summary>
    /// <summary>What an undecided member is called: it may be running, and when no sign of life is newer than the claim window, how long it has not been seen.</summary>
    internal static string UnknownWording(JsonObject state, DateTimeOffset now)
    {
        var why = MemberPlanning.JsConvertText(state.Get("why"));
        if (state.Get("last_life_at") is { IsText: true } at && DateTimeOffset.TryParse(at.AsString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var life))
        {
            var seconds = (long)(now - life).TotalSeconds;
            if (seconds < Ah.Core.Time.LivenessRules.FreshSeconds)
            {
                return $"may still be running (liveness unknown: {why})";
            }

            var age = seconds < 3600 ? $"{seconds / 60}m" : seconds < 86400 ? $"{seconds / 3600}h{seconds % 3600 / 60:00}m" : $"{seconds / 86400}d";
            return $"may still be running (not seen for {age}; liveness unknown: {why})";
        }

        return $"may still be running (not seen for ever; liveness unknown: {why})";
    }

    private static JsonObject UntrackMember(RosterContext c, string dir, JsonValue team, JsonValue target, string name, string teamPath)
    {
        var launch = c.Services.Launch!;
        var templateKey = c.Opts.IsTrue("also-config") ? TeardownSupport.TemplateKey(c, dir) : null;
        var update = launch.TeamRows.UpdateRows(
            dir,
            teamPath,
            fresh => fresh.Get("members") is { IsArray: true } list
                ? list.AsArray().Where(m => MemberPlanning.Text(m.Member("name")) != name).ToList()
                : [],
            keepFile: c.LaneActor || TeardownSupport.NamesOrchestrator(team));
        var remaining = update.Rows is { } rows
            ? rows.ToList()
            : Members(team).Where(m => MemberPlanning.Text(m.Member("name")) != name).ToList();
        var transport = MemberPlanning.Text(team.Member("transport"));
        var teamId = team.Member("team_id") ?? JsonValue.Null;
        var state = launch.Teardown.MemberLiveness(dir, target, c.TeamFile);
        var indeterminate = state.Get("indeterminate") is { IsTruthy: true };
        if (state.Get("live") is { IsTruthy: true } || indeterminate)
        {
            string? command = null;
            if (MemberPlanning.HasPane(c, target.Member("route")) && target.Member("transport_id") is { IsTruthy: true } tid)
            {
                var id = MemberPlanning.JsConvertText(tid);
                command = transport == "herdr" ? $"herdr pane close {id}" : transport == "tmux" ? $"tmux kill-pane -t {id}" : null;
            }

            var how = indeterminate ? UnknownWording(state, DateTimeOffset.UtcNow) : "is still live";
            c.Stderr.Write(
                $"ah roster: ah: {MemberPlanning.JsConvertText(target.Member("name"))} {how} ({transport} {MemberPlanning.JsConvertText(target.Member("transport_id"))}). " +
                $"Its record is gone from team {MemberPlanning.JsConvertText(teamId)}." +
                (command is not null ? $" Close it with `{command}` if you did not mean to leave it running.\n" : "\n"));
        }

        var teamEmpty = remaining.Count == 0;
        if (update.Cleared)
        {
            c.Stderr.Write($"ah roster: ah: team {MemberPlanning.JsConvertText(teamId)} has no members left, so it was removed.\n");
        }
        else if (teamEmpty && update.Rows is not null && !c.LaneActor)
        {
            c.Stderr.Write($"ah roster: ah: team {MemberPlanning.JsConvertText(teamId)} has no members left; it is kept, with no members.\n");
        }

        var result = new JsonObject
        {
            ["untracked"] = true,
            ["member"] = new JsonObject
            {
                ["role"] = target.Member("role") ?? JsonValue.Null,
                ["name"] = target.Member("name") ?? JsonValue.Null,
            },
            ["team_id"] = teamId,
            ["removed"] = name,
            ["remaining"] = TeardownSupport.Strings(remaining.Select(m => MemberPlanning.JsConvertText(m.Member("name")))),
            ["team_empty"] = teamEmpty,
        };
        if (update.Cleared)
        {
            result["team_removed"] = true;
        }

        result["config"] = JsonValue.Null;
        result["store"] = $"team {JsonText.Stringify(teamId, 0)}";
        if (c.Opts.IsTrue("also-config"))
        {
            var removal = new LevelFiles(c).RemoveConfigMember(MemberPlanning.JsConvertText(target.Member("name")), templateKey, sideEffect: true);
            if (!removal.Removed)
            {
                if (!removal.Unusable)
                {
                    c.Stderr.Write(
                        $"ah roster: ah: untracked {MemberPlanning.JsConvertText(target.Member("name"))} from team {MemberPlanning.JsConvertText(teamId)}, but no roster member named {MemberPlanning.JsConvertText(target.Member("name"))} exists at level \"{removal.Level}\" — the config was not changed.\n");
                }

                result["config"] = new JsonObject { ["removed"] = false, ["level"] = RosterJson.Text(removal.Level), ["reason"] = RosterJson.Text(removal.Reason) };
            }
            else
            {
                var reordinaled = new JsonArray();
                var before = removal.Before ?? new JsonArray();
                var after = removal.After ?? new JsonArray();
                var pairs = new List<string>();
                for (var i = removal.Index; i < before.Count - 1; i++)
                {
                    var oldName = MemberPlanning.JsConvertText(before[i + 1].Member("name"));
                    var newName = MemberPlanning.JsConvertText(after[i].Member("name"));
                    if (oldName == newName)
                    {
                        continue;
                    }

                    var recorded = remaining.FirstOrDefault(m => MemberPlanning.Text(m.Member("name")) == oldName);
                    var hasRecord = remaining.Any(m => MemberPlanning.Text(m.Member("name")) == oldName);
                    if (!hasRecord)
                    {
                        continue;
                    }

                    var oldState = launch.Teardown.MemberLiveness(dir, recorded, c.TeamFile);
                    if (oldState.Get("live") is { IsTruthy: true } || oldState.Get("indeterminate") is { IsTruthy: true })
                    {
                        reordinaled.Add(new JsonObject { ["from"] = oldName, ["to"] = newName });
                        pairs.Add($"{oldName} is now derived as {newName}");
                    }
                }

                result["config"] = new JsonObject
                {
                    ["removed"] = true,
                    ["level"] = RosterJson.Text(removal.Level),
                    ["path"] = RosterJson.Text(removal.Path),
                    ["reordinaled"] = reordinaled,
                };
                if (reordinaled.Count > 0)
                {
                    c.Stderr.Write(
                        $"ah roster: ah: removing {MemberPlanning.JsConvertText(target.Member("name"))} from the roster re-ordinals later {MemberPlanning.JsConvertText(target.Member("role"))} members: {string.Join(", ", pairs)}. " +
                        "Live team records keep their original names and still dispatch correctly; a future create/spawn-one will use the new names.\n");
                }
            }
        }

        return result;
    }
}
