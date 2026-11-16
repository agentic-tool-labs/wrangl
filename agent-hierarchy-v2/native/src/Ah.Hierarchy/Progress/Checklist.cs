using System.Globalization;
using System.Text;

namespace Ah.Hierarchy;

/// <summary>One checklist line: a plan item with the stream it lives in (<c>_run</c> for a loose end).</summary>
/// <param name="Stream">The stream whose plan log holds the item.</param>
/// <param name="Item">The folded item.</param>
public sealed record ChecklistEntry(string Stream, PlanItem Item);

/// <summary>What waits on the user in one team (<see cref="HierarchyModule.ChecklistWaiting"/>): its open decisions and the items none of them names.</summary>
/// <param name="Decisions">The open decisions.</param>
/// <param name="Items">The items waiting on the user that no open decision names.</param>
public sealed record ChecklistWaitingList<TDecision, TItem>(IReadOnlyList<TDecision> Decisions, IReadOnlyList<TItem> Items)
{
    /// <summary>How many rows the list has; every count of what waits on the user is this.</summary>
    public int Count => Decisions.Count + Items.Count;
}

/// <summary>A member as the queue rules see it, as plain values.</summary>
/// <param name="Name">The member's name.</param>
/// <param name="Role">The member's role.</param>
/// <param name="Live">Whether the member is live.</param>
/// <param name="IdleSinceMs">When the member went idle, in epoch milliseconds; null when it is not idle.</param>
/// <param name="OpenDispatch">Whether a dispatch addressed to the member is open.</param>
/// <param name="BlockedOnJob">Whether the member waits on a long job.</param>
/// <param name="Dismissed">Whether the member was dismissed.</param>
/// <param name="SpawnedAtMs">When the member's row was written by a spawn or relaunch, in epoch milliseconds; null for a row written before the field existed.</param>
/// <param name="Stream">The stream the member's row names; null for a member on none, who can take any item.</param>
/// <param name="StreamClosed">Whether that stream is no longer open, so the member is not offered work.</param>
public sealed record QueueMember(string Name, string Role, bool Live, double? IdleSinceMs, bool OpenDispatch, bool BlockedOnJob, bool Dismissed, double? SpawnedAtMs = null, string? Stream = null, bool StreamClosed = false);

/// <summary>A dispatch to a role, as the queue rules see it.</summary>
/// <param name="Role">The role the dispatch went to.</param>
/// <param name="AtMs">When it was sent, in epoch milliseconds.</param>
public sealed record QueueDispatch(string Role, double AtMs);

/// <summary>One queued item with the members that could take it.</summary>
/// <param name="Entry">The queued item.</param>
/// <param name="Role">The role that takes it.</param>
/// <param name="QueuedSinceMs">When it became queued, in epoch milliseconds.</param>
/// <param name="Eligible">The members that could take it now, longest idle first.</param>
/// <param name="LastDispatchMs">When a dispatch to that role was last sent, or null.</param>
/// <param name="RoleLiveMembers">The members of that role that are live and not dismissed.</param>
/// <param name="LastRoleSpawnMs">The newest spawn time among the members of that role, live or not, dismissed excluded; null when none has one.</param>
public sealed record QueueEntry(ChecklistEntry Entry, string Role, double QueuedSinceMs, IReadOnlyList<string> Eligible, double? LastDispatchMs, int RoleLiveMembers = 0, double? LastRoleSpawnMs = null);

/// <summary>The pure rules over the checklist: how it is shown, which queued items an idle member could take, and which items wait on the Orchestrator.</summary>
internal static class ChecklistRules
{
    /// <summary>The default role that takes an item whose role was never set.</summary>
    internal const string DefaultRole = "implementor";

    /// <summary>The longest <c>show</c> output, in lines.</summary>
    internal const int MaxLines = 40;

    /// <summary>The display name of a stored state: <c>queued</c> for <c>next</c>, <c>dispatched</c> for <c>in_progress</c>, <c>waiting-orch</c> for <c>waiting_orch</c>.</summary>
    internal static string DisplayState(string state) => state switch
    {
        PlanStates.Next => "queued",
        PlanStates.InProgress => "dispatched",
        PlanStates.WaitingOrch => "waiting-orch",
        PlanStates.WaitingUser => "waiting-user",
        _ => state,
    };

