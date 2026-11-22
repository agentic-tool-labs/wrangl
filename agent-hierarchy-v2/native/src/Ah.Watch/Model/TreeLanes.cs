using System.Globalization;
using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>Helpers shared by the lanes of the Tree: keys joined from parts, and timestamps read from a record's own fields.</summary>
internal static class LaneParts
{
    public const char Separator = '\u001f';

    public static string Join(string first, string second) => first + Separator + second;

    public static (string First, string Second) Split(string key)
    {
        var at = key.IndexOf(Separator, StringComparison.Ordinal);
        return at < 0 ? throw new ArgumentException("not a two-part key", nameof(key)) : (key[..at], key[(at + 1)..]);
    }

    public static string? Text(JsonValue record, string name) =>
        record.Member(name) is { IsText: true } value && value.AsString().Length > 0 ? value.AsString() : null;

    public static JsonValue TextOrNull(JsonValue record, string name) => Text(record, name) is { } text ? text : JsonValue.Null;

    /// <summary>The epoch ms of an ISO timestamp, or 0 when it is missing or not a timestamp.</summary>
    public static long Ms(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when.ToUnixTimeMilliseconds() : 0;

    public const int TextBound = 280;

    /// <summary>The field's text cut to <see cref="TextBound"/> characters, the last of them <c>…</c> when it was cut; null when it is missing.</summary>
    public static JsonValue Bounded(JsonValue record, string name)
    {
        if (Text(record, name) is not { } text)
        {
            return JsonValue.Null;
        }

        if (text.Length <= TextBound)
        {
            return text;
        }

        var keep = TextBound - 1;
        return (char.IsHighSurrogate(text[keep - 1]) ? text[..(keep - 1)] : text[..keep]) + "…";
    }

    /// <summary>The <c>{team, name}</c> a member row's <c>lead</c> names, served as the row holds it; null when it names none.</summary>
    public static JsonValue? LeadOf(JsonValue row) =>
        row.Member("lead") is { IsMap: true } lead && Text(lead, "team") is not null && Text(lead, "name") is not null
            ? (JsonValue?)JsonValue.FromObject(new JsonObject { ["team"] = Bounded(lead, "team"), ["name"] = Bounded(lead, "name") })
            : null;

    public static JsonValue NumberOrNull(JsonValue record, string name) =>
        record.Member(name) is { IsNumber: true } n && double.IsFinite(n.AsNumber()) ? n : JsonValue.Null;
}

/// <summary>
/// The teams lane: one key per team file, <c>(canonical dir, team file path)</c>. The key is re-read with one <c>team.read</c> of that file;
/// the key set comes from <c>team.keys</c>. The record keeps the team's identity, orchestrator and member rows, never the rest of the file.
/// <paramref name="goalOf"/> (ah2-53) reads a named team's own goal, given its dir and name, into the record's <c>goal</c>; the unnamed team has none.
/// </summary>
internal sealed class TeamLane(IViewStateReader reader, Func<IReadOnlyList<string>> canonicalDirs, Func<IReadOnlyList<string>>? currentDirs = null, Func<string, string?>? branchOf = null, Func<string, string, JsonObject?>? goalOf = null) :IModelLane
{
    public const string LaneName = "teams";

    public const string DefaultName = "default";

    public string Name => LaneName;

    public static string KeyOf(string dir, string path) => LaneParts.Join(dir, path);

    /// <summary>The name a team goes by in ids: <c>default</c> for the unnamed team (<c>team.json</c>), the team's own name otherwise; null for a team literally named <c>default</c>, which would collide with the unnamed one.</summary>
    public static string? IdName(string? teamName) => teamName is null ? DefaultName : teamName == DefaultName ? null : teamName;

    /// <summary>The team name a team file stands for: <c>default</c> for <c>team.json</c>, the alias for <c>teams/&lt;alias&gt;.json</c>; null for an alias that would collide with the default.</summary>
    public static string? NameOf(string dir, string path)
    {
        if (path == dir + "/team.json")
        {
            return IdName(null);
        }

        var prefix = dir + "/teams/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(".json", StringComparison.Ordinal) || path.Length <= prefix.Length + 5)
        {
            return null;
        }

        var alias = path[prefix.Length..^5];
        return alias.Contains('/', StringComparison.Ordinal) ? null : IdName(alias);
    }

