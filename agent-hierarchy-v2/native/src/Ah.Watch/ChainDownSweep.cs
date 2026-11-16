using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Roster;

namespace Ah.Watch;

/// <summary>
/// The ahd pass over live member registrations: a member whose chain of command is proven down for the whole grace, and that has no
/// active <c>chain-down</c> row, gets one row and one <c>wind-down</c> event mail, and its team's Orchestrator box a
/// <c>member-wound-down</c> note. Only <see cref="ChainKind.Down"/> winds a member down; Unknown never does. Nothing is killed,
/// closed, dismissed, briefed or resumed.
/// </summary>
internal sealed class ChainDownSweep(WatchDependencies dependencies)
{
    /// <summary>How long a chain must stay Down, as seen by this daemon, before a member is told to wind down.</summary>
    internal const double GraceMs = RosterModule.ChainDownGraceMs;

    private readonly Dictionary<string, double> _firstDown = new(StringComparer.Ordinal);

    internal void Run(double now)
    {
        var host = dependencies.Host;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var peers = host.PeerRecords().Select(static r => (JsonValue)r).ToList();
        foreach (var reg in host.LiveRegistrations())
        {
            var box = reg.Boxes.FirstOrDefault(static b => b.Address.Contains('@', StringComparison.Ordinal) && !b.Address.StartsWith("orchestrator@", StringComparison.Ordinal));
            if (reg.SessionId.Length == 0 || box is null)
            {
                continue;
            }

            _ = seen.Add(reg.SessionId);
            try
            {
                Evaluate(reg, box, now, peers);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                dependencies.Log?.Invoke($"chain-down sweep failed for {reg.SessionId}: {e}");
            }
        }

        foreach (var sid in _firstDown.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _ = _firstDown.Remove(sid);
        }
    }

    private void Evaluate(WatchRegistration reg, WatchBox box, double now, IReadOnlyList<JsonValue> peers)
    {
        var host = dependencies.Host;
        var chain = host.ChainOf(reg);
        if (chain.Kind != ChainKind.Down)
        {
            _ = _firstDown.Remove(reg.SessionId);

            // Only a chain proven whole clears the row; one that cannot be judged leaves the member winding down.
            if (chain.Kind == ChainKind.Up && RosterModule.ChainDownActive(peers, reg.SessionId))
            {
                host.AppendPeerRows([new PeerAppend(new JsonObject
                {
                    ["type"] = "chain-down",
                    ["state"] = "cleared",
                    ["session_id"] = reg.SessionId,
                    ["at"] = Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)now)),
                })]);
            }

            return;
        }

        if (!_firstDown.TryGetValue(reg.SessionId, out var first))
        {
            _firstDown[reg.SessionId] = first = now;
        }

        if (now - first < GraceMs || RosterModule.ChainDownActive(peers, reg.SessionId))
        {
            return;
        }

        var at = Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)now));
        var split = box.Address.IndexOf('@', StringComparison.Ordinal);
        var member = box.Address[..split];
        var team = box.Address[(split + 1)..];
        var link = chain.Link ?? team;
        var orchestrator = "orchestrator@" + team;
        var cwd = host.SessionCwd(reg.SessionId) ?? string.Empty;
        var text = host.WindDownText(link, chain.Why, orchestrator);

        // The row silences every nudge, so it is written only once the member has been told; a failed mail is retried on the next tick
        // under the same due_slot, which the mail dedupes on.
        try
        {
            host.Mail(new WatchMail(box.Address, reg.HierarchyDir, cwd, "wind-down", text, DataOf(link, chain.Why, first, "chain-down:" + reg.SessionId), reg.SessionId));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            dependencies.Log?.Invoke($"wind-down for {box.Address} (session {reg.SessionId}) not mailed ({e.Message})");
            return;
        }

        host.AppendPeerRows([new PeerAppend(new JsonObject
        {
            ["type"] = "chain-down",
            ["state"] = "active",
            ["session_id"] = reg.SessionId,
            ["member"] = member,
            ["team"] = team,
            ["link"] = link,
            ["why"] = chain.Why,
            ["at"] = at,
        })]);

        try
        {
            host.Mail(new WatchMail(orchestrator, reg.HierarchyDir, cwd, "member-wound-down", $"{member} wound down: chain broken at {link} ({chain.Why})", DataOf(link, chain.Why, first, "chain-down-note:" + reg.SessionId), string.Empty));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            dependencies.Log?.Invoke($"member-wound-down for {orchestrator} (session {reg.SessionId}) not mailed ({e.Message})");
        }
    }

    private static JsonObject DataOf(string link, string why, double now, string requestId) => new()
    {
        ["link"] = link,
        ["why"] = why,
        ["due_slot"] = now,
        ["request_id"] = requestId,
    };
}
