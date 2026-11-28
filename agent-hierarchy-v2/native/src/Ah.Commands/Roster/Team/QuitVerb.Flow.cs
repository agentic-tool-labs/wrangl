using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Roster;

namespace Ah.Commands;

/// <summary>
/// The quit itself. <c>--wait</c> is the whole window, measured from the hold's <c>since</c>; one call waits at most
/// <see cref="CallBudget"/> seconds so it never outlives a Bash limit, and a rerun or a killed call loses nothing. One pass covers the
/// team and all its lanes, whether or not a lane's lead is alive.
/// </summary>
internal static partial class QuitVerb
{
    private const int CallBudget = 100;

    /// <summary>One covered team of a quit: its hold, its checkpoint record, and the answers read so far.</summary>
    private sealed class TeamQuit(string? team, JsonObject hold, JsonValue recorded)
    {
        internal string? Team { get; } = team;

        internal JsonObject Hold { get; } = hold;

        internal JsonValue Recorded { get; } = recorded;

        internal List<Answered> Answers { get; set; } = [];

        internal bool Sealed { get; set; }

        /// <summary>Its own hold was removed, or replaced by one not a quit's, during the wait: the quit no longer waits on its members.</summary>
        internal bool Overtaken { get; set; }
    }

    /// <summary>One top team and its lanes, parents first, under one window.</summary>
    private sealed class TreeQuit(string? top)
    {
        internal string? Top { get; } = top;

        internal List<TeamQuit> Teams { get; } = [];

        internal JsonArray Skipped { get; } = [];

        internal JsonArray Refused { get; } = [];

        internal bool MailFailed { get; set; }

        internal JsonArray MailErrors { get; } = [];

        internal JsonArray Abandoned { get; } = [];

        internal List<string> Warnings { get; } = [];

        internal bool Already { get; set; }

        internal string? Failure { get; set; }

        internal JsonArray Members { get; set; } = [];

        internal JsonObject Drained { get; set; } = new() { ["entries"] = 0, ["wake_files"] = 0 };

        internal IEnumerable<Answered> Waiting => Teams.Where(static t => !t.Sealed && !t.Overtaken).SelectMany(static t => t.Answers).Where(static a => a.Answer == "none");

        internal bool AnyUnanswered => Teams.SelectMany(static t => t.Answers).Any(static a => a.Answer == "none");

        internal bool Open => Failure is null && !Already;

        internal bool CanSeal => Open && Refused.Count == 0;
    }

    private static int Quit(RosterContext c)
    {
        var wait = WaitSeconds(c);
        var all = c.Opts.IsTrue("all");
        var budgetEnd = c.Services.Clock.UtcNow.AddSeconds(c.QuitCallBudgetSeconds ?? CallBudget);
        var trees = new List<TreeQuit>();
        foreach (var top in all ? AllTops(c) : [c.TeamFile])
        {
            var tree = new TreeQuit(top);
            trees.Add(tree);
            OnTree(c, tree, all, () => Prepare(c, tree));
        }

        WaitForAnswers(c, trees, wait, budgetEnd);

        var now = c.Services.Clock.UtcNow;
        foreach (var tree in trees.Where(t => t.CanSeal && (!t.Waiting.Any() || now >= WindowEnd(t, wait))))
        {
            OnTree(c, tree, all, () => SealTree(c, tree));
        }

        var reports = trees.Select(t => Report(c, t, all)).ToList();
        if (!all)
        {
            c.Out(reports[0].Output);
            return reports[0].Exit;
        }

        var teams = new JsonArray();
        teams.AddRange(reports.Select(static r => (JsonValue)r.Output));
        c.Out(new JsonObject { ["teams"] = teams });
        return reports.Select(static r => r.Exit).MinBy(ExitRank);
    }

    /// <summary>The exit order: 1 (mail failed or a refusal), 4 (still winding down), 3 (sealed with unanswered members), 0.</summary>
    private static int ExitRank(int exit) => exit switch { 1 => 0, 4 => 1, 3 => 2, _ => 3 };

    /// <summary>Runs a step with <see cref="RosterContext.TeamFile"/> on the tree's top. With <c>--all</c> a refused team is reported and the others go on; alone, its refusal ends the verb.</summary>
    private static void OnTree(RosterContext c, TreeQuit tree, bool all, Action body) => InTeam(c, tree.Top, () =>
    {
        if (!all)
        {
            body();
            return;
        }

        Trapping(c, body, error => tree.Failure = error);
    });