    public IReadOnlyList<string> SliceIds(string key)
    {
        var (dir, path) = LaneParts.Split(key);
        return NameOf(dir, path) is { } name ? [TreeIds.Team(dir, name)] : [];
    }

    public LaneResult Reread(string key, long nowMs)
    {
        var (dir, path) = LaneParts.Split(key);
        if (NameOf(dir, path) is not { } name || !(currentDirs ?? canonicalDirs)().Contains(dir, StringComparer.Ordinal))
        {
            return new LaneResult([]) { Record = LaneRecord.Absent };
        }

        var doc = reader.Read("team", "read", dir, new JsonObject { ["path"] = path, ["dir"] = dir });
        if (!doc.IsMap)
        {
            return new LaneResult([]) { Record = LaneRecord.Absent };
        }

        var value = ValueOf(dir, path, name, doc);
        if (branchOf is not null && LaneParts.Text(doc, "expected_root") is { } root && branchOf(root) is { } branch)
        {
            value["branch"] = branch;
        }

        if (goalOf is not null && name != DefaultName && goalOf(dir, name) is { } goal)
        {
            value["goal"] = JsonValue.FromObject(goal);
        }

        return new LaneResult([]) { Record = new LaneRecord(JsonValue.FromObject(value), AsOf(doc)) };
    }

    public IReadOnlyList<string> Keys()
    {
        var keys = new List<string>();
        foreach (var dir in canonicalDirs().Distinct(StringComparer.Ordinal))
        {
            JsonValue reply;
            try
            {
                reply = reader.Read("team", "keys", dir, new JsonObject { ["dir"] = dir });
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                continue;
            }

            if (reply.Member("teams") is { IsArray: true } teams)
            {
                keys.AddRange(teams.AsArray().Select(t => t.Member("path")).Where(static p => p is { IsText: true }).Select(p => KeyOf(dir, p!.Value.AsString())));
            }
        }

        return keys;
    }

