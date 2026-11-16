using System.Globalization;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Watch;

namespace Ah.Commands;

/// <summary>
/// <c>ah progress item add|start|done|drop|reopen|retitle|link|note &lt;stream&gt; &lt;item&gt;</c> and <c>ah progress item import &lt;stream&gt;</c>:
/// records a stream's plan in its plan log and renders the stream's Checklist from it. It runs as the roster command
/// <c>progress</c>, so the owner proof and the owned-team scope are the shared layer's.
/// </summary>
internal static class ProgressItemVerb
{
    private const string Usage =
        "usage: ah progress item add|start|done|drop|reopen|retitle|link|note|rank|area|assign|next|block|wait-user|wait-orch|role|todo|ref|unblock <stream> <item> [--title T] [--commit SHA]... [--branch B] [--issue N] [--note T] [--rank N] [--area T] [--holder T] [--role R] [--todo T] [--ref ID] [--team T]"
        + " | ah progress item import <stream> [--team T]";

    private static readonly HashSet<string> ValueFlags = new(StringComparer.Ordinal)
    {
        "team", "cwd", "orchestrator-pid", "title", "commit", "branch", "issue", "note", "rank", "area", "holder", "role", "todo", "ref",
    };

    private static readonly HashSet<string> ImportFlags = new(StringComparer.Ordinal) { "team", "cwd", "orchestrator-pid" };

    private static readonly string[] Events = ["add", "start", "done", "drop", "reopen", "retitle", "link", "note", "rank", "area", "assign", "next", "block", "wait-user", "unblock", "wait-orch", "role", "todo", "ref"];

    internal static int Run(RosterContext c)
    {
        if (ProgressDocsVerb.IsRead(c))
        {
            return ProgressDocsVerb.Run(c);
        }

        var words = new List<string>();
        var flags = new List<KeyValuePair<string, string>>();
        var argv = c.Argv.Skip(1).ToList();
        for (var i = 0; i < argv.Count; i++)
        {
            if (!argv[i].StartsWith("--", StringComparison.Ordinal))
            {
                words.Add(argv[i]);
                continue;
            }

            var key = argv[i][2..];
            if (!ValueFlags.Contains(key))
            {
                throw new RosterFailure($"progress item: unknown flag --{key}; {Usage}");
            }

            if (i + 1 >= argv.Count)
            {
                throw new RosterFailure($"progress item: --{key} needs a value");
            }

            flags.Add(new KeyValuePair<string, string>(key, argv[++i]));
        }

        if (words.Count < 3 || words[0] != "item")
        {
            throw new RosterFailure(Usage);
        }

        var sub = words[1];
        var stream = words[2];
        var team = TeamRecord(c);
        var known = !team.IsNull && team.Member("streams") is { IsMap: true } streams ? streams.AsObject().Keys.Order(StringComparer.Ordinal).ToList() : [];
        if (!known.Contains(stream, StringComparer.Ordinal))
        {
            throw new RosterFailure($"progress item {sub}: stream {stream} is not one of this team's streams ({(known.Count == 0 ? "none" : string.Join(", ", known))}); nothing written");
        }

        var by = HierarchyModule.OrchestratorAddress(c.ProgressTeam);
        var progress = c.Services.Hierarchy.Progress;
        if (sub == "import")
        {
            return Import(c, progress, stream, by, words, flags);
        }

        if (!Events.Contains(sub, StringComparer.Ordinal) || words.Count != 4)
        {
            throw new RosterFailure(Usage);
        }

        var item = words[3];
        string? Flag(string key) => flags.LastOrDefault(f => f.Key == key) is { Key: not null } hit ? hit.Value : null;
        var commits = flags.Where(f => f.Key == "commit").Select(f => f.Value).ToList();
        int? issue = null;
        if (Flag("issue") is { } issueText)
        {
            issue = int.TryParse(issueText, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
                ? number
                : throw new RosterFailure($"progress item {sub}: --issue {issueText} is not a positive whole number");
        }

        int? rank = null;
        if (Flag("rank") is { } rankText)
        {
            rank = int.TryParse(rankText, NumberStyles.None, CultureInfo.InvariantCulture, out var order)
                ? order
                : throw new RosterFailure($"progress item {sub}: --rank {rankText} is not a whole number, 0 or more");
        }

        if (sub == "done")
        {
            CheckCommits(c, team, stream, commits);
        }

        var input = new PlanInput(item, sub, Flag("title"), commits.Count > 0 ? commits : null, Flag("branch"), issue, Flag("note"), Rank: rank, Area: Flag("area"), Holder: Flag("holder"), Role: Flag("role"), Todo: Flag("todo"), Ref: Flag("ref"));
        var result = progress.RecordPlan(c.ProgressPool, c.ProgressTeam, stream, by, [input], c.Proved().SessionId);
        if (result.Off)
        {
            throw new RosterFailure($"progress item {sub}: progress is off for {c.ProgressTeam}; nothing written");
        }

        if (result.Refused is { } refused)
        {
            throw new RosterFailure($"progress item {sub}: {refused}; nothing written");
        }

        if (result.Warning is { } warning)
        {
            c.Warn($"progress item {sub}: the event was recorded, but {warning}");
        }

        if (input.Branch is { Length: > 0 } itemBranch)
        {
            RosterLedger.Train(c, "progress item", stream, $"{stream}/{item} {sub} on {itemBranch}", itemBranch);
        }

        var state = progress.ReadPlan(c.ProgressPool, c.ProgressTeam, stream)?.Items.FirstOrDefault(i => i.Item == item)?.State ?? "unknown";
        c.Stdout.Write($"progress: {stream}/{item} → {state}\n");
        return 0;
    }

    private static int Import(
        RosterContext c, IHierarchyProgress progress, string stream, string by, List<string> words, List<KeyValuePair<string, string>> flags)
    {
        if (words.Count != 3 || flags.Any(f => !ImportFlags.Contains(f.Key)))
        {
            throw new RosterFailure("usage: ah progress item import <stream> [--team T]");
        }

        var result = progress.ImportPlan(c.ProgressPool, c.ProgressTeam, stream, by, c.Proved().SessionId);
        if (result.Off)
        {
            throw new RosterFailure($"progress item import: progress is off for {c.ProgressTeam}; nothing written");
        }

        if (result.Refused is { } refused)
        {
            throw new RosterFailure($"progress item import: {refused}; nothing written");
        }

        if (result.Warning is { } warning)
        {
            c.Warn($"progress item import: the events were recorded, but {warning}");
        }

        c.Stdout.Write($"progress: {stream} → planned ({result.Items} item(s) imported)\n");
        foreach (var line in result.Unparsed)
        {
            c.Stdout.Write($"unparsed: {line}\n");
        }

        return 0;
    }

    /// <summary>The team record the verb acts on, found where the owner check finds it; null when there is none.</summary>
    private static JsonValue TeamRecord(RosterContext c)
    {
        var team = c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile);
        return team.IsNull && c.TeamHome != c.Dir ? c.Services.Roster.ReadTeamRecord(c.TeamHome, c.TeamFile) : team;
    }