    /// <summary>The display name of <paramref name="item"/>'s state for a person: <c>rework</c> for work sent back by a failing verdict or gate, <c>reported, waiting on the Orchestrator</c> for a DONE report, else <see cref="DisplayState(string)"/>.</summary>
    internal static string DisplayState(PlanItem item) => item switch
    {
        { State: PlanStates.InProgress, Rework: true } => "rework",
        { State: PlanStates.Reported } => "reported, waiting on the Orchestrator",
        _ => DisplayState(item.State),
    };

    /// <summary>Whether the item is open: not done and not dropped.</summary>
    internal static bool IsOpen(PlanItem item) => item.State is not (PlanStates.Done or PlanStates.Dropped);

    internal static string RoleOf(PlanItem item) => item.Role ?? DefaultRole;

    /// <summary>The instant the item last entered its current state (the row that moved it there), or 0 when unknown.</summary>
    internal static double QueuedMs(PlanItem item) =>
        item.StateSince is { } at && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when.ToUnixTimeMilliseconds() : UpdatedMs(item);

    /// <summary>The instant the item last changed, or 0 when unknown.</summary>
    internal static double UpdatedMs(PlanItem item) =>
        item.UpdatedAt is { } at && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when.ToUnixTimeMilliseconds() : 0;

    /// <summary>
    /// The queued items (state <c>next</c>) of <paramref name="entries"/> that have waited at least <paramref name="queuedMs"/>, each with the
    /// members of its role that are live, idle for at least <paramref name="idleMs"/>, have no open dispatch, are not waiting on a job and were
    /// not dismissed. An item is listed only when such a member exists and no dispatch went to its role within <paramref name="quietMs"/>.
    /// </summary>
    internal static IReadOnlyList<QueueEntry> Stalled(
        IReadOnlyList<ChecklistEntry> entries,
        IReadOnlyList<QueueMember> members,
        IReadOnlyList<QueueDispatch> dispatches,
        double nowMs,
        double queuedMs,
        double idleMs,
        double quietMs)
    {
        var found = new List<QueueEntry>();
        foreach (var entry in entries.Where(static e => e.Item.State == PlanStates.Next && !AnsweredWithNoWork(e.Item)))
        {
            var role = RoleOf(entry.Item);
            var since = QueuedMs(entry.Item);
            if (since <= 0 || nowMs - since < queuedMs)
            {
                continue;
            }

            var last = LastDispatchMs(dispatches, role);
            if (last is { } l && nowMs - l < quietMs)
            {
                continue;
            }

            var eligible = EligibleMembers(members, role, entry.Stream, nowMs, idleMs);
            if (eligible.Count > 0)
            {
                found.Add(new QueueEntry(entry, role, since, eligible, last, RoleLiveMembers(members, role), LastRoleSpawnMs(members, role)));
            }
        }

        return found;
    }

    /// <summary>
    /// Whether the item's last transition was the user's answer (its answer todo still stands) and no request was ever linked to it (no refs): an answered question,
    /// not queued work, so queue-stall does not offer it to a member (b528).
    /// </summary>
    private static bool AnsweredWithNoWork(PlanItem item) =>
        item.Answer is not null && item.Refs is not { Count: > 0 } && item.Todo is { } todo && todo.StartsWith(HierarchyModule.AnswerUserTodo, StringComparison.Ordinal);

    /// <summary>The members of <paramref name="role"/> that are live and not dismissed.</summary>
    private static int RoleLiveMembers(IReadOnlyList<QueueMember> members, string role) =>
        members.Count(m => m.Role == role && m.Live && !m.Dismissed);

    /// <summary>The newest spawn time among the members of <paramref name="role"/>, live or not and dismissed ones excluded; null when none has one.</summary>
    private static double? LastRoleSpawnMs(IReadOnlyList<QueueMember> members, string role) =>
        members.Where(m => m.Role == role && !m.Dismissed).Select(static m => m.SpawnedAtMs).Max();

