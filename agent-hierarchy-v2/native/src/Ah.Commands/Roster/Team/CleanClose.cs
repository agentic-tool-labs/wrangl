using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// The clean-close token: a plan of closing members that all hold no unfiled work carries it, whoever holds the authority of the
/// run, and the permission hook lets a close with it through unasked. The hook only recognises its form (<see cref="Prefix"/> and 16 hex digits);
/// this class decides. A member is clean only when it is released, idle, has nothing queued, has finished its stream and has nothing
/// on its work tree that is not already on a non-backup <c>origin</c> ref. The token's digest covers each member's state, so the
/// close re-reads everything and refuses any token that is not the plan's.
/// </summary>
internal static partial class CleanClose
{
    internal const string Prefix = "cc1-";

    internal sealed record Member(string Name, JsonValue Row, ReleaseGate.Verdict? Verdict);

    /// <summary>What a plan or a close is judged on: the team, and the members that would close.</summary>
    internal sealed record Subject(string Dir, JsonValue Team, IReadOnlyList<Member> Members);

    /// <summary>The first failed condition per member (empty when every member is clean) and, when clean, the token.</summary>
    internal sealed record Reading(IReadOnlyList<string> Failures, string? Token);

    internal static Member MemberOf(string name, JsonValue row, ReleaseGate.Outcome release) =>
        new(name, row, release.Verdicts.FirstOrDefault(v => v.Member == name));

    internal static Reading Read(RosterContext c, Subject subject, string normalToken)
    {
        if (subject.Members.Count == 0)
        {
            return new Reading(["nothing to close"], null);
        }

        try
        {
            return ReadCore(c, subject, normalToken);
        }
        catch (StoreUnavailableException)
        {
            // defensive: ReleaseGate holds first during an outage
            // no clean token while the store cannot answer what is queued; the normal close token still works
            return new Reading([StoreUnreadable], null);
        }
    }

    private const string StoreUnreadable = "message store cannot be read";

