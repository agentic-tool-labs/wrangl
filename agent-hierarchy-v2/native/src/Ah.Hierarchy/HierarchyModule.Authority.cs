using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>The grant of a team's open run anchor as it stands now: <see cref="Mode"/> (<c>none</c> when there is no single open anchor), the anchor's id and its message path.</summary>
public sealed record AuthorityRead(string Mode, JsonValue Run, JsonValue Anchor);

public static partial class HierarchyModule
{
    /// <summary>
    /// The run's grant, read live from the team's single open run anchor: the one resolver both <c>whoami</c> and the decision verbs
    /// use. No open anchor, several of them, or a line that is neither value all give <c>none</c>. A <c>surrogate</c> grant holds only
    /// while <paramref name="orchestratorSession"/>'s recorded Authority answer confirms it, read from the asks of the anchor's pool
    /// and of <paramref name="dir"/> joined.
    /// </summary>
    public static AuthorityRead ReadAuthority(HierarchyServices hierarchy, IFileReader files, string dir, string? team, string? orchestratorSession)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);
        ArgumentNullException.ThrowIfNull(files);
        var none = new AuthorityRead("none", JsonValue.Null, JsonValue.Null);
        var anchor = hierarchy.Decisions.OpenRunAnchor(dir, team is null ? JsonValue.Null : team);
        if (anchor.Get("id") is not { IsText: true } id)
        {
            return none;
        }

        // the anchor is open, so it is in the live scope
        foreach (var exchange in hierarchy.Messages.ListLiveExchangesAllTeams(dir))
        {
            if (exchange.Member("id") is not { IsText: true } have || have.AsString() != id.AsString()
                || exchange.Member("request")?.Member("path") is not { IsText: true } path)
            {
                continue;
            }

            var anchorText = hierarchy.Core.ReadMsgFile(path.AsString()).Member("text") is { IsText: true } stored ? stored.AsString() : string.Empty;
            // an asks store that cannot be read holds no recorded answer for this read: surrogate reads as user (§6c G5)
            var mode = AsksOf(hierarchy, files, dir, path.AsString()) is { } asks
                ? EffectiveAuthority(anchorText, asks, orchestratorSession, hierarchy.Decisions.LastAnchorCloseTime(dir, team ?? TeamOf(anchorText), CreatedOf(anchorText)))
                : EffectiveAuthority(anchorText, string.Empty, orchestratorSession, null);
            return new AuthorityRead(mode ?? "none", id, path);
        }

        return none;
    }

    /// <summary>
    /// The asks logs of the anchor's pool and of this directory, with the parked-question stores beside them, joined, each read once;
    /// null when any one of them cannot be read (§6c G5), so the caller decides as if nothing were recorded.
    /// </summary>
    private static string? AsksOf(HierarchyServices hierarchy, IFileReader files, string dir, string anchorPath)
    {
        var asks = new[] { Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(anchorPath)) ?? string.Empty, "asks.jsonl"), hierarchy.Decisions.AsksPath(dir) }
            .Distinct(StringComparer.Ordinal).ToList();
        var logs = asks.Concat(asks.Select(a => Path.Combine(Path.GetDirectoryName(a) ?? string.Empty, ParkedQuestions.FileName)));
        var text = new List<string>();
        foreach (var log in logs)
        {
            if (!files.ReadAskLog(log).TryLines(out var lines))
            {
                return null;
            }

            if (lines.Count > 0)
            {
                text.Add(string.Join('\n', lines));
            }
        }

        return string.Join('\n', text);
    }
}
