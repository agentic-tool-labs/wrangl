using Ah.Core.Json;

namespace Ah.Roster;

/// <summary>
/// Claude Code's isolation of a member's session in a worktree, as the worktree gate records it on the member's team-file row:
/// the tree, and the pane the session was running in. A member launched anew gets a new pane, so a record made for another pane
/// no longer describes it.
/// </summary>
internal static class MemberIsolation
{
    /// <summary>The row key naming the tree.</summary>
    public const string TreeKey = "isolated_worktree";

    /// <summary>The row key naming the pane the isolated session ran in.</summary>
    public const string PaneKey = "isolated_pane";

    /// <summary>The pane the member's row records now (null when it records none), as the isolation record's pane is compared with.</summary>
    public static string? PaneOf(JsonValue memberRow) =>
        memberRow.Member("transport_id") is { IsText: true } pane ? pane.AsString() : null;

    /// <summary>The tree the member's session is isolated in, or null when its row records none or records it for another pane.</summary>
    public static string? TreeOf(JsonValue memberRow)
    {
        if (!memberRow.IsMap || memberRow.Member(TreeKey) is not { IsText: true } tree)
        {
            return null;
        }

        var recordedPane = memberRow.Member(PaneKey) is { IsText: true } pane ? pane.AsString() : null;
        return recordedPane == PaneOf(memberRow) ? tree.AsString() : null;
    }
}