    private static void InTeam(RosterContext c, string? team, Action body) => CheckpointVerb.WithTeam(c, team, () =>
    {
        body();
        return true;
    });

    /// <summary>Runs <paramref name="body"/> with refusals unprinted, and hands a refusal's text to <paramref name="refused"/>.</summary>
    private static void Trapping(RosterContext c, Action body, Action<string> refused)
    {
        var trapped = c.TrapRefusals;
        c.TrapRefusals = true;
        try
        {
            body();
        }
        catch (RosterFailure error)
        {
            refused(error.Message);
        }
        finally
        {
            c.TrapRefusals = trapped;
        }
    }

    private static List<string?> AllTops(RosterContext c)
    {
        var tops = CheckpointVerb.OwnedTeams(c).Where(t => c.Services.Roster.ReadTeamRecord(c.Dir, t).Member("parent") is not { IsMap: true }).ToList();
        return tops.Count > 0 ? tops : throw new RosterFailure($"{c.Command} --all: this session owns no team");
    }

    /// <summary>Holds and checkpoints the top, then every lane parents first, so every lane member is asked at the start.</summary>
    private static void Prepare(RosterContext c, TreeQuit tree)
    {
        var top = tree.Top;
        var roster = c.Services.Roster;
        var record = roster.ReadTeamRecord(c.Dir, top);
        if (record.IsNull)
        {
            throw new RosterFailure($"{c.Command}: there is no team {(top is null ? "(the default team)" : $"\"{top}\"")} to put on hold");
        }

        var effective = roster.HoldOf(c.Dir, top);
        if (effective is { Kind: "stop" })
        {
            throw HoldStore.Refuse(c, "team_stopped", $"team {top ?? "default"} is stopped; only `ah roster resume --hold --team {HoldStore.TeamArg(effective.Source)}` on the user's order resumes it");
        }

        if (record.Member("parent") is { IsMap: true } && effective?.CascadeFrom is null && HoldStore.ReadOwn(c, top) is null)
        {
            throw HoldStore.Refuse(c, "quit_lane_without_parent", "a lane is quit through its parent; ask the top Orchestrator");
        }

        var first = Ensure(c, tree, top);
        tree.Teams.Add(first);
        tree.Already = first.Sealed;
        if (tree.Already)
        {
            return;
        }