    internal static JsonObject ValueOf(string dir, string path, string name, JsonValue doc)
    {
        var members = new JsonArray();
        if (doc.Member("members") is { IsArray: true } rows)
        {
            foreach (var row in rows.AsArray().Where(static r => r.IsMap && LaneParts.Text(r, "name") is not null))
            {
                var entry = new JsonObject
                {
                    ["name"] = LaneParts.Text(row, "name")!,
                    ["role"] = LaneParts.TextOrNull(row, "role"),
                    ["transport_id"] = LaneParts.TextOrNull(row, "transport_id"),
                    ["stream"] = LaneParts.TextOrNull(row, "stream"),
                    ["session_id"] = LaneParts.TextOrNull(row, "session_id"),
                    ["pid"] = LaneParts.NumberOrNull(row, "pid"),
                    ["pid_start"] = LaneParts.TextOrNull(row, "pid_start"),
                };
                if (LaneParts.Text(row, "expected_root") is { } expectedRoot)
                {
                    entry["expected_root"] = expectedRoot;
                }

                if (LaneParts.Text(row, "model") is { } model)
                {
                    entry["model"] = model;
                }

                if (LaneParts.Text(row, "kind") is { Length: > 0 } kind)
                {
                    entry["kind"] = kind;
                }

                if (LaneParts.LeadOf(row) is { } lead)
                {
                    entry["lead"] = lead;
                }

                // ah2-242 S1: the member's transport (its row's, else its team's, ah2-102 E10) and herdr flavour, so the composer can tell a herdr member.
                if ((LaneParts.Text(row, "transport") is { Length: > 0 } own ? own : LaneParts.Text(doc, "transport")) is { Length: > 0 } transport)
                {
                    entry["transport"] = transport;
                }

                if (Ah.Core.Mux.HerdrFlavour.OfMember(row) is { } flavour)
                {
                    entry[Ah.Core.Mux.HerdrFlavour.Field] = flavour;
                }

                members.Add(JsonValue.FromObject(entry));
            }
        }

        var orchestrator = doc.Member("orchestrator") is { IsMap: true } o
            ? JsonValue.FromObject(new JsonObject
            {
                ["session_id"] = LaneParts.TextOrNull(o, "session_id"),
                ["pid"] = LaneParts.NumberOrNull(o, "pid"),
                ["pid_start"] = LaneParts.TextOrNull(o, "pid_start"),
            })
            : JsonValue.Null;
        var value = new JsonObject
        {
            ["name"] = name,
            ["dir"] = dir,
            ["path"] = path,
            ["team_id"] = LaneParts.TextOrNull(doc, "team_id"),
            ["orchestrator"] = orchestrator,
            ["members"] = JsonValue.FromArray(members),
        };
        if (LaneParts.Text(doc, "expected_root") is { } workRoot)
        {
            value["expected_root"] = workRoot;
        }

        if (LaneParts.Text(doc, "display_name") is { } displayName)
        {
            value["display_name"] = displayName;
        }

        if (doc.Member("closed") is { IsMap: true } closed && LaneParts.Text(closed, "at") is { } closedAt)
        {
            value["closed"] = new JsonObject { ["at"] = closedAt };
        }

        if (doc.Member("reopened") is { IsMap: true } reopened && LaneParts.Text(reopened, "at") is { } reopenedAt)
        {
            value["reopened"] = new JsonObject { ["at"] = reopenedAt };
        }

        if (doc.Member("parent") is { IsMap: true } parent && LaneParts.Text(parent, "team") is not null)
        {
            value["parent"] = new JsonObject { ["team"] = LaneParts.Bounded(parent, "team"), ["stream"] = LaneParts.Bounded(parent, "stream") };
        }

        if (doc.Member("workstream") is { IsMap: true } workstream)
        {
            var reported = new JsonObject
            {
                ["state"] = LaneParts.Bounded(workstream, "state"),
                ["summary"] = LaneParts.Bounded(workstream, "summary"),
                ["updated"] = LaneParts.Bounded(workstream, "updated"),
            };
            if (LaneParts.Text(workstream, "result") is not null)
            {
                reported["result"] = LaneParts.Bounded(workstream, "result");
            }

            value["workstream"] = reported;
        }

        if (doc.Member("streams") is { IsMap: true } streams)
        {
            var kept = new JsonObject();
            var source = streams.AsObject();
            foreach (var stream in source.Keys.Where(static k => k.Length is > 0 and <= LaneParts.TextBound).Order(StringComparer.Ordinal))
            {
                if (source[stream] is { IsMap: true } record)
                {
                    kept[stream] = StreamOf(record);
                }
            }

            value["streams"] = kept;
            value["streams_rev"] = LaneView.Revision(kept);
        }

        return value;
    }

    /// <summary>The fields of a manager's stream record that a view serves, each bounded; nothing else of the record is kept.</summary>
    private static JsonValue StreamOf(JsonValue record)
    {
        var row = new JsonObject
        {
            ["state"] = LaneParts.Bounded(record, "state"),
            ["branch"] = LaneParts.Bounded(record, "branch"),
            ["base"] = LaneParts.Bounded(record, "base"),
            ["sub_orchestrator"] = LaneParts.Bounded(record, "sub_orchestrator"),
        };
        if (record.Member("lane") is { IsMap: true } caps)
        {
            row["lane"] = new JsonObject { ["max_members"] = LaneParts.NumberOrNull(caps, "max_members"), ["max_streams"] = LaneParts.NumberOrNull(caps, "max_streams") };
        }

        return JsonValue.FromObject(row);
    }

    /// <summary>The latest of the document's own timestamps: <c>created</c> and the workstream's <c>updated</c>; 0 when it has neither.</summary>
    internal static long AsOf(JsonValue doc) =>
        Math.Max(LaneParts.Ms(LaneParts.Text(doc, "created")), doc.Member("workstream") is { IsMap: true } w ? LaneParts.Ms(LaneParts.Text(w, "updated")) : 0);
}

/// <summary>
/// The registrations lane: one key per session id. A key is re-read with one <c>mailbox.registration</c>, which reads that session's own record;
/// the key set comes from <c>mailbox.keys</c>. The record carries no liveness verdict, only the registration's own fields.
/// </summary>
internal sealed class RegistrationLane(IViewStateReader reader, string home) : IModelLane, IQuietKeys
{
    public const string LaneName = "registrations";

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _readAt = new(StringComparer.Ordinal);
    private JsonObject _stamps = new();