    /// <summary>
    /// Refuses a <c>done</c> naming a commit that does not resolve. The commit is looked up in the work tree the stream records; where
    /// the command runs is asked only when the stream records none.
    /// </summary>
    private static void CheckCommits(RosterContext c, JsonValue team, string stream, List<string> commits)
    {
        if (commits.Count == 0)
        {
            return;
        }

        var runner = c.Services.Launch?.Processes
            ?? throw new RosterFailure("progress item done: commits cannot be checked here; nothing written");
        var trees = new List<string>();
        var checkedOn = new List<string>();
        if (!team.IsNull && team.Member("streams") is { IsMap: true } streams)
        {
            var own = streams.Member(stream);
            AddTree(own, stream);
            foreach (var other in streams.AsObject().Keys)
            {
                if (other != stream)
                {
                    AddTree(streams.Member(other), other);
                }
            }
        }

        if (trees.Count == 0)
        {
            trees.Add(c.Cwd);
            checkedOn.Add(c.Cwd);
        }

        foreach (var sha in commits)
        {
            if (!trees.Any(t => WatchModule.CommitExists(runner, t, sha)))
            {
                throw new RosterFailure($"progress item done: commit {sha} not found; nothing written (branches checked: {string.Join(", ", checkedOn)})");
            }
        }

        void AddTree(JsonValue? record, string name)
        {
            if (record?.Member("worktree") is { IsText: true } w && w.AsString().Length > 0 && !trees.Contains(w.AsString()))
            {
                trees.Add(w.AsString());
                checkedOn.Add(record.Value.Member("branch") is { IsText: true } b && b.AsString().Length > 0 ? b.AsString() : $"{name} ({w.AsString()})");
            }
        }
    }
}
