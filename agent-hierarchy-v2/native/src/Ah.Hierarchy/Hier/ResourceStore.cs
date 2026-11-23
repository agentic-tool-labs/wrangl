using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>
/// The named exclusive resources (ah2-509, adr-36): the <c>resources</c> table, machine-wide, keyed by name only. A row is a hold or a
/// waiter owned by the process its pid and start name; a row whose process is gone is dead everywhere (R11). Every write first deletes
/// the dead rows it meets; a read only filters them out.
/// </summary>
internal static partial class ResourceRules
{
    /// <summary>The shape a name must have, after lowercasing (R10): the rules key shape.</summary>
    internal const string Shape = "1-40 characters of a-z, 0-9 and -";

    [GeneratedRegex("^[a-z0-9-]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex Name();

    /// <summary>The name lowercased, or null when it does not have the <see cref="Shape"/>.</summary>
    internal static string? NameOf(string? raw) => raw?.ToLowerInvariant() is { } name && Name().IsMatch(name) ? name : null;

    internal static ResourceRow RowOf(object?[] r) =>
        new((long)r[0]!, (string)r[1]!, (string)r[2]!, (long)r[3]!, r[4] as string, (string)r[5]!, r[6] as string, r[7] as string, (long)r[8]!);
}

internal sealed partial class HierLib
{
    private const string ResourceColumns = "id, name, kind, pid, pid_start, who, session, note, since_ms";

    /// <summary>The one liveness rule (R1, R11): the pid is alive and not reused, by its recorded start time.</summary>
    internal bool ResourceOwnerLive(long pid, string? start) =>
        pid > 1 && pid <= int.MaxValue && _processes.PidAlive((int)pid) && (_processTable is null || !Ah.Core.Time.ProcessIdentity.Reused(_processTable, pid, start));

    /// <summary>The live rows of every name, oldest first; a dead row is filtered out, never deleted.</summary>
    internal IReadOnlyList<ResourceRow> LiveResources() => [.. (_resourceRows?.Invoke() ?? []).Where(r => ResourceOwnerLive(r.Pid, r.PidStart))];

    /// <summary>Deletes the dead rows of <paramref name="name"/> (every name when null) and returns the live ones, oldest first.</summary>
    private List<ResourceRow> ResourceReap(IDbOpTransaction tx, string? name)
    {
        var rows = (name is null
            ? tx.Query($"SELECT {ResourceColumns} FROM resources ORDER BY since_ms, id")
            : tx.Query($"SELECT {ResourceColumns} FROM resources WHERE name = ?1 ORDER BY since_ms, id", name)).Select(ResourceRules.RowOf).ToList();
        var live = new List<ResourceRow>();
        foreach (var row in rows)
        {
            if (ResourceOwnerLive(row.Pid, row.PidStart))
            {
                live.Add(row);
            }
            else
            {
                tx.Execute("DELETE FROM resources WHERE id = ?1", row.Id);
            }
        }

        return live;
    }

    private static (long Pid, string? Start) ResourceOwner(JsonValue args, string key) =>
        args.Member(key) is { IsMap: true } owner && owner.Member("pid") is { IsNumber: true } pid
            ? ((long)pid.AsNumber(), owner.Member("start") is { IsText: true } start ? start.AsString() : null)
            : throw new InvalidOperationException($"args.{key} is undefined");

    private static string ResourceName(JsonValue args) =>
        ResourceRules.NameOf(args.Member("name") is { IsText: true } n ? n.AsString() : null) ?? throw new InvalidOperationException($"resource name must be {ResourceRules.Shape}");

    private static string? ResourceText(JsonValue args, string key) => args.Member(key) is { IsText: true } v ? v.AsString() : null;

    private static bool Owns(ResourceRow row, (long Pid, string? Start) owner) => row.Pid == owner.Pid && row.PidStart == owner.Start;

    internal static JsonObject ResourceJson(ResourceRow row) => new()
    {
        ["name"] = row.Name,
        ["role"] = row.Kind,
        ["who"] = row.Who,
        ["since_ms"] = row.SinceMs,
        ["note"] = row.Note is null ? JsonValue.Null : row.Note,
    };

    /// <summary>
    /// The answer of a write: its state, the number of live waiters and the caller's place among them. A write's result is at most 256
    /// bytes, so who holds the name is read with <c>resources.rows</c>.
    /// </summary>
    private static JsonObject ResourceAnswer(string state, List<ResourceRow> live, (long Pid, string? Start)? waiter)
    {
        var waits = live.Where(static r => r.Kind == "wait").ToList();
        var place = waiter is { } me ? waits.FindIndex(r => Owns(r, me)) : -1;
        return new JsonObject
        {
            ["state"] = state,
            ["waiting"] = waits.Count,
            ["position"] = place >= 0 ? JsonValue.FromNumber(place + 1) : JsonValue.Null,
        };
    }

    /// <summary>
    /// <c>resources.claim</c> (R3, R4, R9): reaps the name, then answers <c>mine</c> when the holder already holds it; grants it when it is
    /// free and the caller is the oldest live waiter or nobody waits (<c>granted</c>, with <c>waited_since_ms</c>); else, with <c>queue</c>,
    /// records the caller's waiter row once, and answers <c>held</c>. The partial unique index makes a second grant insert nothing, and
    /// that claim waits.
    /// </summary>
    internal OpResult ResourceClaimDb(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
    {
        var name = ResourceName(args);
        var holder = ResourceOwner(args, "holder");
        var waiter = ResourceOwner(args, "waiter");
        var live = ResourceReap(tx, name);
        if (live.FirstOrDefault(static r => r.Kind == "hold") is { } held)
        {
            if (Owns(held, holder))
            {
                return ResourceAnswer("mine", live, waiter);
            }
        }
        else
        {
            var waits = live.Where(static r => r.Kind == "wait").ToList();
            var mine = waits.FirstOrDefault(r => Owns(r, waiter));
            if (waits.Count == 0 || waits[0].Id == mine?.Id)
            {
                tx.Execute(
                    "INSERT OR IGNORE INTO resources(name, kind, pid, pid_start, who, session, note, since_ms) VALUES (?1, 'hold', ?2, ?3, ?4, ?5, ?6, ?7)",
                    name, holder.Pid, holder.Start, ResourceText(args, "who") ?? "user", ResourceText(args, "session"), ResourceText(args, "note"), (long)NowMs);
                if (tx.Query("SELECT changes()") is [[long n]] && n == 1)
                {
                    if (mine is not null)
                    {
                        tx.Execute("DELETE FROM resources WHERE id = ?1", mine.Id);
                    }

                    var granted = ResourceAnswer("granted", ResourceReap(tx, name), null);
                    granted["waited_since_ms"] = mine is null ? JsonValue.Null : mine.SinceMs;
                    return granted;
                }
            }
        }

        if (args.Member("queue") is { IsBool: true } queue && queue.AsBool() && !live.Any(r => r.Kind == "wait" && Owns(r, waiter)))
        {
            tx.Execute(
                "INSERT INTO resources(name, kind, pid, pid_start, who, session, note, since_ms) VALUES (?1, 'wait', ?2, ?3, ?4, ?5, NULL, ?6)",
                name, waiter.Pid, waiter.Start, ResourceText(args, "who") ?? "user", ResourceText(args, "session"), (long)NowMs);
        }

        return ResourceAnswer("held", ResourceReap(tx, name), waiter);
    });

    /// <summary>
    /// <c>resources.release</c> (R8): ends the hold when the holder asks, or anyone with <c>force</c> (<c>released</c>, with the ended hold's
    /// <c>since_ms</c>); a non-holder without force changes nothing (<c>held</c>); a free name answers <c>free</c>.
    /// </summary>
    internal OpResult ResourceReleaseDb(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
    {
        var name = ResourceName(args);
        var holder = ResourceOwner(args, "holder");
        var live = ResourceReap(tx, name);
        if (live.FirstOrDefault(static r => r.Kind == "hold") is not { } held)
        {
            return ResourceAnswer("free", live, null);
        }

        if (!Owns(held, holder) && !(args.Member("force") is { IsBool: true } force && force.AsBool()))
        {
            return ResourceAnswer("held", live, null);
        }

        tx.Execute("DELETE FROM resources WHERE id = ?1", held.Id);
        var released = ResourceAnswer("released", ResourceReap(tx, name), null);
        released["released_since_ms"] = held.SinceMs;
        return released;
    });

    /// <summary><c>resources.leave</c> (R6): removes the caller's waiter row of the name, when it has one.</summary>
    internal OpResult ResourceLeaveDb(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
    {
        var name = ResourceName(args);
        var waiter = ResourceOwner(args, "waiter");
        tx.Execute("DELETE FROM resources WHERE name = ?1 AND kind = 'wait' AND pid = ?2 AND pid_start IS ?3", name, waiter.Pid, waiter.Start);
        return ResourceAnswer("left", ResourceReap(tx, name), null);
    });

    /// <summary><c>resources.reap</c>: deletes the dead rows of every name: <c>{live: n}</c>.</summary>
    internal OpResult ResourceReapDb(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, _ => new JsonObject { ["live"] = ResourceReap(tx, null).Count });

    /// <summary><c>resources.rows</c>, a read: the live rows of every name, oldest first; a dead row is filtered out, never deleted (R11).</summary>
    internal JsonValue ResourceRowsOp() => new JsonObject { ["rows"] = LiveResourceJson() };

    internal JsonArray LiveResourceJson()
    {
        var rows = new JsonArray();
        foreach (var row in LiveResources())
        {
            rows.Add(ResourceJson(row));
        }

        return rows;
    }

    /// <summary>
    /// The <c>resources</c> of one team of the status document (ah2-509): the live holds and waits whose <c>who</c> is a member of the team
    /// (<c>name@team</c> or the bare member name), every hold by <c>user</c>, and the hold of a name a member waits on that is held
    /// outside the team, with <c>outside: true</c>.
    /// </summary>
    internal static JsonArray TeamResources(JsonArray live, JsonArray members, string? team)
    {
        var tag = team ?? "_default";
        var names = members.Select(static m => TextOf(Member(m, "name"))).Where(static n => n is { Length: > 0 }).ToHashSet(StringComparer.Ordinal);
        bool Mine(JsonValue row)
        {
            var who = TextOf(Member(row, "who")) ?? string.Empty;
            var at = who.LastIndexOf('@');
            return at > 0 ? who[(at + 1)..] == tag && names.Contains(who[..at]) : names.Contains(who);
        }

        bool IsHold(JsonValue row) => TextOf(Member(row, "role")) == "hold";
        var kept = Enumerable.Range(0, live.Count).Where(i => Mine(live[i]) || (IsHold(live[i]) && TextOf(Member(live[i], "who")) == "user")).ToHashSet();

        // Amendment 2 X1: a member's wait on a name held outside the team carries that hold, marked outside
        var waitedOn = kept.Where(i => !IsHold(live[i])).Select(i => TextOf(Member(live[i], "name"))).ToHashSet(StringComparer.Ordinal);
        var outside = Enumerable.Range(0, live.Count).Where(i => !kept.Contains(i) && IsHold(live[i]) && waitedOn.Contains(TextOf(Member(live[i], "name")))).ToHashSet();
        var rows = new JsonArray();
        for (var i = 0; i < live.Count; i++)
        {
            if (!kept.Contains(i) && !outside.Contains(i))
            {
                continue;
            }

            var row = live[i];
            var entry = new JsonObject
            {
                ["name"] = Member(row, "name"),
                ["role"] = Member(row, "role"),
                ["who"] = TextOf(Member(row, "who")) ?? string.Empty,
                ["since"] = Member(row, "since_ms"),
                ["note"] = Member(row, "note"),
            };
            if (outside.Contains(i))
            {
                entry["outside"] = true;
            }

            rows.Add(entry);
        }

        return rows;
    }
}
