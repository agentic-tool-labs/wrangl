using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Roster;

namespace Ah.Mesh;

internal sealed partial class Mailbox
{
    /// <summary>A team's record in <paramref name="home"/>, null when it has none; not readable when the file exists and could not be read as one.</summary>
    internal (bool Readable, JsonValue Record) TeamRecordOf(string home, string? team)
    {
        var record = roster.ReadTeamRecord(home, team);
        return (!record.IsNull || !roster.TeamExistsAt(roster.TeamPath(home, team)), record);
    }

    /// <summary>
    /// Mail to a held team is appended and drained in the same call. Returns the send result when the entry was drained, else null.
    /// A brief marked <c>hold_exempt</c> is still delivered while the hold is winding down or stopped; a request from outside cannot carry the mark
    /// through the CLI, which builds its request from flags only.
    /// </summary>
    private JsonObject? DrainIfHeld(ResolvedAddress to, RequestContext context, JsonObject req, JsonObject stored)
    {
        var hold = roster.HoldOf(to.Home ?? context.HierarchyDirectory, to.Team);
        if (hold is null || (hold.AcceptsExempt && req.Get("hold_exempt") is { IsBool: true } exempt && exempt.AsBool()))
        {
            return null;
        }

        var id = Js(stored.Get("id"));
        DrainEntry(to.Dir, stored, hold, to.Team);
        return new JsonObject
        {
            ["entry"] = stored,
            ["address"] = to.Address,
            ["delivery"] = DrainedState.State,
            ["message"] = $"team {to.Team} is {hold.Kind}; this message will not be delivered",
            ["id"] = id,
        };
    }

    public JsonObject DrainTeam(string teamFile, string team, EffectiveHold hold) =>
        SweepBoxes(teamFile, team, (dir, entry) => entry.Get("id") is { IsText: true } id && !IsAcked(dir, id.AsString()) && DrainEntry(dir, entry, hold, team));

    public JsonObject DrainedBy(string teamFile, string team, EffectiveHold hold)
    {
        var by = DrainedByOf(hold);
        return SweepBoxes(
            teamFile,
            team,
            (dir, entry) => entry.Get("id") is { IsText: true } id && LadderRowsOf(dir, id.AsString()) is { Count: > 0 } rows
                && rows[^1].Get("state") is { IsText: true } state && state.AsString() == DrainedState.State
                && rows[^1].Get("by") is { IsText: true } rowBy && rowBy.AsString() == by);
    }

    public JsonObject RedeliverDrained(string teamFile, string team, EffectiveHold hold)
    {
        var by = DrainedByOf(hold);
        return SweepBoxes(teamFile, team, (dir, entry) =>
            entry.Get("id") is { IsText: true } id && Js(entry.Get("kind")) != "event" && entry.Get("hold_exempt") is null
            && LadderRowsOf(dir, id.AsString()) is { Count: > 0 } rows && DrainedState.Is(rows)
            && rows[^1].Get("by") is { IsText: true } rowBy && rowBy.AsString() == by
            && AppendRedelivery(dir, entry, id.AsString()));
    }

    /// <summary>A copy of a drained entry under an id made from the original's, so a second pass finds it already there. The drained row stays the record that the original was not delivered.</summary>
    private bool AppendRedelivery(string dir, JsonObject entry, string id)
    {
        var copy = entry.Clone();
        var nowMs = NowMs;
        copy["id"] = id + "-held";
        copy["redelivers"] = id;
        copy["created"] = Ah.Core.Time.Timestamps.ToIsoString(clock.UtcNow);
        // The session it was for may not be the one that resumes; the address decides.
        _ = copy.Remove("to_session");
        var hb = ReadHeartbeatOf(dir, null, nowMs);
        copy["route"] = FreshOf(hb, nowMs) ? "pull" : "fallback";
        try
        {
            return Append(dir, copy, AttemptRow(copy, hb)).Get("existed") is not { IsTruthy: true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or MailboxError)
        {
            return false;
        }
    }

    private static string DrainedByOf(EffectiveHold hold)
    {
        var stamp = hold.Since is { } since && DateTimeOffset.TryParse(since, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? RosterModule.HoldStamp(at)
            : "unknown";
        return $"hold:{hold.Kind}:{stamp}";
    }

    private JsonObject SweepBoxes(string teamFile, string team, Func<string, JsonObject, bool> take)
    {
        var names = new List<string> { "orchestrator" };
        if (roster.ReadTeamFileAt(teamFile).Member("members") is { IsArray: true } members)
        {
            names.AddRange(members.AsArray().Select(m => m.Member("name")).Where(n => n is { IsText: true }).Select(n => n!.Value.AsString()));
        }

        var rows = new JsonArray();
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            var dir = BoxDirFor(teamFile, name);
            IReadOnlyList<JsonValue> entries;
            try
            {
                entries = ReadEntries(dir).Entries;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var value in entries.Where(static v => v.IsMap))
            {
                var entry = value.AsObject();
                if (entry.Get("id") is not { IsText: true } id || !take(dir, entry))
                {
                    continue;
                }

                var text = entry.Get("text") is { IsText: true } t ? t.AsString() : entry.Get("ref") is { IsText: true } r ? r.AsString() : Js(entry.Get("event"));
                var oneLine = text.ReplaceLineEndings(" ").Replace("|", "/", StringComparison.Ordinal);
                rows.Add(new JsonObject
                {
                    ["id"] = id.AsString(),
                    ["box"] = $"{name}@{team}",
                    ["from"] = Js(entry.Get("from")),
                    ["kind"] = Js(entry.Get("kind")),
                    ["created"] = Js(entry.Get("created")),
                    ["summary"] = oneLine.Length > 80 ? oneLine[..80] : oneLine,
                });
            }
        }

        return new JsonObject { ["entries"] = rows.Count, ["rows"] = rows };
    }

    /// <summary>
    /// Appends the <c>drained</c> ladder row of an entry unless its ladder already ended. Returns whether a row was written.
    /// </summary>
    internal bool DrainEntry(string dir, JsonObject entry, EffectiveHold hold, string team)
    {
        var id = Js(entry.Get("id"));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var rows = LadderRowsOf(dir, id);
            if (rows.Count > 0 && rows[^1].Get("state") is { IsText: true } last && DrainedState.IsTerminal(last.AsString()))
            {
                return false;
            }

            var rung = rows.Count > 0 && rows[^1].Get("rung") is { IsNumber: true } r ? r : JsonValue.FromNumber(Js(entry.Get("route")) == "pull" ? 2 : 3);
            var row = new JsonObject
            {
                ["id"] = id,
                ["rung"] = rung,
                ["state"] = DrainedState.State,
                ["by"] = DrainedByOf(hold),
                ["team"] = team,
            };
            try
            {
                if (AppendLadderRow(dir, row, rows.Count + 1L))
                {
                    return true;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or MailboxError)
            {
                return false;
            }
        }

        return false;
    }
}
