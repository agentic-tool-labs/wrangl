using System.Globalization;

namespace Ah.Commands;

/// <summary>
/// The <c>stream-empty</c> doctor row: a stream that is open, was opened more than <see cref="Grace"/> ago and has no member in it.
/// Such a stream holds a worktree and branch (and, once a member left, maybe a tab) for nothing. Never red.
/// </summary>
internal static class StreamEmptyDoctor
{
    internal static readonly TimeSpan Grace = TimeSpan.FromMinutes(30);

    internal static (string Status, string Detail)? Row(RosterContext c)
    {
        var team = c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile);
        if (team.Member("streams") is not { IsMap: true } streams)
        {
            return null;
        }

        var joined = team.Member("members") is { IsArray: true } list
            ? list.AsArray().Where(m => m.IsMap).Select(m => MemberPlanning.Text(m.AsObject(), "stream")).Where(s => s is not null).ToHashSet(StringComparer.Ordinal)
            : [];
        var now = c.Services.Clock.UtcNow;
        var empty = new List<string>();
        foreach (var name in streams.AsObject().Keys.Order(StringComparer.Ordinal))
        {
            var record = streams.AsObject()[name];
            if (!record.IsMap || MemberPlanning.Text(record.AsObject(), "state") != "open" || joined.Contains(name))
            {
                continue;
            }

            if ((MemberPlanning.Text(record.AsObject(), "opened_at") ?? MemberPlanning.Text(record.AsObject(), "opened")) is { } opened
                && DateTimeOffset.TryParse(opened, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                && now - at > Grace)
            {
                empty.Add($"{name} (`ah roster stream-done {name}{StreamSupport.TeamFlagText(c)}`)");
            }
        }

        return empty.Count == 0
            ? ("ok", "no open stream is empty")
            : ("warn", $"open for over {(int)Grace.TotalMinutes} min with no member: {string.Join(", ", empty)}");
    }
}
