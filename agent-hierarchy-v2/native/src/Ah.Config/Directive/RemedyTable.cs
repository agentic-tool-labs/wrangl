namespace Ah.Config;

/// <summary>One refusal class and, per actor, the command that gets past it. An empty cell means that actor has no remedy for it.</summary>
/// <param name="Code">The refusal class.</param>
/// <param name="Unstick">What the lane lead runs to get the member going again.</param>
/// <param name="HandOff">How the lead hands the work to another member.</param>
/// <param name="DoItItself">What the lead does itself.</param>
/// <param name="Escalate">What only the top Orchestrator can do.</param>
internal sealed record RemedyRow(string Code, string Unstick, string HandOff, string DoItItself, string Escalate);

/// <summary>
/// The one table of refusal remedies: what a lane lead or the top Orchestrator runs for each class of refusal a member can hit, so a
/// refusal text, a launch warning and a stuck report all say the same thing. The launch causes below it are the reasons a member
/// launches without its launch-only rules, each with its one-line remedy.
/// </summary>
internal static class RemedyTable
{
    public const string NoConsent = "no-consent";

    public const string NoPluginRoot = "no-plugin-root";

    public const string RoleNotText = "role-not-text";

    public const string RootMismatch = "root-mismatch";

    public const string WriteFailed = "write-failed";

    public const string Unknown = "unknown";

    public static readonly IReadOnlyList<RemedyRow> Rows =
    [
        new("no-consent / launch-file", string.Empty, "fresh member after `ah allow on`", string.Empty, "the user runs `ah allow on` in their own terminal; then relaunch the member with `ah roster resume`"),
        new("git-cotenant", "`dismiss` the co-tenant if done", "`stream-open` + `spawn-one --stream`", "merge/commit in place", "grant a worktree"),
        new("not-child-team", "use own/opened stream", string.Empty, string.Empty, "parent-owned spawn"),
        new("close-member-ask", string.Empty, string.Empty, "`dismiss` (untrack)", "`close-approve`"),
        new("lane-allow", string.Empty, "member whose tree covers the path", string.Empty, "`allow --team <child>`"),
        new("PG-SO", string.Empty, "member merges", "local merge", "push / protected merge"),
        new("progress-*", "`progress item done …`", string.Empty, string.Empty, string.Empty),
        new("not-team-owner", "per `proof_failed`", string.Empty, string.Empty, "`checkin` / `adopt`"),
        new("team-mismatch", "`ah roster recover --member <m>`", "`ah roster resume`", string.Empty, "`ah roster recover --team <t>`"),
    ];

    private static readonly Dictionary<string, string> LaunchRemedies = new(StringComparer.Ordinal)
    {
        [NoConsent] = "member launch rules were turned off (`ah allow off`); the user runs `ah allow on` in their own terminal, then relaunch the member with `ah roster resume`",
        [NoPluginRoot] = "start the member from a session that has the Wrangl plugin loaded, then relaunch it with `ah roster resume`",
        [RoleNotText] = "give the member's roster row a role name, then relaunch it with `ah roster resume`",
        [RootMismatch] = "relaunch the member from the tree its roster row records, or move it with `ah roster move`",
        [WriteFailed] = "make the team's launch directory writable (`ah roster doctor` names it), then relaunch the member with `ah roster resume`",
        [Unknown] = "relaunch the member with `ah roster resume`; if it repeats, report it to the Orchestrator",
    };

    /// <summary>The one-line remedy for a launch cause; an unlisted cause gets the <see cref="Unknown"/> remedy.</summary>
    public static string LaunchRemedy(string cause) => LaunchRemedies.TryGetValue(cause, out var remedy) ? remedy : LaunchRemedies[Unknown];

    /// <summary>Whether only the top Orchestrator can act on a launch cause (it alone may ask the user).</summary>
    public static bool NeedsTopOrchestrator(string cause) => cause == NoConsent;
}