    public string Name => LaneName;

    public IReadOnlyList<string> SliceIds(string key) => [];

    /// <summary>The sessions whose stamp in the last listing is the one they were last read at.</summary>
    public IReadOnlySet<string> QuietKeys()
    {
        lock (_gate)
        {
            return _readAt.Where(r => _stamps.Get(r.Key) is { IsText: true } now && now.AsString() == r.Value).Select(static r => r.Key).ToHashSet(StringComparer.Ordinal);
        }
    }

    public LaneResult Reread(string key, long nowMs)
    {
        // the stamp of the listing, older than the read: a write in between costs one more read, never a stale record
        lock (_gate)
        {
            if (_stamps.Get(key) is { IsText: true } stamp)
            {
                _readAt[key] = stamp.AsString();
            }
            else
            {
                _ = _readAt.Remove(key);
            }
        }

        var reply = reader.Read("mailbox", "registration", home, new JsonObject { ["session_id"] = key });
        if (reply.Member("registration") is not { IsMap: true } r)
        {
            return new LaneResult([]) { Record = LaneRecord.Absent };
        }

        var addresses = new JsonArray();
        if (r.Member("addresses") is { IsArray: true } list)
        {
            foreach (var a in list.AsArray().Where(static a => a.IsText).Select(static a => a.AsString()).Order(StringComparer.Ordinal))
            {
                addresses.Add(a);
            }
        }

        var files = new JsonArray();
        if (reply.Member("team_files") is { IsArray: true } teamFiles)
        {
            foreach (var f in teamFiles.AsArray().Where(static f => f.IsText).Select(static f => f.AsString()).Order(StringComparer.Ordinal))
            {
                files.Add(f);
            }
        }

        var value = new JsonObject
        {
            ["session_id"] = key,
            ["address"] = LaneParts.TextOrNull(r, "address"),
            ["addresses"] = JsonValue.FromArray(addresses),
            ["team_files"] = JsonValue.FromArray(files),
            ["pid"] = LaneParts.NumberOrNull(r, "pid"),
            ["pid_start"] = LaneParts.TextOrNull(r, "pid_start"),
            ["at"] = LaneParts.TextOrNull(r, "at"),
        };
        if (LaneParts.Text(r, "pane_id") is { } paneId)
        {
            value["pane_id"] = paneId;
        }

        if (r.Member("pid_proof") is { IsNull: false } pidProof)
        {
            value["pid_proof"] = pidProof;
        }

        return new LaneResult([]) { Record = new LaneRecord(JsonValue.FromObject(value), LaneParts.Ms(LaneParts.Text(r, "at"))) };
    }

    public IReadOnlyList<string> Keys()
    {
        JsonValue reply;
        try
        {
            reply = reader.Read("mailbox", "keys", home, new JsonObject());
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            lock (_gate)
            {
                _stamps = new JsonObject();
            }

            return [];
        }

        lock (_gate)
        {
            _stamps = reply.Member("stamps") is { IsMap: true } stamps ? stamps.AsObject() : new JsonObject();
        }

        return reply.Member("sessions") is { IsArray: true } sessions ? [.. sessions.AsArray().Where(static s => s.IsText).Select(static s => s.AsString())] : [];
    }
}

/// <summary>
/// The worktrees lane: one key per grants document, <c>(canonical dir, team)</c>. A key is re-read with one <c>worktree-grants.status</c>; the
/// key set comes from <c>worktree-grants.keys</c>. The record is the grant mode and, per member, the recorded work root, stream and role.
/// </summary>
internal sealed class WorktreeLane(IViewStateReader reader, Func<IReadOnlyList<string>> canonicalDirs, Func<IReadOnlyList<string>>? currentDirs = null) : IModelLane
{
    public const string LaneName = "worktrees";

    public string Name => LaneName;

    public static string KeyOf(string dir, string team) => LaneParts.Join(dir, team);

    public IReadOnlyList<string> SliceIds(string key) => [];

