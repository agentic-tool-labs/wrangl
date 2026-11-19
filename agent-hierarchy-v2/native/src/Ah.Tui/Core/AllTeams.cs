namespace Ah.Tui.Core;

/// <summary>
/// The rows the screens share. The teams themselves come from the read model (<see cref="TeamSlices"/>). Every label is plain words: a task with no human title reads
/// as its slug in words, or as "a task" when the slug is an id, and an agent
/// is named by its role, with its member name added when the role repeats in the team.
/// </summary>
internal static class AllTeams
{
    /// <summary>One team's row. <c>Agents</c> is -1 when the team's member list has not arrived; <c>Parent</c> is the id of the
    /// team a lane belongs to; <c>Updated</c> says how long ago the team last changed, when the daemon serves it.</summary>
    internal sealed record TeamRow(string Name, StatusLanguage.Status Status, string? Since, int Up, int Agents, int InFlight, int NeedsYou, string? Orch, string? Path = null)
    {
        internal string? Parent { get; init; }

        internal string? Updated { get; init; }

        /// <summary>How many of the team's agents the daemon can neither prove up nor gone; 0 when it does not serve the count.</summary>
        internal int Unknown { get; init; }

        /// <summary>Whether the daemon can neither prove the team's lead up nor gone.</summary>
        internal bool LeadUnsure { get; init; }

        /// <summary>Whether the daemon serves the team as ended; Home hides it until asked.</summary>
        internal bool Ended { get; init; }

        /// <summary>Whether the team is past its served <c>quiet_at</c>; Home folds it until asked.</summary>
        internal bool Quiet { get; init; }

        internal int ItemsDone { get; init; }

        internal int ItemsTotal { get; init; }

        /// <summary>False while the team's summary has not arrived, so its zeros say nothing.</summary>
        internal bool SummaryRead { get; init; } = true;

        /// <summary>False while the daemon has not read the team's plan, so its counts say nothing.</summary>
        internal bool PlanLoaded { get; init; } = true;

        /// <summary>How many of the team's members are working now.</summary>
        internal int Working { get; init; }

        /// <summary>How many of the team's members are blocked now.</summary>
        internal int Blocked { get; init; }
    }

    internal sealed record NeedRow(StatusLanguage.Status Status, string Team, string Agent, string What, string Since, DateTimeOffset At, string? TeamPath = null, string? AgentId = null);

    internal sealed record FlightRow(StatusLanguage.Status Status, string Team, string Agent, string What, string Time, DateTimeOffset At, string? TeamPath = null, string? AgentId = null);

    internal sealed record Model(IReadOnlyList<TeamRow> Teams, IReadOnlyList<NeedRow> Needs, IReadOnlyList<FlightRow> Flight)
    {
        internal int Up => Teams.Sum(t => t.Up);

        internal int Agents => Teams.Sum(t => t.Agents);
    }

    /// <summary>One agent of the Team screen. <c>Short</c> is its member name without the team's name in front, which a screen
    /// short of room shows in place of <c>Name</c>.</summary>
    internal sealed record AgentRow(string Id, string Name, StatusLanguage.Status Status, string? Since, string Working, string? Short = null, string? Liveness = null)
    {
        /// <summary>The member's role, which picks its colour.</summary>
        internal string? Role { get; init; }

        /// <summary>The item title the daemon serves for the member.</summary>
        internal string? Item { get; init; }

        /// <summary>The liveness word to show: <c>live</c>, the word for another served liveness, or "–" where none is served.</summary>
        internal string Live { get; init; } = "–";

        internal string? Tool { get; init; }

        internal string? StateBranch { get; init; }
    }

    internal sealed record TeamDetail(TeamRow Row, IReadOnlyList<AgentRow> Agents, IReadOnlyList<FlightRow> Flight);

    /// <summary>A task's plain name: its title when the producer wrote one, else its slug in words, else "a task" for a slug that is an id.</summary>
    internal static string Title(string? title, string? slug)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title.Trim();
        }

        if (string.IsNullOrWhiteSpace(slug) || LooksLikeId(slug))
        {
            return "a task";
        }

        return slug.Replace('-', ' ').Replace('_', ' ').Trim();
    }

    private static bool LooksLikeId(string s)
    {
        var digits = s.Count(char.IsAsciiDigit);
        return digits >= 6 || (s.Length >= 12 && !s.Contains('-', StringComparison.Ordinal) && s.All(char.IsAsciiHexDigit));
    }

    /// <summary>A duration as the screens show it: <c>1m</c> at the least, then minutes, hours under two days, then days.</summary>
    internal static string Age(DateTimeOffset now, DateTimeOffset? at)
    {
        if (at is null)
        {
            return "–";
        }

        var m = Math.Max(1, (int)Math.Floor((now - at.Value).TotalMinutes));
        return m < 60 ? $"{m}m" : m < 48 * 60 ? $"{m / 60}h" : $"{m / (24 * 60)}d";
    }
}