    /// <summary>
    /// The queued items that have waited at least <paramref name="queuedMs"/> for a role with no live member while the team is under its
    /// session budget and no member of the role was spawned within <paramref name="recentSpawnMs"/>. <paramref name="budget"/> is the most live
    /// members the team may have; null means no limit. The team's live count is every member that is live.
    /// </summary>
    internal static IReadOnlyList<QueueEntry> Unstaffed(
        IReadOnlyList<ChecklistEntry> entries,
        IReadOnlyList<QueueMember> members,
        IReadOnlyList<QueueDispatch> dispatches,
        double nowMs,
        double queuedMs,
        int? budget,
        double recentSpawnMs)
    {
        if (budget is { } limit && TeamLive(members) >= limit)
        {
            return [];
        }

        return [.. Queued(entries, members, dispatches, nowMs).Where(q =>
            nowMs - q.QueuedSinceMs >= queuedMs
            && q.RoleLiveMembers == 0
            && (q.LastRoleSpawnMs is not { } spawned || nowMs - spawned >= recentSpawnMs))];
    }

    /// <summary>How many of <paramref name="members"/> are live.</summary>
    internal static int TeamLive(IReadOnlyList<QueueMember> members) => members.Count(static m => m.Live);

    /// <summary>The members of <paramref name="role"/> that are live, idle for at least <paramref name="idleMs"/>, have no open dispatch, are not waiting on a job and were not dismissed, longest idle first.</summary>
    private static List<string> EligibleMembers(IReadOnlyList<QueueMember> members, string role, string stream, double nowMs, double idleMs) =>
        [.. members
            .Where(m => m.Role == role && m.Live && !m.OpenDispatch && !m.BlockedOnJob && !m.Dismissed && !m.StreamClosed
                && (stream == ProgressFormat.RunStream || string.IsNullOrEmpty(m.Stream) || m.Stream == stream)
                && m.IdleSinceMs is { } idle && nowMs - idle >= idleMs)
            .OrderBy(static m => m.IdleSinceMs)
            .Select(static m => m.Name)];

    private static double? LastDispatchMs(IReadOnlyList<QueueDispatch> dispatches, string role) =>
        dispatches.Where(d => d.Role == role).Select(static d => (double?)d.AtMs).Max();

    /// <summary>Every queued item with a known queue time, whether or not it has stalled, each with the members of its role that are idle now and the last dispatch to that role.</summary>
    internal static IReadOnlyList<QueueEntry> Queued(IReadOnlyList<ChecklistEntry> entries, IReadOnlyList<QueueMember> members, IReadOnlyList<QueueDispatch> dispatches, double nowMs)
    {
        var found = new List<QueueEntry>();
        foreach (var entry in entries.Where(static e => e.Item.State == PlanStates.Next))
        {
            var role = RoleOf(entry.Item);
            var since = QueuedMs(entry.Item);
            if (since > 0)
            {
                found.Add(new QueueEntry(entry, role, since, EligibleMembers(members, role, entry.Stream, nowMs, 0), LastDispatchMs(dispatches, role), RoleLiveMembers(members, role), LastRoleSpawnMs(members, role)));
            }
        }

        return found;
    }

    /// <summary>The items that have been in <c>waiting_orch</c> for at least <paramref name="waitedMs"/>.</summary>
    internal static IReadOnlyList<ChecklistEntry> WaitingOnOrchestrator(IReadOnlyList<ChecklistEntry> entries, double nowMs, double waitedMs) =>
        [.. entries.Where(e => e.Item.State == PlanStates.WaitingOrch && QueuedMs(e.Item) > 0 && nowMs - QueuedMs(e.Item) >= waitedMs)];