        foreach (var lane in ParentsFirst(c, top).Skip(1))
        {
            InTeam(c, lane, () => Trapping(c, () => PrepareLane(c, tree, lane), error => tree.Refused.Add(new JsonObject { ["team"] = lane!, ["error"] = error })));
        }
    }

    private static void PrepareLane(RosterContext c, TreeQuit tree, string? lane)
    {
        if (c.Services.Roster.HoldOf(c.Dir, lane) is { Kind: "stop" })
        {
            tree.Skipped.Add(new JsonObject { ["team"] = lane!, ["reason"] = "stopped" });
            return;
        }

        tree.Teams.Add(Ensure(c, tree, lane));
    }

    /// <summary>
    /// The single-team step: writes the team's hold unless it has one, and takes its checkpoint unless the hold names one. A rerun, or a
    /// lane lead's own <c>quit</c>, finds both and joins them.
    /// </summary>
    private static TeamQuit Ensure(RosterContext c, TreeQuit tree, string? team)
    {
        var roster = c.Services.Roster;
        var hold = HoldStore.ReadOwn(c, team);
        if (hold is null && HoldStore.OwnUnreadable(c, team))
        {
            throw HoldStore.Refuse(c, "hold_unreadable", $"the hold file {roster.HoldPath(c.Dir, team)} cannot be read; the team stays held. Fix or remove it by hand");
        }

        if (hold is not null && HoldStore.Field(hold, "phase") == "held")
        {
            return new TeamQuit(team, hold, JsonValue.Null) { Sealed = true };
        }

        var note = c.Opts.Text("note") ?? string.Empty;
        if (hold is null)
        {
            var now = c.Services.Clock.UtcNow;
            var by = HoldStore.By(c, team);
            var effective = roster.HoldOf(c.Dir, team);
            hold = HoldStore.Change(c, team, current =>
            {
                if (current is not null && HoldStore.Field(current, "kind") != "quit")
                {
                    throw Stopped(c, team);
                }

                return current ?? new JsonObject
                {
                    ["v"] = 1,
                    ["team"] = team is null ? JsonValue.Null : team,
                    ["kind"] = "quit",
                    ["phase"] = "winding-down",
                    // a lane shares the since of the hold above it: mail is stamped drained by that since, and the first drain covers the lanes
                    ["since"] = effective?.Since is { } shared ? shared : Timestamps.ToIsoString(now),
                    ["sealed_at"] = JsonValue.Null,
                    ["by"] = by,
                    ["note"] = note.Length == 0 ? JsonValue.Null : note,
                    ["reason"] = JsonValue.Null,
                    ["checkpoint"] = JsonValue.Null,
                    ["plan"] = JsonValue.Null,
                    ["evidence"] = JsonValue.Null,
                    ["assessment"] = JsonValue.Null,
                    ["cascade_from"] = effective?.CascadeFrom is { } cascade ? cascade : JsonValue.Null,
                    ["previous"] = JsonValue.Null,
                };
            })!;
            if (HoldStore.Field(hold, "phase") == "held")
            {
                return new TeamQuit(team, hold, JsonValue.Null) { Sealed = true };
            }
        }

        var checkpoint = HoldStore.Field(hold, "checkpoint");
        if (checkpoint is null)
        {
            var holding = hold;
            var winding = new EffectiveHold(team, null, HoldStore.PathOf(c, team), "quit", "winding-down", HoldStore.Field(hold, "since"), hold, null);
            _ = DrainAll(c, roster.HoldCovered(c.Dir, team), winding);
            var taken = CheckpointVerb.Take(c, team, note, quit: true, decide: true);
            tree.MailFailed |= taken.Get("mail_failed") is { IsTruthy: true };
            foreach (var asked in taken.Get("members") is { IsArray: true } rows ? rows.AsArray() : [])
            {
                if (asked.Member("mail_error") is { IsText: true } refusal)
                {
                    tree.MailErrors.Add(new JsonObject { ["team"] = team is null ? JsonValue.Null : team, ["member"] = asked.Member("name") ?? JsonValue.Null, ["error"] = refusal });
                }
            }

            checkpoint = MemberPlanning.JsConvertText(taken.Get("record"));
            hold["checkpoint"] = checkpoint;
            _ = HoldStore.Change(c, team, current =>
            {
                StillQuit(c, team, current, holding);
                return holding;
            });
        }

        return new TeamQuit(team, hold, CheckpointVerb.RecordAt(c, checkpoint) ?? throw new RosterFailure($"quit: no such checkpoint record {checkpoint}"));
    }

    /// <summary>The covered teams whose own quit hold is still winding down.</summary>
    internal static List<string?> WindingDown(RosterContext c, string? top) =>
        ParentsFirst(c, top).Where(t => HoldStore.ReadOwn(c, t) is { } own && HoldStore.Field(own, "kind") == "quit" && HoldStore.Field(own, "phase") == "winding-down").ToList();

    /// <summary>Seals with window 0 what a killed or unfinished quit left winding down, so a resume never needs a second quit.</summary>
    internal static void SealForResume(RosterContext c, string? top)
    {
        var tree = new TreeQuit(top);
        foreach (var team in WindingDown(c, top))
        {
            if (team == top)
            {
                tree.Teams.Add(Ensure(c, tree, team));
            }
            else
            {
                InTeam(c, team, () => Trapping(c, () => tree.Teams.Add(Ensure(c, tree, team)), error => tree.Skipped.Add(new JsonObject { ["team"] = team!, ["reason"] = error })));
            }
        }

        foreach (var quit in tree.Teams)
        {
            InTeam(c, quit.Team, () => quit.Answers = Collect(c, c.Dir, quit.Recorded));
        }

        SealTree(c, tree);
    }

    /// <summary>The covered teams, each parent before its lanes.</summary>
    internal static List<string?> ParentsFirst(RosterContext c, string? top)
    {
        var roster = c.Services.Roster;
        var covered = roster.HoldCovered(c.Dir, top);
        var order = new List<string?> { top };
        for (var at = 0; at < order.Count; at++)
        {
            var parent = order[at];
            order.AddRange(covered.Where(t => t is not null && !order.Contains(t) && MemberPlanning.Text(roster.ReadTeamRecord(c.Dir, t).Member("parent")?.Member("team")) == parent).ToList());
        }

        return order;
    }

    /// <summary>When the top's window ends. A <c>since</c> that cannot be read has no window left, so the hold seals rather than never ending.</summary>
    private static DateTimeOffset WindowEnd(TreeQuit tree, int wait) =>
        DateTimeOffset.TryParse(HoldStore.Field(tree.Teams[0].Hold, "since"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var since)
            ? since.AddSeconds(wait)
            : DateTimeOffset.MinValue;

    /// <summary>Reads the answers of every covered team until none is missing, every window is over, or this call's budget is spent.</summary>
    private static void WaitForAnswers(RosterContext c, List<TreeQuit> trees, int wait, DateTimeOffset budgetEnd)
    {
        while (true)
        {
            var now = c.Services.Clock.UtcNow;
            foreach (var quit in trees.Where(static t => t.Open).SelectMany(static t => t.Teams).Where(static t => !t.Sealed))
            {
                InTeam(c, quit.Team, () =>
                {
                    var own = HoldStore.ReadOwn(c, quit.Team);
                    quit.Sealed = own is not null && HoldStore.Field(own, "phase") == "held";
                    quit.Overtaken |= !quit.Sealed && (own is null ? !HoldStore.OwnUnreadable(c, quit.Team) : HoldStore.Field(own, "kind") != "quit");
                    quit.Answers = quit.Sealed ? [] : Collect(c, c.Dir, quit.Recorded);
                });
            }

            var ends = trees.Where(t => t.CanSeal && t.Waiting.Any()).Select(t => WindowEnd(t, wait)).Where(end => now < end).ToList();
            if (ends.Count == 0 || now >= budgetEnd)
            {
                return;
            }

            var left = (ends.Min() < budgetEnd ? ends.Min() : budgetEnd) - now;
            c.Services.Sleep(left < TimeSpan.FromSeconds(5) ? left : TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Seals deepest first, so the top's plan embeds each lane's. A lane changed meanwhile, say by a stop, stays as it is and is listed.</summary>
    private static void SealTree(RosterContext c, TreeQuit tree)
    {
        foreach (var quit in Enumerable.Reverse(tree.Teams).Where(static t => !t.Sealed))
        {
            if (quit.Team == tree.Top)
            {
                SealOne(c, tree, quit);
                continue;
            }

            InTeam(c, quit.Team, () => Trapping(c, () => SealOne(c, tree, quit), error => tree.Skipped.Add(new JsonObject { ["team"] = quit.Team!, ["reason"] = error })));
        }
    }

    private static void SealOne(RosterContext c, TreeQuit tree, TeamQuit quit)
    {
        var team = quit.Team;
        var hold = quit.Hold;
        var sealedAt = c.Services.Clock.UtcNow;
        var holdNow = new EffectiveHold(team, null, HoldStore.PathOf(c, team), "quit", "winding-down", HoldStore.Field(hold, "since"), hold, null);
        var drained = SealDrain(c, c.Services.Roster.HoldCovered(c.Dir, team), holdNow);
        var planPath = HoldStore.Named(c, team, HoldStore.StampOfSince(HoldStore.Field(hold, "since")), "resume-plan.md");
        var plan = BuildPlan(c, team, hold, sealedAt, quit.Recorded, quit.Answers, drained);
        HoldStore.WriteFile(c, planPath, plan.Text);
        hold["phase"] = "held";
        hold["sealed_at"] = Timestamps.ToIsoString(sealedAt);
        hold["plan"] = planPath;
        _ = HoldStore.Change(c, team, current =>
        {
            StillQuit(c, team, current, hold);
            return hold;
        });
        quit.Sealed = true;
        AbandonBriefs(c, tree, team, quit.Recorded);
        if (team == tree.Top)
        {
            tree.Members = plan.Members;
            tree.Drained = drained.Totals;
        }
    }

    /// <summary>
    /// A sealed team's members leave their open briefs abandoned, as dismiss does, so no clock keeps timing them. Checkpoint briefs stay
    /// open: resume reads a checkpoint's response as the member's answer. A member the quit's checkpoint recorded is one resume relaunches,
    /// so it keeps all its open briefs and can still answer them; only a member the checkpoint does not record (a row added after it) has them abandoned.
    /// </summary>
    private static void AbandonBriefs(RosterContext c, TreeQuit tree, string? team, JsonValue recorded)
    {
        var warnings = new List<string>();
        var relaunched = (recorded.Member("members") is { IsArray: true } listed ? listed.AsArray() : [])
            .Select(static m => MemberPlanning.Text(m.Member("name")))
            .ToHashSet(StringComparer.Ordinal);
        var record = c.Services.Roster.ReadTeamRecord(c.Dir, team);
        foreach (var m in record.Member("members") is { IsArray: true } rows ? rows.AsArray() : [])
        {
            var name = MemberPlanning.JsConvertText(m.Member("name"));
            if (relaunched.Contains(name))
            {
                continue;
            }

            var summary = new JsonObject();
            DismissVerb.AbandonOpenBriefs(c, c.Dir, name, m, warnings, summary, soleOfRole: true, $"team {team ?? "default"} was quit", $"quit of {name}", keepCheckpoints: true);
            if (summary.Get("briefs_abandoned") is { IsArray: true } closed)
            {
                tree.Abandoned.AddRange(closed.AsArray());
            }
        }

        tree.Warnings.AddRange(warnings);
    }

    private static (JsonObject Output, int Exit) Report(RosterContext c, TreeQuit tree, bool all)
    {
        var team = tree.Top;
        if (tree.Failure is { } failure)
        {
            return (new JsonObject { ["verb"] = "quit", ["team"] = team ?? "_default", ["refused"] = failure }, 1);
        }

        var hold = tree.Teams[0].Hold;
        var laneArray = new JsonArray();
        laneArray.AddRange(Lanes(c, team).Select(static l => (JsonValue)l));
        var output = new JsonObject
        {
            ["verb"] = "quit",
            ["team"] = team ?? "_default",
            ["state"] = "quit",
            ["phase"] = HoldStore.Field(hold, "phase") ?? "held",
            ["already"] = tree.Already,
            ["since"] = hold.Get("since") ?? JsonValue.Null,
            ["sealed_at"] = hold.Get("sealed_at") ?? JsonValue.Null,
            ["hold"] = HoldStore.PathOf(c, team),
            ["checkpoint"] = hold.Get("checkpoint") ?? JsonValue.Null,
            ["plan"] = hold.Get("plan") ?? JsonValue.Null,
            ["members"] = tree.Members,
            ["lanes"] = laneArray,
            ["drained"] = tree.Drained,
            ["mail_failed"] = tree.MailFailed,
        };
        if (tree.MailErrors.Count > 0)
        {
            output["mail_errors"] = tree.MailErrors;
        }

        if (tree.Abandoned.Count > 0)
        {
            output["briefs_abandoned"] = tree.Abandoned;
        }

        if (tree.Warnings.Count > 0)
        {
            output["warnings"] = TeardownSupport.Strings(tree.Warnings);
        }

        if (tree.Skipped.Count > 0)
        {
            output["skipped"] = tree.Skipped;
        }

        if (tree.Teams[0].Sealed)
        {
            output["next"] = $"Hand the plan to the user. Resume only on the user's order: ah roster resume --hold --team {HoldStore.TeamArg(team)} (it can pass 120 s: give it a 600 s timeout or run it in the background)";
            return (output, tree.MailFailed ? 1 : tree.AnyUnanswered ? 3 : 0);
        }

        var waiting = new JsonArray();
        waiting.AddRange(tree.Waiting.Select(static a => (JsonValue)a.Name));
        output["waiting_on"] = waiting;
        output["phase"] = "winding-down";
        if (tree.Refused.Count > 0)
        {
            output["refused"] = tree.Refused;
        }

        output["next"] = "ah roster quit" + (all ? " --all" : " --team " + HoldStore.TeamArg(team)) + (c.Opts.Text("wait") is { } wait ? " --wait " + wait : string.Empty);
        return (output, tree.MailFailed || tree.Refused.Count > 0 ? 1 : 4);
    }
}
