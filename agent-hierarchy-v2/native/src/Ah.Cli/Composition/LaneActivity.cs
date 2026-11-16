using System.Globalization;
using Ah.Config;
using Ah.Core.Json;

namespace Ah.Cli.Composition;

/// <summary>b376: what a lane's child team does for its lead counts as the lead speaking, so its silence alarms stay quiet while the child works.</summary>
internal static class LaneActivity
{
    /// <summary>Whether <paramref name="fm"/> is a request the lane's lead sent into its child team: from the child's orchestrating role, or naming <paramref name="lead"/> as its sender.</summary>
    internal static bool FromLead(JsonObject fm, string? lead) =>
        (fm.Get("from") is { IsText: true } from && from.AsString() is "orchestrator" or "sub-orchestrator")
        || (lead is { Length: > 0 } && fm.Get("from_name") is { IsText: true } name && name.AsString() == lead);

    /// <summary>
    /// When the child team <paramref name="child"/> last showed work: <paramref name="nowMs"/> while a request its lead sent is open and before its eta,
    /// else the latest commit in the work tree of a stream of the child that is not done; null when neither shows. <paramref name="commitOf"/>,
    /// when given, answers a work tree's latest commit from the read it is handed (b459 step 19: the watch tick runs git once per work tree).
    /// </summary>
    internal static double? LatestMs(ServiceGraph g, string hdir, string child, JsonValue childRecord, double nowMs, Func<string, Func<double?>, double?>? commitOf = null)
    {
        var source = new DaemonServices.OpenExchangeSource(g);
        foreach (var e in source.OpenExchanges(hdir, child, allTeams: false))
        {
            if (source.FrontMatter(e.RequestPath) is { } fm && FromLead(fm, null) && fm.Get("created") is { IsText: true } created
                && Ah.Core.Time.Timestamps.TryParseMs(created.AsString(), out var createdMs)
                && nowMs < createdMs + (g.Hierarchy.Messages.ThresholdFor(g.Hierarchy.Messages.EtaOf(fm.Get("eta") ?? JsonValue.Null)) * 1000))
            {
                return nowMs;
            }
        }

        if (childRecord.Member("streams") is not { IsMap: true } streams)
        {
            return null;
        }

        double? latest = null;
        var git = ConfigModule.CreateConventions(g.Runner, g.Io.Files, () => g.Environment.HomeDirectory);
        foreach (var ws in streams.AsObject().Keys)
        {
            var stream = streams.AsObject()[ws];
            if (stream.Member("state") is { IsText: true } state && state.AsString() == "done" || stream.Member("worktree") is not { IsText: true } tree)
            {
                continue;
            }

            // b459 step 22: a missing tree reads as a failed git log does, without spawning git; checked on every read, so a recreated tree is seen next tick
            double? Read() =>
                g.Io.Files.Exists(tree.AsString())
                && git.TryGit(tree.AsString(), ["log", "-1", "--format=%ct"])?.Trim() is { Length: > 0 } text
                && double.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ? seconds * 1000 : null;
            if ((commitOf is null ? Read() : commitOf(tree.AsString(), Read)) is { } ms && (latest is null || ms > latest))
            {
                latest = ms;
            }
        }

        return latest;
    }
}
