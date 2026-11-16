using System.Globalization;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Commands;

/// <summary>
/// Auto-mode classifier blocks and the decision taken on each. The alarm records every block as a <c>CLASSIFIER-DENIED</c> peer row;
/// <c>roster doctor</c> lists the untriaged ones of the last seven days in groups, and the owner-only <c>roster block-triage</c> appends a
/// <c>classifier-triaged</c> row per block it decides.
/// </summary>
internal static class BlockTriage
{
    internal const string TriagedType = "classifier-triaged";
    internal const string DeniedKind = "CLASSIFIER-DENIED";
    internal const string ValidDecisions = "issue:<#N>, upstream:<url>, guidance, outward-confirm, none";
    internal static readonly TimeSpan Window = TimeSpan.FromDays(7);

    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "cwd", "team", "decision", "note", "orchestrator-pid", "session" };

    /// <summary>The blocks of one group: <c>verdict|label|first word|role</c>.</summary>
    internal sealed record Group(string Key, int Count, double NewestMs, string NewestAt, string Sample, IReadOnlyList<string> RowIds);

    private static bool IsDecision(string d)
    {
        if (d is "guidance" or "outward-confirm" or "none")
        {
            return true;
        }

        if (d.StartsWith("issue:#", StringComparison.Ordinal))
        {
            var digits = d.AsSpan("issue:#".Length);
            return digits.Length > 0 && digits.IndexOfAnyExceptInRange('0', '9') < 0;
        }

        foreach (var scheme in new[] { "upstream:https://", "upstream:http://" })
        {
            if (d.StartsWith(scheme, StringComparison.Ordinal))
            {
                var rest = d.AsSpan(scheme.Length);
                return rest.Length > 0 && !rest.ContainsAny(" \t\r\n");
            }
        }

        return false;
    }

    private static string? Text(JsonValue row, string key) => row.Member(key) is { IsText: true } t ? t.AsString() : null;

    /// <summary>What names a block in a triage row: its tool use id, else its session and time.</summary>
    internal static string RowId(JsonValue row) => Text(row, "tool_use_id") is { Length: > 0 } id ? id : Text(row, "session_id") + "@" + Text(row, "ts");

    private static string FirstWord(string command)
    {
        var trimmed = command.TrimStart();
        var end = trimmed.AsSpan().IndexOfAny(" \t\r\n");
        return end < 0 ? trimmed : trimmed[..end];
    }

    internal static string KeyOf(JsonValue row) =>
        string.Join('|', Text(row, "verdict") ?? "denied", Text(row, "label") ?? "unknown", FirstWord(Text(row, "command") ?? string.Empty), Text(row, "role") ?? "unknown");

    /// <summary>The only two row types <see cref="Untriaged"/> reads: the blocks and the rows that triage them.</summary>
    private static IReadOnlyList<JsonValue> TriageRows(Ah.Hierarchy.IHierarchyCore core) =>
        core.PeerRecordsWhere(new PeerQuery(Types: new HashSet<string>(StringComparer.Ordinal) { TriagedType, "watch-event" }));

    /// <summary>The blocks of the last seven days that no <c>classifier-triaged</c> row names, grouped, the largest group first.</summary>
    internal static List<Group> Untriaged(IEnumerable<JsonValue> rows, DateTimeOffset now)
    {
        var all = rows.ToList();
        var done = all.Where(static r => Text(r, "type") == TriagedType).Select(static r => Text(r, "row_id")).Where(static i => i is not null).ToHashSet(StringComparer.Ordinal);
        var cutoff = now.ToUnixTimeMilliseconds() - Window.TotalMilliseconds;
        var groups = new Dictionary<string, (int Count, double Newest, string NewestAt, string Sample, List<string> Ids)>(StringComparer.Ordinal);
        foreach (var r in all)
        {
            if (Text(r, "type") != "watch-event" || Text(r, "kind") != DeniedKind || Text(r, "ts") is not { } ts
                || !Timestamps.TryParseMs(ts, out var ms) || ms < cutoff || done.Contains(RowId(r)))
            {
                continue;
            }

            var key = KeyOf(r);
            var held = groups.TryGetValue(key, out var g) ? g : (Count: 0, Newest: 0d, NewestAt: string.Empty, Sample: string.Empty, Ids: new List<string>());
            held.Ids.Add(RowId(r));
            groups[key] = ms >= held.Newest
                ? (held.Count + 1, ms, ts, Text(r, "command") ?? string.Empty, held.Ids)
                : (held.Count + 1, held.Newest, held.NewestAt, held.Sample, held.Ids);
        }

        return groups.Select(static p => new Group(p.Key, p.Value.Count, p.Value.Newest, p.Value.NewestAt, p.Value.Sample, p.Value.Ids))
            .OrderByDescending(static g => g.Count).ThenBy(static g => g.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>Doctor's section: absent (null) when nothing is untriaged.</summary>
    internal static JsonObject? DoctorRow(RosterContext c)
    {
        var groups = Untriaged(TriageRows(c.Services.Hierarchy.Core), c.Services.Clock.UtcNow);
        if (groups.Count == 0)
        {
            return null;
        }

        var total = groups.Sum(static g => g.Count);
        var lines = groups.Select(static g =>
            $"{g.Key} x{g.Count.ToString(CultureInfo.InvariantCulture)} newest {g.NewestAt} e.g. `{(g.Sample.Length > 120 ? g.Sample[..120] : g.Sample)}`");
        return new JsonObject
        {
            ["name"] = "classifier blocks (7 d)",
            ["status"] = "warn",
            ["detail"] = $"{total.ToString(CultureInfo.InvariantCulture)} untriaged: " + string.Join("; ", lines)
                + $". Decide with: ah roster block-triage <tool_use_id|group key> --decision {ValidDecisions} --note <why>",
        };
    }

    internal static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure($"block-triage: unrecognized flag --{key} (use --decision, --note, --team or --cwd)");
            }
        }

        var words = c.Opts.Positionals.Where(static p => p != "block-triage").ToList();
        if (words.Count != 1)
        {
            throw new RosterFailure("block-triage: needs one <tool_use_id|group key>");
        }

        var decision = c.Opts.Text("decision") ?? throw new RosterFailure($"block-triage: needs --decision {ValidDecisions}");
        if (!IsDecision(decision))
        {
            throw new RosterFailure($"block-triage: \"{decision}\" is not a decision; the valid ones are {ValidDecisions}");
        }

        var note = c.Opts.Text("note");
        if (string.IsNullOrWhiteSpace(note))
        {
            throw new RosterFailure("block-triage: needs --note <why>");
        }

        if (c.Services.OwnerProof is not { } owner)
        {
            c.Stderr.WriteLine("ah: roster block-triage is not implemented natively: the owner proof is not provided");
            return 70;
        }

        var proof = owner.Check(c.TeamHome, c.TeamFile);
        if (!proof.Allowed)
        {
            c.Stderr.WriteLine(proof.Text);
            if (proof.Why is { Length: > 0 } why)
            {
                c.Stderr.WriteLine(why);
            }

            return 2;
        }

        var now = c.Services.Clock.UtcNow;
        var target = words[0];
        var untriaged = Untriaged(TriageRows(c.Services.Hierarchy.Core), now);
        var ids = target.Contains('|', StringComparison.Ordinal)
            ? untriaged.Where(g => g.Key == target).SelectMany(static g => g.RowIds).ToList()
            : untriaged.SelectMany(static g => g.RowIds).Where(i => i == target).ToList();
        if (ids.Count == 0)
        {
            throw new RosterFailure($"block-triage: no untriaged block matches \"{target}\" (ah roster doctor lists them)");
        }

        foreach (var id in ids)
        {
            c.Services.Hierarchy.Peers.RecordPeerRow(JsonValue.FromObject(new JsonObject
            {
                ["type"] = TriagedType,
                ["row_id"] = id,
                ["group"] = target.Contains('|', StringComparison.Ordinal) ? target : JsonValue.Null,
                ["decision"] = decision,
                ["note"] = note,
                ["ts"] = Timestamps.ToIsoString(now),
            }));
        }

        c.Out(new JsonObject { ["action"] = "block-triage", ["decision"] = decision, ["triaged"] = (double)ids.Count });
        return 0;
    }
}