    public LaneResult Reread(string key, long nowMs)
    {
        var (dir, team) = LaneParts.Split(key);
        if (!(currentDirs ?? canonicalDirs)().Contains(dir, StringComparer.Ordinal))
        {
            return new LaneResult([]) { Record = LaneRecord.Absent };
        }

        var status = reader.Read("worktree-grants", "status", dir, new JsonObject { ["dir"] = dir, ["team"] = team });
        if (!(status.Member("present") is { IsBool: true } present && present.AsBool()))
        {
            return new LaneResult([]) { Record = LaneRecord.Absent };
        }

        var members = new JsonObject();
        if (status.Member("members") is { IsMap: true } map)
        {
            var source = map.AsObject();
            foreach (var name in source.Keys.Order(StringComparer.Ordinal))
            {
                var row = source[name];
                members[name] = JsonValue.FromObject(new JsonObject
                {
                    ["work_root"] = LaneParts.TextOrNull(row, "work_root"),
                    ["stream"] = LaneParts.TextOrNull(row, "stream"),
                    ["role"] = LaneParts.TextOrNull(row, "role"),
                });
            }
        }

        var value = new JsonObject
        {
            ["team"] = team,
            ["dir"] = dir,
            ["mode"] = LaneParts.Text(status, "mode") ?? "off",
            ["members"] = JsonValue.FromObject(members),
        };
        return new LaneResult([]) { Record = new LaneRecord(JsonValue.FromObject(value), 0) };
    }

    public IReadOnlyList<string> Keys()
    {
        var keys = new List<string>();
        foreach (var dir in canonicalDirs().Distinct(StringComparer.Ordinal))
        {
            JsonValue reply;
            try
            {
                reply = reader.Read("worktree-grants", "keys", dir, new JsonObject { ["dir"] = dir });
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                continue;
            }

            if (reply.Member("teams") is { IsArray: true } teams)
            {
                keys.AddRange(teams.AsArray().Where(static t => t.IsText).Select(t => KeyOf(dir, t.AsString())));
            }
        }

        return keys;
    }
}

/// <summary>
/// The decisions lane: one key per pipeline run, <c>(canonical dir, run id)</c>. A key is re-read with one <c>decisions.read</c> of that run's log;
/// the key set comes from <c>decisions.keys</c>. The record is the run id, the number of decisions, the latest decision time, and the counts by distinct id: <c>by_team</c> (decided ids), <c>by_user</c> (parked ids an answer names), <c>waiting</c> (parked ids with no answer) and <c>orphans</c> (answers naming no parked id).
/// </summary>
internal sealed class DecisionsLane(IViewStateReader reader, Func<IReadOnlyList<string>> canonicalDirs, Func<IReadOnlyList<string>>? currentDirs = null) : IModelLane
{
    public const string LaneName = "decisions";

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _orphans = new(StringComparer.Ordinal);

    public string Name => LaneName;

    /// <summary>
    /// How many answers name no parked id, over every run the lane holds. The lane updates it on the projector thread as it re-reads a run,
    /// and a health request only reads it, so the record store is never touched from another thread.
    /// </summary>
    public long Orphans => _orphans.Values.Sum(static n => (long)n);

    public static string KeyOf(string dir, string run) => LaneParts.Join(dir, run);

    public static string LogPath(string dir, string run) => dir + "/pipeline/" + run + "/decisions.jsonl";

    public IReadOnlyList<string> SliceIds(string key) => [];

