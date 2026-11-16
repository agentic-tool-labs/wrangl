using Ah.Core.Mux;
using Ah.Hierarchy;

namespace Ah.Watch;

/// <summary>
/// The <c>ah.*</c> work labels on herdr-core terminals, the contract herdr desktop mods read: each member's current request
/// (<c>ah.item</c>, <c>ah.title</c>, <c>ah.req</c>, removed when it has none) and the Orchestrator's team summary (<c>ah.title</c>).
/// Every tick reconciles them; a terminal is written only when its labels differ from what this daemon last wrote there, so
/// labels left stale while ahd was down are corrected on the first tick. A failed write is logged and retried next tick.
/// </summary>
/// <param name="write">Writes <c>k=v</c> pairs on a terminal address; false when the write failed.</param>
/// <param name="log">The daemon's log line writer, or null.</param>
internal sealed class WorkLabels(Func<string, IReadOnlyList<string>, bool> write, Action<string>? log)
{
    internal const string ItemLabel = "ah.item";
    internal const string TitleLabel = "ah.title";
    internal const string RequestLabel = "ah.req";

    private readonly Dictionary<string, string> _written = new(StringComparer.Ordinal);

    /// <summary>Brings the labels of every member of <paramref name="owned"/> on a herdr-core terminal, and of <paramref name="orchestratorTerminal"/>, up to date.</summary>
    internal void Reconcile(IReadOnlyList<OwnedTeam> owned, IReadOnlyList<DispatchCandidate> candidates, Func<string?, IReadOnlyList<ChecklistEntry>> checklistOf, string? orchestratorTerminal, double nowMs)
    {
        var summaries = new List<string>();
        foreach (var team in owned)
        {
            var checklist = new Lazy<IReadOnlyList<ChecklistEntry>>(() => checklistOf(team.Name));
            var working = 0;
            foreach (var member in team.Members)
            {
                var current = CurrentRequest(team.Name, member.Name, candidates);
                if (current is not null)
                {
                    working++;
                }

                // a dead member's old address may belong to another terminal by now
                if (member.Live != false && member.Row.Str("transport_id") is { } address && MuxIds.IsHerdrCoreAddress(address))
                {
                    Set(address, MemberPairs(current, current is null ? [] : checklist.Value), nowMs);
                }
            }

            if (orchestratorTerminal is not null && checklist.Value.Count(static e => e.Item.State == "waiting_user") is var waiting && (working > 0 || waiting > 0))
            {
                summaries.Add($"{team.Name ?? "default"} · {working} working · {waiting} waiting on you");
            }
        }

        // a quiet team is left out; with every team quiet the label is removed (key=)
        if (orchestratorTerminal is not null && MuxIds.IsHerdrCoreAddress(orchestratorTerminal) && owned.Count > 0)
        {
            Set(orchestratorTerminal, [$"{TitleLabel}={string.Join("; ", summaries)}"], nowMs);
        }
    }

    /// <summary>The member's oldest open request (a chat note excepted), or null.</summary>
    internal static DispatchCandidate? CurrentRequest(string? team, string member, IReadOnlyList<DispatchCandidate> candidates) =>
        candidates
            .Where(c => c.Open && !c.Note && DispatchClock.NameOf(c.ToName, c.ToAddr) == member && TeamOf(c) is var t && (t is null || t == team))
            .OrderBy(static c => c.Created ?? string.Empty, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>The three member labels for <paramref name="current"/>; every one removed (<c>key=</c>) when it is null.</summary>
    internal static IReadOnlyList<string> MemberPairs(DispatchCandidate? current, IReadOnlyList<ChecklistEntry> checklist)
    {
        if (current is null)
        {
            return [$"{ItemLabel}=", $"{TitleLabel}=", $"{RequestLabel}="];
        }

        var item = current.Item is { } id ? checklist.FirstOrDefault(e => e.Item.Item == id)?.Item.Parent ?? id : string.Empty;
        return [$"{ItemLabel}={item}", $"{TitleLabel}={current.Title ?? string.Empty}", $"{RequestLabel}={current.RequestId}"];
    }

    private static string? TeamOf(DispatchCandidate c) =>
        c.ToAddr is { } to && to.IndexOf('@', StringComparison.Ordinal) is var at and >= 0 ? to[(at + 1)..] : c.Team;

    private void Set(string address, IReadOnlyList<string> pairs, double nowMs)
    {
        var key = string.Join('\n', pairs);
        lock (_written)
        {
            if ((_written.TryGetValue(address, out var had) && had == key) || (_failedUntil.TryGetValue(address, out var until) && nowMs < until))
            {
                return;
            }
        }

        string? why = null;
        try
        {
            if (!write(address, pairs))
            {
                why = "hc label failed";
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            why = e.Message;
        }

        lock (_written)
        {
            if (why is null)
            {
                _written[address] = key;
                _ = _failedUntil.Remove(address);
                return;
            }

            _failedUntil[address] = nowMs + FailureBackoffMs;
        }

        log?.Invoke($"work labels on {address} not written ({why}); skipped for {FailureBackoffMs / 60000:0} min");
    }

    /// <summary>How long a terminal whose label write failed is left alone, so a dead <c>hc</c> does not cost every tick.</summary>
    internal const double FailureBackoffMs = 5 * 60 * 1000;

    private readonly Dictionary<string, double> _failedUntil = new(StringComparer.Ordinal);
}