    /// <summary>
    /// The lines <c>ah checklist show</c> prints: open items ordered <c>waiting-orch</c>, then queued items that are stalled, then
    /// <c>blocked</c>, then dispatched, then the remaining queued, each as id, state, owner, what, next, links and age, at most
    /// <see cref="MaxLines"/> lines with a count of the rest.
    /// </summary>
    internal static IReadOnlyList<string> Show(IReadOnlyList<ChecklistEntry> entries, IReadOnlySet<string> stalledIds, double nowMs, bool all)
    {
        var shown = Shown(entries, stalledIds, all);
        var lines = new List<string>();
        foreach (var entry in shown.Take(MaxLines))
        {
            lines.Add(Line(entry, nowMs));
        }

        if (shown.Count > MaxLines)
        {
            lines.Add($"… {shown.Count - MaxLines} more: run `ah checklist show --all`");
        }

        if (lines.Count == 0)
        {
            lines.Add("checklist: nothing outstanding");
        }

        return lines;
    }

    /// <summary>The items <c>show</c> lists, in its order: every item with <paramref name="all"/>, else the open ones.</summary>
    internal static List<ChecklistEntry> Shown(IReadOnlyList<ChecklistEntry> entries, IReadOnlySet<string> stalledIds, bool all) =>
        [.. entries.Where(e => all || IsOpen(e.Item)).OrderBy(e => Rank(e, stalledIds))
            .ThenBy(static e => e.Item.State == PlanStates.WaitingUser ? ProgressFormat.ClassRank(e.Item.Class) : 0).ThenBy(static e => UpdatedMs(e.Item))];

    /// <summary>How long an <c>add</c> of an identical title returns the item it already made rather than a new one.</summary>
    internal static readonly TimeSpan AddRepeatWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The item in <paramref name="stream"/> that a repeated <c>add</c> of <paramref name="what"/> means: not dropped, the same title once
    /// whitespace is collapsed (case-sensitive), and added within <see cref="AddRepeatWindow"/> of <paramref name="nowMs"/>. Null when none.
    /// </summary>
    internal static ChecklistEntry? RecentTwin(IReadOnlyList<ChecklistEntry> entries, string stream, string what, double nowMs)
    {
        var title = CollapseSpace(what);
        return entries.FirstOrDefault(e =>
            e.Stream == stream
            && e.Item.State != PlanStates.Dropped
            && CollapseSpace(e.Item.Title) == title
            && DateTimeOffset.TryParse(e.Item.AddedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var added)
            && nowMs - added.ToUnixTimeMilliseconds() >= 0
            && nowMs - added.ToUnixTimeMilliseconds() < AddRepeatWindow.TotalMilliseconds);
    }

    private static string CollapseSpace(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static int Rank(ChecklistEntry e, IReadOnlySet<string> stalledIds) => e.Item.State switch
    {
        PlanStates.WaitingOrch => 0,
        PlanStates.Next when stalledIds.Contains(e.Item.Item) => 1,
        PlanStates.Blocked => 2,
        PlanStates.InProgress => 3,
        PlanStates.Next => 4,
        PlanStates.NotStarted => 5,
        PlanStates.WaitingUser => 6,
        _ => 7,
    };

    private static string Line(ChecklistEntry e, double nowMs)
    {
        var item = e.Item;
        var sb = new StringBuilder();
        sb.Append(item.Item).Append(' ').Append(DisplayState(item));
        if (item.State == PlanStates.WaitingUser)
        {
            sb.Append(" class=").Append(HierarchyModule.ChecklistClassOf(item));
        }

        sb.Append(" owner=").Append(item.Holder ?? "-");
        sb.Append(" | ").Append(item.Title);
        if (item.Todo is { Length: > 0 } todo)
        {
            sb.Append(" | next: ").Append(todo);
        }

        var links = new List<string>(item.Refs ?? []);
        if (item.Issue is { } issue)
        {
            links.Add("#" + issue.ToString(CultureInfo.InvariantCulture));
        }

        links.AddRange(item.Commits);
        if (links.Count > 0)
        {
            sb.Append(" | ").Append(string.Join(' ', links));
        }

        var updated = UpdatedMs(item);
        if (updated > 0)
        {
            sb.Append(" | ").Append(Age(nowMs - updated));
        }

        return sb.ToString();
    }

    private static string Age(double ms)
    {
        var minutes = (long)Math.Max(0, ms / 60_000);
        return minutes < 60 ? $"{minutes}m" : minutes < 1440 ? $"{minutes / 60}h" : $"{minutes / 1440}d";
    }
}