    public LaneResult Reread(string key, long nowMs)
    {
        var (dir, run) = LaneParts.Split(key);
        if (!(currentDirs ?? canonicalDirs)().Contains(dir, StringComparer.Ordinal))
        {
            _ = _orphans.TryRemove(key, out _);
            return new LaneResult([]) { Record = LaneRecord.Absent };
        }

        var reply = reader.Read("decisions", "read", dir, new JsonObject { ["path"] = LogPath(dir, run) });
        if (reply.Member("decisions") is not { IsArray: true } rows || rows.AsArray().Count == 0)
        {
            _ = _orphans.TryRemove(key, out _);
            return new LaneResult([]) { Record = LaneRecord.Absent };
        }

        string? last = null;
        foreach (var row in rows.AsArray().Where(static r => r.IsMap))
        {
            if (LaneParts.Text(row, "time") is { } time && (last is null || string.CompareOrdinal(time, last) > 0))
            {
                last = time;
            }
        }

        var maps = rows.AsArray().Where(static r => r.IsMap).ToList();
        var parked = IdsOfKind(maps, "parked");
        var parkedCount = parked.Count + IdlessOfKind(maps, "parked");
        var decidedCount = IdsOfKind(maps, "decided").Count + IdlessOfKind(maps, "decided");
        var answerNames = maps.Where(static r => LaneParts.Text(r, "kind") == "answered").Select(static r => NonEmptyText(r, "answers")).ToList();
        var answered = answerNames.Where(a => a is not null && parked.Contains(a)).Distinct(StringComparer.Ordinal).Count();
        var orphans = answerNames.Count(a => a is null || !parked.Contains(a));
        if (orphans > 0)
        {
            _orphans[key] = orphans;
        }
        else
        {
            _ = _orphans.TryRemove(key, out _);
        }
        var value = new JsonObject
        {
            ["run"] = run,
            ["dir"] = dir,
            // Questions, not log lines: a question parked and answered is two lines.
            ["decisions_count"] = JsonValue.FromNumber(parkedCount + decidedCount),
            ["last_decision_at"] = last is null ? JsonValue.Null : last,
            ["by_team"] = JsonValue.FromNumber(decidedCount),
            ["by_user"] = JsonValue.FromNumber(answered),
            ["waiting"] = JsonValue.FromNumber(parkedCount - answered),
            ["orphans"] = JsonValue.FromNumber(orphans),
            ["open"] = OpenRows(maps, answerNames),
        };
        return new LaneResult([]) { Record = new LaneRecord(JsonValue.FromObject(value), LaneParts.Ms(last)) };
    }

    /// <summary>The parked rows with an id and no answer, as a team summary lists them; <see cref="QuestionsLane.OpenTextBytes"/> caps each question as the questions slice does.</summary>
    private static JsonArray OpenRows(List<JsonValue> maps, List<string?> answers)
    {
        var open = new JsonArray();
        foreach (var row in maps.Where(static r => LaneParts.Text(r, "kind") == "parked"))
        {
            if (NonEmptyText(row, "id") is { } id && !answers.Contains(id))
            {
                open.Add(JsonValue.FromObject(new JsonObject
                {
                    ["id"] = id,
                    ["question"] = QuestionsLane.Cut(LaneParts.Text(row, "question") ?? string.Empty, QuestionsLane.OpenTextBytes),
                    ["item"] = LaneParts.TextOrNull(row, "item"),
                    ["time"] = LaneParts.TextOrNull(row, "time"),
                }));
            }
        }

        return open;
    }

    // An id, and an answer's target, is a non-empty string; any other value (absent, null, empty, a number) is none.
    private static string? NonEmptyText(JsonValue row, string member) => LaneParts.Text(row, member) is { Length: > 0 } text ? text : null;

    private static HashSet<string> IdsOfKind(List<JsonValue> rows, string kind) =>
        rows.Where(r => LaneParts.Text(r, "kind") == kind && NonEmptyText(r, "id") is not null).Select(static r => NonEmptyText(r, "id")!).ToHashSet(StringComparer.Ordinal);

    // A row with no usable id is still a recorded decision: it counts once per row and nothing can answer it.
    private static int IdlessOfKind(List<JsonValue> rows, string kind) =>
        rows.Count(r => LaneParts.Text(r, "kind") == kind && NonEmptyText(r, "id") is null);

    public IReadOnlyList<string> Keys()
    {
        var keys = new List<string>();
        foreach (var dir in canonicalDirs().Distinct(StringComparer.Ordinal))
        {
            JsonValue reply;
            try
            {
                reply = reader.Read("decisions", "keys", dir, new JsonObject { ["dir"] = dir });
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                continue;
            }

            if (reply.Member("runs") is { IsArray: true } runs)
            {
                keys.AddRange(runs.AsArray().Where(static r => r.IsText).Select(r => KeyOf(dir, r.AsString())));
            }
        }

        return keys;
    }
}

/// <summary>
/// The parked-questions lane (b517): one key per canonical hierarchy directory, the home <c>ah msg decision park</c> writes a team's <c>q&lt;k&gt;</c> questions to.
/// A key is re-read with one read of that directory's parked-question store through <paramref name="probe"/> (null when the store cannot be read); the record is the open questions, each naming its team.
/// The store is written outside the daemon (<c>decisions.ask</c> is not served), so no write names it: the directory's status refresh, which park and answer ask for, re-reads it.
/// </summary>
internal sealed class ParkedLane(Func<string, IReadOnlyList<Ah.Hierarchy.ParkedQuestion>?> probe, Func<IReadOnlyList<string>> canonicalDirs, Func<IReadOnlyList<string>>? currentDirs = null) : IModelLane
{
    public const string LaneName = "parked";

