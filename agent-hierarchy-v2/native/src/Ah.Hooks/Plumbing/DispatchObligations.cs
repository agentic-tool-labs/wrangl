using Ah.Core.Json;

namespace Ah.Hooks;

/// <summary>The dispatch rows a hook may raise an obligation from.</summary>
internal static class DispatchObligations
{
    /// <summary>
    /// The session's dispatch rows minus those of withdrawn requests (a request with a withdrawn brief and no live one), the one read every
    /// hook that blocks a stop, nudges or prompts goes through. Rows are matched by request id, so a row that records no request path is
    /// dropped too. Without the mailbox services every row is returned.
    /// </summary>
    internal static IReadOnlyList<JsonValue> Live(GateServices s, string sessionId)
    {
        var rows = s.Hierarchy.Peers.LatestDispatchRows(sessionId).ToList();
        if (s.Advisory?.Mailbox is not { } mailbox || rows.Count == 0)
        {
            return rows;
        }

        var withdrawn = mailbox.WithdrawnRequestFiles().Select(file => file.Split("--")[0]).ToHashSet(StringComparer.Ordinal);
        return withdrawn.Count == 0
            ? rows
            : [.. rows.Where(row => !(row.Member("request_id") is { IsText: true } id && withdrawn.Contains(id.AsString())))];
    }
}
