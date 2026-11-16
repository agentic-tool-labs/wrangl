using Ah.Hierarchy;

namespace Ah.Commands;

/// <summary>Records the team changes a roster verb has committed in the team's memory ledger. A dry run records nothing; a failure never fails the verb.</summary>
internal static class RosterLedger
{
    /// <summary>A member spawned, retired or moved: <paramref name="what"/> is <c>spawned</c>, <c>retired</c> or <c>moved</c>.</summary>
    internal static void Member(RosterContext c, string verb, string what, string? name, string? role) =>
        Append(c, new LedgerEvent("member", Text: $"{what} {name ?? "?"} ({role ?? "?"})", SourceType: "verb", SourceVerb: verb));

    /// <summary>A stream opened or finished, or its branch or base changed.</summary>
    internal static void Train(RosterContext c, string verb, string? stream, string text, string? branch = null, string? baseRef = null, string? verdict = null, string? sha = null) =>
        Append(c, new LedgerEvent("train", Stream: stream, Sha: sha, Ref: branch, Base: baseRef, Verdict: verdict, Text: text, SourceType: "verb", SourceVerb: verb));

    private static void Append(RosterContext c, LedgerEvent ev)
    {
        if (c.Opts.IsTrue("dry-run"))
        {
            return;
        }

        try
        {
            c.Services.Hierarchy.Progress.AppendLedger(c.ProgressPool, c.ProgressTeam, ev);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // the verb has committed; recording it is best-effort
        }
    }
}