    /// <summary>The <c>source</c> of a waiting row that is a parked question; a run's decision has none, or its own.</summary>
    public const string Source = "parked";

    public string Name => LaneName;

    public IReadOnlyList<string> SliceIds(string key) => [];

    public IReadOnlyList<string> Keys() => [.. canonicalDirs().Distinct(StringComparer.Ordinal)];

    public LaneResult Reread(string key, long nowMs)
    {
        if (!(currentDirs ?? canonicalDirs)().Contains(key, StringComparer.Ordinal))
        {
            return new LaneResult([]) { Record = LaneRecord.Absent };
        }

        // a store that cannot be read is no reading: the projector keeps the record it has and reads again, so a question never drops off for a busy store
        var open = Rows(probe(key) ?? throw new InvalidOperationException("the parked-question store of " + key + " could not be read"));
        return new LaneResult([]) { Record = open.Count == 0 ? LaneRecord.Absent : new LaneRecord(JsonValue.FromObject(new JsonObject { ["dir"] = key, ["open"] = open }), 0) };
    }

    /// <summary>The open questions as waiting rows, in the field names an open decision row has; the header is <c>header</c>, never <c>item</c>, which would hide a checklist item of that name.</summary>
    internal static JsonArray Rows(IReadOnlyList<Ah.Hierarchy.ParkedQuestion> questions)
    {
        var open = new JsonArray();
        foreach (var q in questions.Where(static p => p.Open))
        {
            var options = new JsonArray();
            foreach (var label in q.Labels)
            {
                options.Add(label);
            }

            var row = new JsonObject
            {
                ["id"] = q.Id,
                ["team"] = q.Team,
                ["source"] = Source,
                ["header"] = q.Header,
                ["question"] = QuestionsLane.Cut(q.Text, QuestionsLane.OpenTextBytes),
                ["options"] = options,
                ["default"] = Ah.Hierarchy.ParkedQuestions.DefaultText(q.Default),
                ["class"] = q.Class is null ? JsonValue.Null : q.Class,
                ["asked_for"] = q.AskedFor is null ? JsonValue.Null : q.AskedFor,
                ["time"] = q.At,
            };
            if (q.Question.Member("multiSelect") is { IsBool: true } multi && multi.AsBool())
            {
                row["multi"] = true;
            }

            // ah2-387 T1: the answer route's own test; the row's source is Source, so the question's own source is read here
            if (Ah.Hierarchy.ParkedQuestions.IsTerminalOnly(q))
            {
                row["terminal_only"] = true;
            }

            open.Add(JsonValue.FromObject(row));
        }

        return open;
    }
}

/// <summary>The known-dirs lane: one key, <c>dirs</c>, the set of hierarchy directories the view enumerates.</summary>
internal sealed class KnownDirsLane(IViewStateReader reader, string home) : IModelLane
{
    public const string LaneName = "known-dirs";

    public const string Key = "dirs";

    public string Name => LaneName;

    public IReadOnlyList<string> SliceIds(string key) => [];

    public LaneResult Reread(string key, long nowMs)
    {
        var reply = reader.Read("known-dirs", "read", home, new JsonObject());
        if (reply.Member("dirs") is not { IsArray: true } dirs)
        {
            return new LaneResult([]) { Record = LaneRecord.Absent };
        }

        var sorted = new JsonArray();
        foreach (var dir in dirs.AsArray().Where(static d => d.IsText).Select(static d => d.AsString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            sorted.Add(dir);
        }

        return new LaneResult([]) { Record = new LaneRecord(JsonValue.FromObject(new JsonObject { ["dirs"] = JsonValue.FromArray(sorted) }), 0) };
    }

    public IReadOnlyList<string> Keys() => [Key];
}
