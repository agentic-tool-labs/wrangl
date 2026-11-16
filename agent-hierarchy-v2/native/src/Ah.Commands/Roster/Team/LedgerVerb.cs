using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Commands;

/// <summary>
/// <c>ledger ship &lt;sha&gt; [--branch &lt;b&gt;]</c>: records a ship that ran outside a Claude Bash call (a release script) in the team's ledger, the same row
/// the PostToolUse hook writes for a <c>git push</c>. It closes nothing: a release ref moving does (spec ah-ui-focus §4).
/// </summary>
internal static class LedgerVerb
{
    private const string Usage = "usage: ah ledger ship <sha> [--branch <b>] [--team T] [--cwd <repo>]";

    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "cwd", "team", "orchestrator-pid", "branch" };

    internal static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure($"ledger: unrecognized flag --{key}; {Usage}");
            }
        }

        var words = c.Opts.Positionals.Where(static p => p != "ledger").ToList();
        if (words is not ["ship", var given] || given.Length is < 7 or > 40 || !given.All(static ch => ch is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
        {
            throw new RosterFailure(Usage);
        }

        var sha = ReleaseGate.Git(c, c.Cwd, "rev-parse", "--verify", "-q", given + "^{commit}") is { Ok: true } full && full.Stdout.Trim() is { Length: > 0 } text
            ? text
            : throw new RosterFailure($"ledger ship: commit {given} not found in {c.Cwd}; nothing written");
        var branch = c.Opts.Text("branch");
        var ev = new LedgerEvent(
            "ship", Sha: sha, Ref: branch, Text: branch is null ? $"shipped {sha[..8]}" : $"pushed {branch}", SourceType: "verb", SourceVerb: HierarchyModule.LedgerShipVerb, Confirmed: true);
        var appended = c.Services.Hierarchy.Progress.AppendLedger(c.ProgressPool, c.ProgressTeam, ev);
        if (appended.Error is { } error)
        {
            throw new RosterFailure($"ledger ship: {error}; nothing written");
        }

        var json = new JsonObject { ["kind"] = "ship", ["sha"] = sha, ["ref"] = branch is null ? JsonValue.Null : branch, ["team"] = c.ProgressTeam, ["appended"] = appended.Appended };
        if (Timing(c, sha) is { } timing)
        {
            json["timing"] = timing;
        }

        c.Out(json);
        return 0;
    }

    /// <summary>The ship's batch as one line (spec ah-stage-timing §4), from the same fold as <c>roster timing</c>; null when the fold fails or has no batch for <paramref name="sha"/>.</summary>
    private static string? Timing(RosterContext c, string sha)
    {
        try
        {
            var progress = c.Services.Hierarchy.Progress;
            var logs = HierarchyModule.ChecklistEntries(progress, c.ProgressPool, c.ProgressTeam)
                .Select(static e => e.Stream).Distinct(StringComparer.Ordinal).Select(s => ChecklistVerb.PlanLog(c, s)).ToList();
            var report = HierarchyModule.StageTiming(logs, progress.ReadLedger(c.ProgressPool, c.ProgressTeam).ToList(), id => SlugOf(c, id), c.Services.Clock.UtcNow.ToUnixTimeMilliseconds());
            if (report.Batches.LastOrDefault(b => b.Sha == sha) is not { } b)
            {
                return null;
            }

            var line = $"batch {b.Items.Count} items, {TimingVerb.Dur(b.WallMs)}: {TimingVerb.Stages(b.StageMs)}";
            return b.Critical is { } gate ? $"{line}; gated by {gate.Group} in {gate.Stage}; ready {TimingVerb.Dur(gate.ShipWaitMs)} before ship" : line;
        }
        catch (Exception)
        {
            // ponytail: any fold failure drops the key so the ship still succeeds (spec §4); the cause is not reported
            return null;
        }
    }

    /// <summary>The slug in the frontmatter of request <paramref name="id"/> in the team's pool, or null.</summary>
    internal static string? SlugOf(RosterContext c, string id) =>
        c.Services.Hierarchy.Messages.FindByIdAndType(c.ProgressPool, id, "request").Member("meta")?.Member("slug") is { IsText: true } slug ? slug.AsString() : null;
}
