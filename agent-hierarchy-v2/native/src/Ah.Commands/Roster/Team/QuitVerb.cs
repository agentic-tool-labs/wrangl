using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Roster;

namespace Ah.Commands;

/// <summary>
/// <c>quit [--team T] [--wait SECONDS] [--note TEXT]</c> (alias <c>pause</c>): puts the team on hold, asks every member that is not
/// idle and clean to save its work and write a resume plan, waits for the answers, then seals the hold with the team's resume plan.
/// <c>quit --status</c> reads the hold. Nothing here needs the daemon.
/// </summary>
internal static partial class QuitVerb
{
    private const int DefaultWait = 420;

    private const int MaxWait = 540;

    private static readonly string[] Flags = ["cwd", "team", "all", "wait", "note", "status", "orchestrator-pid"];

    internal static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure($"{c.Command}: unrecognized flag --{key} (use --team, --all, --wait, --note, --status)");
            }
        }

        if (c.Opts.IsTrue("all") && (c.Opts.Has("team") || c.Opts.IsTrue("status")))
        {
            throw HoldStore.Refuse(c, "bad_flags", "--all excludes --team and --status");
        }

        return c.Opts.IsTrue("status") ? Status(c) : Quit(c);
    }

    // ---------------------------------------------------------------- status

    private static int Status(RosterContext c)
    {
        var team = c.TeamFile;
        var hold = c.Services.Roster.HoldOf(c.Dir, team);
        var path = c.Services.Roster.HoldPath(c.Dir, hold?.CascadeFrom ?? team);
        var result = new JsonObject
        {
            ["team"] = team ?? "_default",
            ["effective"] = hold is null ? "running" : hold.Kind,
            ["phase"] = hold is null ? JsonValue.Null : hold.Phase,
            ["cascade_from"] = hold?.CascadeFrom is { } from ? from : JsonValue.Null,
            ["hold"] = hold is { Record.IsMap: true } ? hold.Record : JsonValue.Null,
            ["path"] = path,
        };
        if (hold?.ParseError is { } error)
        {
            result["parse_error"] = error;
        }

        c.Out(result);
        return hold is null ? 0 : 5;
    }

    /// <summary>
    /// Refuses unless the team's own hold is still this quit's <paramref name="hold"/> (same kind and since): a stop that landed during
    /// the wind-down is never overwritten, and a hold another quit replaced or a resume released is never written back.
    /// </summary>
    internal static void StillQuit(RosterContext c, string? team, JsonObject? now, JsonObject hold)
    {
        if (HoldStore.Same(now, hold))
        {
            return;
        }

        throw now is not null && HoldStore.Field(now, "kind") != "quit"
            ? Stopped(c, team)
            : HoldStore.Refuse(c, "hold_changed", $"the hold of team {team ?? "default"} was replaced or released during the quit; nothing is written");
    }

    private static RosterFailure Stopped(RosterContext c, string? team) =>
        HoldStore.Refuse(c, "team_stopped", $"team {team ?? "default"} was stopped during the quit; the stop hold is left as it is");

    private static int WaitSeconds(RosterContext c)
    {
        if (!c.Opts.Has("wait"))
        {
            return DefaultWait;
        }

        var text = c.Opts.Text("wait");
        if (text is null || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds > MaxWait)
        {
            throw HoldStore.Refuse(c, "bad_wait", $"--wait must be a whole number of seconds from 0 to {MaxWait}");
        }

        return seconds;
    }

    // ---------------------------------------------------------------- waiting

    private sealed record Answered(string Name, string Classification, string Answer, string? Response);

    private static List<Answered> Collect(RosterContext c, string dir, JsonValue recorded)
    {
        var cache = new Dictionary<string, (IReadOnlyList<string> Pools, List<JsonObject> Exchanges)>(StringComparer.Ordinal);
        var list = new List<Answered>();
        if (recorded.Member("members") is not { IsArray: true } members)
        {
            return list;
        }

        foreach (var entry in members.AsArray())
        {
            var name = MemberPlanning.Text(entry.Member("name")) ?? string.Empty;
            var classification = MemberPlanning.Text(entry.Member("classification")) ?? string.Empty;
            if (classification != "asked")
            {
                list.Add(new Answered(name, classification, "not-asked", null));
                continue;
            }

            var read = CheckpointVerb.ReadResponse(c, dir, entry, MemberPlanning.Text(entry.Member("worktree")), cache);
            list.Add(new Answered(name, classification, read.Answer is null ? "none" : read.Answer.Ready ? "ready" : "not-ready", read.Response));
        }

        return list;
    }

    // ---------------------------------------------------------------- drain

    internal sealed record Drained(JsonObject Totals, List<JsonObject> Rows);

    /// <summary>Every entry of the covered teams' boxes that this quit's hold drained, whether at the first drain, at append or at the seal.</summary>
    private static List<JsonObject> DrainedRowsOf(RosterContext c, IReadOnlyList<string?> covered, EffectiveHold hold)
    {
        var rows = new List<JsonObject>();
        if (c.Services.Mailbox is not { } mailbox)
        {
            return rows;
        }

        foreach (var team in covered)
        {
            if (!c.Services.Roster.ReadTeamRecord(c.Dir, team).IsNull)
            {
                rows.AddRange(mailbox.DrainedBy(c.Services.Roster.TeamPath(c.Dir, team), team ?? "_default", hold).Get("rows")!.Value.AsArray().Select(r => r.AsObject()));
            }
        }

        return rows;
    }

    /// <summary>The last sweep of a quit: its rows are every row the hold drained across all passes, and <c>entries</c> counts those rows.</summary>
    internal static Drained SealDrain(RosterContext c, IReadOnlyList<string?> covered, EffectiveHold hold)
    {
        var drained = DrainAll(c, covered, hold);
        var rows = DrainedRowsOf(c, covered, hold);
        drained.Totals["entries"] = rows.Count;
        return drained with { Rows = rows };
    }

    /// <summary>D2: every covered team's unacked, unended mail gets a drained row, and every covered session's wake files are renamed away.</summary>
    internal static Drained DrainAll(RosterContext c, IReadOnlyList<string?> covered, EffectiveHold hold)
    {
        var entries = 0;
        var wakes = 0;
        var rows = new List<JsonObject>();
        var mailbox = c.Services.Mailbox;
        foreach (var team in covered)
        {
            var record = c.Services.Roster.ReadTeamRecord(c.Dir, team);
            if (record.IsNull)
            {
                continue;
            }

            if (mailbox is null)
            {
                continue;
            }

            // a member's pending resume entries are withdrawn first, so the drain below neither reports nor holds them
            var teamFile = c.Services.Roster.TeamPath(c.Dir, team);
            if (record.Member("members") is { IsArray: true } members)
            {
                foreach (var member in members.AsArray())
                {
                    if (MemberPlanning.Text(member.Member("name")) is { Length: > 0 } name)
                    {
                        wakes += mailbox.WithdrawRewakes(mailbox.BoxDirFor(teamFile, name), null, null, null, "drained-by-quit");
                    }
                }
            }

            var report = mailbox.DrainTeam(teamFile, team ?? "_default", hold);
            entries += (int)report.Get("entries")!.Value.AsNumber();
            rows.AddRange(report.Get("rows")!.Value.AsArray().Select(r => r.AsObject()));
        }

        return new Drained(new JsonObject { ["entries"] = entries, ["wake_files"] = wakes }, rows);
    }

    // ---------------------------------------------------------------- plan

    private sealed record PlanOut(string Text, JsonArray Members);

    [GeneratedRegex(@"^## \[2\][^\n]*\n", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PlanHeading();

    [GeneratedRegex(@"^## \[", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex AnyHeading();

    /// <summary>The member's resume-plan section: <c>## [2]</c> of its response up to the next heading, kept only when it has the plan's bullets.</summary>
    private static string? PlanSection(RosterContext c, string? responsePath)
    {
        if (responsePath is null || c.Services.Hierarchy.Core.ReadMsgFile(responsePath).Member("text") is not { IsText: true } stored)
        {
            return null;
        }

        var text = stored.AsString().ReplaceLineEndings("\n");
        var start = PlanHeading().Match(text);
        if (!start.Success)
        {
            return null;
        }

        var rest = text[(start.Index + start.Length)..];
        var next = AnyHeading().Match(rest);
        var body = (next.Success ? rest[..next.Index] : rest).Trim('\n');
        return body.Contains("Doing:", StringComparison.Ordinal) || body.Contains("Next:", StringComparison.Ordinal) ? body : null;
    }

    private static PlanOut BuildPlan(RosterContext c, string? team, JsonObject hold, DateTimeOffset sealedAt, JsonValue recorded, List<Answered> answers, Drained drained)
    {
        var name = team ?? "_default";
        var since = HoldStore.Field(hold, "since") ?? "unknown";
        var by = hold.Get("by") is { IsMap: true } b ? b.AsObject() : new JsonObject();
        var text = new StringBuilder();
        text.Append("# Resume plan: team ").Append(name).Append('\n')
            .Append("- held: quit since ").Append(since).Append(", sealed ").Append(Timestamps.ToIsoString(sealedAt))
            .Append(", by ").Append(HoldStore.Field(by, "role") ?? "orchestrator").Append(" pid ").Append(by.Get("pid") is { IsNumber: true } pid ? ((long)pid.AsNumber()).ToString(CultureInfo.InvariantCulture) : "unknown").Append('\n')
            .Append("- note: ").Append(HoldStore.Field(hold, "note") ?? "none").Append('\n')
            .Append("- checkpoint: ").Append(HoldStore.Field(hold, "checkpoint") ?? "none").Append("   hold: ").Append(HoldStore.PathOf(c, team)).Append('\n')
            .Append("- resume (user's order only): ah roster resume --hold --team ").Append(HoldStore.TeamArg(team)).Append("   (plan, then --yes)\n\n")
            .Append("## Members\n| name | role | classification | answer | branch | head | worktree |\n|---|---|---|---|---|---|---|\n");
        var members = new JsonArray();
        var sections = new StringBuilder();
        var openRequests = new StringBuilder();
        var entries = recorded.Member("members") is { IsArray: true } m ? m.AsArray().ToList() : [];
        foreach (var entry in entries)
        {
            var memberName = MemberPlanning.Text(entry.Member("name")) ?? string.Empty;
            var answer = answers.FirstOrDefault(a => a.Name == memberName) ?? new Answered(memberName, string.Empty, "not-asked", null);
            var branch = MemberPlanning.Text(entry.Member("branch")) ?? "none";
            var head = MemberPlanning.Text(entry.Member("head")) ?? "none";
            var tree = MemberPlanning.Text(entry.Member("worktree"));
            text.Append("| ").Append(memberName).Append(" | ").Append(MemberPlanning.Text(entry.Member("role")) ?? string.Empty)
                .Append(" | ").Append(answer.Classification).Append(" | ").Append(answer.Answer).Append(" | ").Append(branch)
                .Append(" | ").Append(head).Append(" | ").Append(tree ?? "none").Append(" |\n");

            var section = answer.Answer == "none" || answer.Answer == "not-asked" ? null : PlanSection(c, answer.Response);
            sections.Append("\n## ").Append(memberName).Append('\n');
            if (section is not null)
            {
                sections.Append(section).Append('\n');
            }
            else
            {
                var dirty = tree is not null && ReleaseGate.TreeState(c, tree).Dirty;
                var open = entry.Member("open_requests") is { IsArray: true } o ? string.Join(", ", o.AsArray().Select(r => r.IsText ? r.AsString() : string.Empty)) : string.Empty;
                sections.Append("STUB (no answer by ").Append(Timestamps.ToIsoString(sealedAt)).Append("): branch=").Append(branch).Append(" head=").Append(head)
                    .Append(" dirty=").Append(dirty ? "yes" : "no").Append("; open requests: ").Append(open.Length == 0 ? "none" : open).Append('\n');
            }

            if (entry.Member("open_requests") is { IsArray: true } ids)
            {
                foreach (var id in ids.AsArray().Where(static r => r.IsText))
                {
                    openRequests.Append("- ").Append(id.AsString()).Append(" (").Append(memberName).Append(")\n");
                }
            }

            members.Add(new JsonObject
            {
                ["name"] = memberName,
                ["classification"] = answer.Classification,
                ["answer"] = answer.Answer,
                ["plan_section"] = section is not null ? "member" : "stub",
            });
        }

        text.Append(sections);
        text.Append("\n## Lanes\n");
        foreach (var lane in Lanes(c, team))
        {
            var laneName = lane.Get("team")!.Value.AsString();
            text.Append("### ").Append(laneName).Append('\n');
            if (lane.Get("plan") is { IsText: true } planPath && c.Services.Files.Exists(planPath.AsString()))
            {
                text.Append(Demote(c.Services.Files.ReadText(planPath.AsString()))).Append('\n');
            }
            else
            {
                text.Append("no plan: lane is ").Append(lane.Get("phase")!.Value.AsString()).Append('\n');
            }
        }

        text.Append("\n## Drained mail\n| id | box | from | kind | created | summary |\n|---|---|---|---|---|---|\n");
        foreach (var row in drained.Rows)
        {
            text.Append("| ").Append(HoldStore.Field(row, "id")).Append(" | ").Append(HoldStore.Field(row, "box")).Append(" | ").Append(HoldStore.Field(row, "from"))
                .Append(" | ").Append(HoldStore.Field(row, "kind")).Append(" | ").Append(HoldStore.Field(row, "created")).Append(" | ").Append(HoldStore.Field(row, "summary")).Append(" |\n");
        }

        text.Append("\n## Open requests\n").Append(openRequests.Length == 0 ? "none\n" : openRequests.ToString());
        return new PlanOut(text.ToString(), members);
    }

    /// <summary>Headings one level deeper, so a lane's plan nests under its heading.</summary>
    private static string Demote(string plan) => HeadingMarks().Replace(plan.ReplaceLineEndings("\n").TrimEnd('\n'), "#$1 ");

    [GeneratedRegex("^(#+) ", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex HeadingMarks();

    // ---------------------------------------------------------------- output

    /// <summary>The direct lanes of <paramref name="team"/> and where each one's hold stands.</summary>
    private static List<JsonObject> Lanes(RosterContext c, string? team)
    {
        var lanes = new List<JsonObject>();
        foreach (var child in c.Services.Roster.HoldCovered(c.Dir, team).Where(t => t != team && t is not null))
        {
            if (c.Services.Roster.ReadTeamRecord(c.Dir, child).Member("parent")?.Member("team") is not { IsText: true } parent || parent.AsString() != team)
            {
                continue;
            }

            var own = HoldStore.ReadOwn(c, child);
            lanes.Add(new JsonObject
            {
                ["team"] = child!,
                ["phase"] = own is null ? "none" : HoldStore.Field(own, "phase") ?? "none",
                ["plan"] = own is not null && HoldStore.Field(own, "plan") is { } p ? p : JsonValue.Null,
            });
        }

        return lanes;
    }
}