    private static Reading ReadCore(RosterContext c, Subject subject, string normalToken)
    {
        // what is queued or unfiled decides whether a member may close: a message store that cannot answer refuses the clean close
        using var decisions = c.Services.Hierarchy.Core.DecisionReads();
        var roster = c.Services.Hierarchy.Roster.AttributedRoster(subject.Dir);
        var open = c.Services.Launch!.Streams.OpenTeamExchanges(subject.Dir, c.TeamFile, allTeams: false);
        var failures = new List<string>();
        var entries = new JsonArray();
        var pools = new Dictionary<string, (IReadOnlyList<string> Pools, List<JsonObject> Exchanges)>(StringComparer.Ordinal);
        foreach (var member in subject.Members.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            var (failure, entry) = Assess(c, subject, member, roster, open, pools);
            if (failure is not null)
            {
                failures.Add($"{member.Name}: {failure}");
            }

            entries.Add(entry);
        }

        if (failures.Count > 0)
        {
            return new Reading(failures, null);
        }

        var digest = new JsonObject { ["base"] = normalToken, ["members"] = entries };
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(JsonText.Stringify(digest, 0)));
        return new Reading([], Prefix + Convert.ToHexString(hash).ToLowerInvariant()[..16]);
    }

    /// <summary>
    /// The first reason the release check holds a member of the subject, or null when none is held: a member with unshipped or uncommitted
    /// work closes with a warning, and its work stays in its tree.
    /// </summary>
    internal static string? NotReleased(Subject subject)
    {
        if (subject.Members.Count == 0)
        {
            return "nothing to close";
        }

        return subject.Members.FirstOrDefault(static m => m.Verdict is not { Closable: true }) is { } held
            ? $"{held.Name}: {(held.Verdict is null ? "release check skipped" : $"release {held.Verdict.Text}")}"
            : null;
    }

    private static (string? Failure, JsonObject Entry) Assess(
        RosterContext c, Subject subject, Member member, JsonArray roster, JsonArray open,
        Dictionary<string, (IReadOnlyList<string> Pools, List<JsonObject> Exchanges)> pools)
    {
        var name = member.Name;
        var stream = MemberPlanning.Text(member.Row.Member("stream")) ?? string.Empty;
        var streamState = stream.Length == 0 ? string.Empty : MemberPlanning.Text(c.Services.Launch!.Streams.StreamsOf(subject.Team).Get(stream)?.Member("state")) ?? string.Empty;
        var tree = ReleaseGate.WorkTree(c, member.Row, name, inTeam: true, roster);
        var role = MemberPlanning.Text(member.Row.Member("role")) ?? string.Empty;
        var queued = open
            .Where(e => MemberPlanning.Text(e.Member("to_name")) == name)
            .Select(e => MemberPlanning.JsConvertText(e.Member("id")))
            .Concat(role.Length == 0 ? [] : RoleQueued(c, subject.Dir, tree, role, pools))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // b430: a request with a report (a --part report keeps it open) that its sender acked after its last part is closed for this check
        if (queued.Count > 0)
        {
            var responses = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in ReleaseGate.ExchangesFor(c, subject.Dir, tree, pools))
            {
                if (e.Get("id") is { IsText: true } eid && e.Get("response")?.Member("path") is { IsText: true } rp)
                {
                    responses.TryAdd(eid.AsString(), rp.AsString());
                }
            }

            if (queued.Any(responses.ContainsKey))
            {
                var rows = c.Services.Hierarchy.Core.PeerRecordsWhere(new PeerQuery(Types: new HashSet<string>(StringComparer.Ordinal) { "report-acked", "reopened", "sent" }));
                queued.RemoveAll(id => responses.TryGetValue(id, out var path) && AckedAfterLastWork(c, id, path, rows));
            }
        }

        var reading = tree is null ? null : ReleaseGate.TreeState(c, tree, member.Row);
        var entry = new JsonObject
        {
            ["name"] = name,
            ["verdict"] = member.Verdict?.Status ?? string.Empty,
            ["session"] = SessionOf(c, member.Row, name, roster),
            ["head"] = reading?.Head ?? string.Empty,
            ["open"] = TeardownSupport.Strings(queued),
            ["stream"] = stream + ":" + streamState,
        };

        // b450: "ahead" (commits past the run's branch and the release) is decided by Unshipped below, which counts any non-wip origin ref
        // such as the train a --no-ff ship merged the member branch into
        if (member.Verdict is not { Status: "ready" or "not-running" or "ahead" })
        {
            return (member.Verdict is null ? "release check skipped" : $"release {member.Verdict.Text}", entry);
        }

        if (member.Verdict.Status == "ready" && ReleaseGate.BusyNow(c, subject.Dir, member.Row, name, roster))
        {
            return ("busy now", entry);
        }

        if (queued.Count > 0)
        {
            return ($"open request {string.Join(", ", queued)}", entry);
        }

        if (stream.Length > 0 && streamState != "done")
        {
            return ($"stream {stream} is not done", entry);
        }

        if (tree is null)
        {
            return (null, entry);
        }

        if (reading!.Failure is { } failure)
        {
            return ($"cannot tell if shipped: {failure}", entry);
        }

        if (reading.Dirty)
        {
            return ("work tree is dirty", entry);
        }

        return (Unshipped(c, tree), entry);
    }

    /// <summary>
    /// Whether the newest <c>report-acked</c> row of request <paramref name="id"/> is later than the newest part of its response and than
    /// any <c>sent</c> or <c>reopened</c> row of it: an ack the member's later work overtook does not close the request.
    /// </summary>
    private static bool AckedAfterLastWork(RosterContext c, string id, string responsePath, IReadOnlyList<JsonValue> rows)
    {
        DateTimeOffset? acked = null;
        DateTimeOffset? worked = null;
        foreach (var r in rows)
        {
            var type = MemberPlanning.Text(r.Member("type"));
            var of = type == "sent"
                ? c.Services.Hierarchy.Core.ParseMsgFilename(Path.GetFileName(MemberPlanning.Text(r.Member("response")) ?? string.Empty))?.Id
                : MemberPlanning.Text(r.Member("request_id"));
            if (of != id || !DateTimeOffset.TryParse(MemberPlanning.Text(r.Member("ts")), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts))
            {
                continue;
            }

            if (type == "report-acked")
            {
                acked = acked is { } a && a >= ts ? a : ts;
            }
            else
            {
                worked = worked is { } w && w >= ts ? w : ts;
            }
        }

        if (acked is null)
        {
            return false;
        }

        // each fill heads its text `## part N · <time>` or `## final · <time>`; the last heading is the newest part
        var text = c.Services.Hierarchy.Core.MsgBytes(responsePath) is { } bytes ? Encoding.UTF8.GetString(bytes) : string.Empty;
        var part = PartHeading().Matches(text).LastOrDefault();
        if (part is not null && DateTimeOffset.TryParse(part.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var partAt))
        {
            worked = worked is { } w && w >= partAt ? w : partAt;
        }

        return worked is null || acked > worked;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?m)^## (?:part \d+|final) · (\S+)\s*$")]
    private static partial System.Text.RegularExpressions.Regex PartHeading();

    /// <summary>
    /// The open requests addressed to the member's role with no member named. Any such request counts as queued for the member, since
    /// the role's holder cannot be told apart from the request alone.
    /// </summary>
    private static IEnumerable<string> RoleQueued(
        RosterContext c, string dir, string? tree, string role, Dictionary<string, (IReadOnlyList<string> Pools, List<JsonObject> Exchanges)> pools)
    {
        foreach (var exchange in ReleaseGate.ExchangesFor(c, dir, tree, pools))
        {
            if (exchange.Get("response") is { IsMap: true }
                || exchange.Get("request")?.Member("path") is not { IsText: true } path
                || exchange.Get("id") is not { IsText: true } id
                || c.Services.Hierarchy.Core.ReadMsgFile(path.AsString()).Member("fm") is not { IsMap: true } fm
                || MemberPlanning.Text(fm.Member("to_name")) is { Length: > 0 }
                || MemberPlanning.Text(fm.Member("to")) != role)
            {
                continue;
            }

            yield return id.AsString();
        }
    }

    /// <summary>
    /// Why the tree holds work that is not on a non-backup <c>origin</c> ref, or null when every commit of its HEAD is. Reads the refs as they
    /// are, never fetches; no <c>origin</c>, a git error or anything unreadable is not clean.
    /// </summary>
    private static string? Unshipped(RosterContext c, string tree)
    {
        if (!ReleaseGate.Git(c, tree, "config", "--get", "remote.origin.url").Ok)
        {
            return "no origin remote: cannot tell if shipped";
        }

        var count = ReleaseGate.Git(c, tree, "rev-list", "--count", "HEAD", "--not", "--exclude=origin/ah/wip/*", "--remotes=origin");
        if (!count.Ok || !int.TryParse(count.Stdout.Trim(), out var n))
        {
            return $"cannot tell if shipped: {count.Detail}";
        }

        return n == 0 ? null : $"unshipped commits ({n} not on origin)";
    }

    private static string SessionOf(RosterContext c, JsonValue row, string name, JsonArray roster)
    {
        if (row.Member("session_id") is { IsText: true, IsTruthy: true } direct)
        {
            return direct.AsString();
        }

        return c.Services.Hierarchy.Roster.AttributedLiveness(roster, name, c.TeamFile).Get("rec") is { IsMap: true } rec && rec.Member("session_id") is { IsText: true } sid
            ? sid.AsString()
            : string.Empty;
    }
}
