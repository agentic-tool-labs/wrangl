using Ah.Cli.Verbs;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.StateDb;

namespace Ah.Cli.Composition;

/// <summary>The bridge's side of the rules (rules-of-engagement §6): what the dialog reads and writes. The repo comes from the team the client names; the client never sends a directory.</summary>
internal sealed class RulesUi(ServiceGraph g, string ahHome, Func<IReadOnlyList<string>> hierarchyDirs, Func<IStateDb> stateDb, Func<DateTimeOffset> now)
{
    /// <summary>The repo of <paramref name="team"/>; null with why when no repo, or more than one, has that team.</summary>
    private string? DirOfTeam(string team, out string? refused)
    {
        var found = hierarchyDirs().Distinct(StringComparer.Ordinal).Where(dir => g.Roster.ReadTeamRecord(dir, team) is { IsMap: true }).Select(MainOf).Distinct(StringComparer.Ordinal).ToList();
        refused = found.Count switch
        {
            0 => $"no team {team}",
            1 => null,
            _ => $"team {team} exists in more than one repo",
        };
        return refused is null ? found[0] : null;
    }

    /// <summary>Linked worktrees share one rule set, so a worktree's directory stands for its main one, as the verbs' does.</summary>
    private string MainOf(string dir) =>
        Path.GetDirectoryName(Path.GetDirectoryName(dir)) is { Length: > 0 } root ? g.Config.MainHierarchyDir(root) ?? dir : dir;

    private static JsonObject Refusal(string message) => new() { ["code"] = RulesOfEngagement.Refused, ["message"] = message };

    /// <summary>
    /// Every row of the team's repo unresolved, as <c>ah rules list --all</c>, with what the dialog needs beside it: the one resolver's answer at the repo, at the team and at each
    /// open project and goal, and those items.
    /// </summary>
    internal JsonObject Read(string team)
    {
        if (DirOfTeam(team, out var refused) is not { } dir)
        {
            return Refusal(refused!);
        }

        WorkVerb.Reconcile(g, ahHome, dir);
        return RulesReads.With(ahHome, new JsonObject { ["code"] = RulesOfEngagement.Ok }, query =>
        {
            var prefix = HierarchyModule.WorkPrefix(query, dir);
            var rows = RulesOfEngagement.Rows(query, dir);
            var teamSubject = RulesReads.SubjectOf(g, dir, team);
            var progressTeam = HierarchyModule.ProgressTeamOf(team);
            var board = HierarchyModule.WorkBoardOf(query, g.Hierarchy.Progress, dir);
            var items = new JsonArray();
            var at = new JsonObject();
            foreach (var view in board.Items.Where(v => v.Plan?.Kind is RulesOfEngagement.Project or RulesOfEngagement.Goal && WorkBoard.IsOpen(v) && (v.Row.IsBacklog || v.Row.Team == progressTeam)))
            {
                var id = prefix is null ? view.Row.Item : HierarchyModule.WorkId(prefix, view.Row.N);
                items.Add(new JsonObject { ["id"] = id, ["kind"] = view.Plan!.Kind!, ["title"] = view.Plan.Title });
                at[id] = RulesJson.Resolved(RulesOfEngagement.Resolve(rows, RulesOfEngagement.Over(teamSubject, board, [view])), prefix);
            }

            var all = new JsonArray();
            foreach (var row in rows)
            {
                all.Add(RulesJson.Row(row, prefix));
            }

            return new JsonObject
            {
                ["code"] = RulesOfEngagement.Ok,
                ["team"] = team,
                ["rows"] = all,
                ["resolved"] = new JsonObject
                {
                    [RulesOfEngagement.Repo] = RulesJson.Resolved(RulesOfEngagement.Resolve(rows, RuleSubject.RepoOnly), prefix),
                    [RulesOfEngagement.Team] = RulesJson.Resolved(RulesOfEngagement.Resolve(rows, teamSubject), prefix),
                    ["items"] = at,
                },
                ["items"] = items,
                ["max_text"] = RulesOfEngagement.MaxText,
                ["live"] = RulesOfEngagement.Live,
            };
        });
    }

    /// <summary>One write as the user: whoever holds a bridge session, recorded with no <c>said</c>. A team-level write with no scope is the named team's.</summary>
    internal JsonObject Write(JsonObject body)
    {
        string? Str(string key) => body.Get(key) is { IsText: true } value ? value.AsString() : null;

        var team = Str("team")!;
        if (DirOfTeam(team, out var refused) is not { } dir)
        {
            return Refusal(refused!);
        }

        var level = Str("level")!;
        var scope = level == RulesOfEngagement.Repo ? string.Empty : Str("scope") is { Length: > 0 } given ? given : level == RulesOfEngagement.Team ? team : string.Empty;
        var op = Str("op")!;
        if (!RulesOfEngagement.Ops.Contains(op, StringComparer.Ordinal))
        {
            return Refusal($"unknown op {op}");
        }

        var result = RulesWrites.Apply(g, ahHome, stateDb(), new RuleAsk(op, dir, level, scope, Str("key")!, Str("text"), "user", null, Str("applies"), Str("choose")), now());
        var answer = RulesJson.Answer(result, RulesReads.With<string?>(ahHome, null, query => HierarchyModule.WorkPrefix(query, dir)));
        if (op == "accept" && result.Code == RulesOfEngagement.Ok)
        {
            answer["via"] = "web";
        }

        return answer;
    }
}
