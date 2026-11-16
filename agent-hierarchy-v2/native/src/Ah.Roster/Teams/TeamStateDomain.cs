using Ah.Abstractions;
using Ah.Core.Files;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Roster;

/// <summary>
/// The <c>team</c> state domain: the team files of each hierarchy dir (a worktree's team lives in the main checkout's), the team
/// history, and the team snapshots. Its operations touch those files only; the pool's status document is refreshed from
/// <see cref="AfterWrite"/>, outside the operation and after its lock is released.
/// </summary>
internal sealed class TeamStateDomain : IStateDomain
{
    private const string LockName = ".domain.lock";

    private readonly RosterCore _core;
    private readonly IClock _clock;
    private readonly IRandom _random;
    private readonly IStatusRefresher? _refresher;

    internal TeamStateDomain(RosterCore core, IClock clock, IRandom random, IStatusRefresher? refresher)
    {
        _core = core;
        _clock = clock;
        _random = random;
        _refresher = refresher;
        Ops =
        [
            new StateOp("write", OpKind.Write, OpStyle.Replace, false, LockName, true, Guard(WriteOp), DigestPrecondition: true, PathArgs: ["path", "dir"], FileDigestApplies: () => Database() is null),
            new StateOp("clear", OpKind.Write, OpStyle.Replace, false, LockName, true, Guard(ClearOp), DigestPrecondition: true, PathArgs: ["path", "dir"], FileDigestApplies: () => Database() is null),
            new StateOp("read", OpKind.Read, null, false, null, true, Guard(ReadOp), PathArgs: ["path", "dir"]),
            new StateOp("names", OpKind.Read, null, false, null, false, Guard(NamesOp), PathArgs: ["dir"]),
            new StateOp("keys", OpKind.Read, null, false, null, false, Guard(KeysOp), PathArgs: ["dir"]),
            new StateOp("snapshot", OpKind.Write, OpStyle.Replace, false, LockName, true, Guard(SnapshotOp), PathArgs: ["dir"]),
            new StateOp("history", OpKind.Write, OpStyle.Replace, false, LockName, true, Guard(HistoryOp), PathArgs: ["dir"]),
        ];
    }

    public string Name => "team";

    public IReadOnlyList<string> StoreNames { get; } = ["team-files", "team-history", "team-snapshots"];

    public IReadOnlyList<StateOp> Ops { get; }

    /// <summary>The directory that holds the domain lock for a request that names no target of its own: the team home of its hierarchy dir.</summary>
    public string StateDirectory(RequestContext context) => NodePath.Join(_core.TeamHomeDir(context.HierarchyDirectory), ".state-team");

    public void AfterWrite(OpCall operation, OpResult result)
    {
        if (_refresher is null)
        {
            return;
        }

        var wrote = operation.Op == "write"
            || (operation.Op == "clear" && JsonText.Parse(result.ResultJson.Span).Member("cleared") is { IsTruthy: true });
        if (wrote && JsonText.Parse(operation.ArgsJson.Span).Member("dir") is { IsText: true } dir)
        {
            _refresher.Delta("team-state", operation, result, dir.AsString());
        }
    }

    // ---------------------------------------------------------------- plumbing

    private static Func<OpCall, IStoreWriteContext?, OpResult> Guard(Func<JsonValue, IStoreWriteContext?, JsonValue> body) =>
        (call, ctx) =>
        {
            try
            {
                var args = call.ArgsJson.IsEmpty ? new JsonObject() : JsonText.Parse(call.ArgsJson.Span);
                var value = body(args, ctx);
                return new OpResult(true, JsonText.ToUtf8(value, 0));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return new OpResult(false, ReadOnlyMemory<byte>.Empty, e.Message);
            }
        };

    private static string Text(JsonValue args, string field) =>
        args.Member(field) is { IsText: true } t ? t.AsString() : throw new InvalidOperationException($"{field} must be a string");

    private static IStoreWriteContext Writer(IStoreWriteContext? ctx) =>
        ctx ?? throw new InvalidOperationException("a write operation needs a write context");

    /// <summary>The bytes a JSON document is written as: two-space indent and a final newline.</summary>
    private static byte[] DocumentBytes(JsonValue data) => [.. JsonText.ToUtf8(data, 2), (byte)'\n'];

    /// <summary>Writes a JSON document beside its target through a tmp file unique to this write, then renames it over the target.</summary>
    private void AtomicWriteJson(IStoreWriteContext ctx, string path, JsonValue data)
    {
        ctx.Mkdir(NodePath.Dirname(path));
        var tmp = TmpNames.For(TmpNameStyle.PidHexTmp, path, Environment.ProcessId, _random);
        ctx.Write(path, tmp, DocumentBytes(data));
    }

    /// <summary>The database's authority; null only where a rig composes no database.</summary>
    private ITeamAuthority? Database() => _core.TeamAuthority;

    private static InvalidOperationException Refused(TeamAuthorityResult result) => new($"{result.Code}: {result.Message}");

    /// <summary>What the engine answers when the digest a writer presented is stale: the writer reads again and retries.</summary>
    private static JsonObject Conflict() => new() { ["conflict"] = true };

    private static string? ExpectOf(JsonValue args) => args.Member("expect") is { IsText: true } expect ? expect.AsString() : null;

    private static bool UnlinkTeamFile(IFileReader files, IStoreWriteContext ctx, string path)
    {
        if (!files.Exists(path))
        {
            return false;
        }

        try
        {
            return ctx.Remove(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- operations

    /// <summary><c>write</c> (replace): <c>{path, team, dir}</c> writes the whole team document <c>team</c> to <c>path</c>.</summary>
    private JsonValue WriteOp(JsonValue args, IStoreWriteContext? ctx)
    {
        var team = args.Member("team") ?? throw new InvalidOperationException("team is required");
        var path = Text(args, "path");
        if (Database() is { } authority)
        {
            var stored = authority.Write(path, DocumentBytes(team), ExpectOf(args));
            return stored.Code == "digest-mismatch" ? Conflict() : stored.Ok ? new JsonObject { ["ok"] = true } : throw Refused(stored);
        }

        AtomicWriteJson(Writer(ctx), path, team);
        return new JsonObject { ["ok"] = true };
    }

    /// <summary><c>clear</c>: <c>{path, dir}</c> removes the team file.</summary>
    private JsonValue ClearOp(JsonValue args, IStoreWriteContext? ctx)
    {
        if (Database() is { } authority)
        {
            if (args.Member("path") is not { IsText: true } teamPath)
            {
                return new JsonObject { ["cleared"] = false };
            }

            var removed = authority.Clear(teamPath.AsString(), ExpectOf(args));
            return removed.Code == "digest-mismatch" ? Conflict() : removed.Ok ? new JsonObject { ["cleared"] = removed.Changed } : throw Refused(removed);
        }

        var writer = Writer(ctx);
        var cleared = args.Member("path") is { IsText: true } p && UnlinkTeamFile(_core.Files, writer, p.AsString());
        return new JsonObject { ["cleared"] = cleared };
    }

    /// <summary><c>read</c>: <c>{path}</c> is that team file's record (or null); <c>{dir}</c> is every visible team of the pool.</summary>
    private JsonValue ReadOp(JsonValue args, IStoreWriteContext? ctx)
    {
        if (args.Member("path") is { IsText: true } path)
        {
            return Database()?.Read(path.AsString()) is { } stored ? RosterCore.TeamDocument(stored.Span) : _core.ReadTeamFileAt(path.AsString());
        }

        var dir = Text(args, "dir");
        var teams = new JsonArray();
        foreach (var name in new string?[] { null }.Concat(_core.ListTeamNames(dir)))
        {
            var team = _core.ReadTeamRecord(dir, name);
            if (!team.IsNull)
            {
                teams.Add(new JsonObject { ["name"] = name is null ? JsonValue.Null : name, ["team"] = team });
            }
        }

        return new JsonObject { ["teams"] = teams };
    }

    /// <summary>
    /// <c>names</c>: <c>{dir}</c> is <c>{teams: [{name, path}]}</c>, one entry for every team file the pool lists (what
    /// <c>ListTeamNames</c> returns, including a file whose record is null or invalid), with the path of its file. With <c>include_default: true</c> the default
    /// team (name null, <c>team.json</c>) comes first, whether or not its file exists.
    /// </summary>
    private JsonValue NamesOp(JsonValue args, IStoreWriteContext? ctx)
    {
        var dir = Text(args, "dir");
        var teams = new JsonArray();
        if (args.Member("include_default") is { IsBool: true } includeDefault && includeDefault.AsBool())
        {
            teams.Add(new JsonObject { ["name"] = JsonValue.Null, ["path"] = _core.TeamPath(dir, null) });
        }

        foreach (var name in _core.ListTeamNames(dir))
        {
            teams.Add(new JsonObject { ["name"] = name, ["path"] = _core.TeamPath(dir, name) });
        }

        return new JsonObject { ["teams"] = teams };
    }

    /// <summary>
    /// <c>keys</c>: <c>{dir}</c> is <c>{teams: [{name, path}]}</c>, the team files that exist: <see cref="NamesOp"/>'s list, with the default
    /// team (name null) first only when its file is there. It reads no file's content.
    /// </summary>
    private JsonValue KeysOp(JsonValue args, IStoreWriteContext? ctx)
    {
        var teams = NamesOp(args, ctx).Member("teams")!.Value.AsArray();
        var keys = new JsonArray();
        if (_core.TeamExistsAt(_core.TeamPath(Text(args, "dir"), null)))
        {
            keys.Add(new JsonObject { ["name"] = JsonValue.Null, ["path"] = _core.TeamPath(Text(args, "dir"), null) });
        }

        foreach (var team in teams)
        {
            keys.Add(team);
        }

        return new JsonObject { ["teams"] = keys };
    }

    /// <summary>
    /// <c>snapshot</c> (replace): <c>{dir, team_name, ts, doc}</c> stores <c>doc</c> as the snapshot <c>&lt;team_name&gt;-&lt;ts&gt;.json</c> of the team home.
    /// With the database it is a <c>team_snapshots</c> row, and the team's rows beyond the newest <see cref="RosterValues.SnapshotKeep"/> are
    /// pruned in the same transaction (old files are never deleted); with no authority it is the file under <c>snapshots/</c>, and the
    /// older files are deleted. The snapshot a hold names is never pruned.
    /// </summary>
    private JsonValue SnapshotOp(JsonValue args, IStoreWriteContext? ctx)
    {
        var writer = Writer(ctx);
        var ts = JsConvert.ToJsString(args.Member("ts"));
        var teamName = JsConvert.ToJsString(args.Member("team_name"));
        if (!RosterCore.IsSnapshotStamp(ts) || !IsSnapshotTeamName(teamName))
        {
            throw new InvalidOperationException("snapshot needs a team_name and a ts like 20260101T120000123Z");
        }

        var baseDir = _core.SnapshotsHome(Text(args, "dir"));
        var name = $"{teamName}-{ts}.json";
        var (known, pinned) = HeldSnapshot(Text(args, "dir"), teamName);

        // a row, and the team's older rows pruned in the same transaction; no snapshots/ dir and no file deleted (files-off-disk §12.3)
        if (Database() is { } authority)
        {
            var document = System.Text.Encoding.UTF8.GetString(DocumentBytes(args.Member("doc") ?? JsonValue.Null));
            var stored = authority.WriteSnapshot(_core.TeamHomeDir(Text(args, "dir")), teamName, ts, document, known ? RosterValues.SnapshotKeep : null, pinned);
            return stored.Ok
                ? new JsonObject { ["file"] = name, ["removed"] = stored.Removed }
                : throw new InvalidOperationException($"failed: {stored.Message}");
        }

        writer.Mkdir(baseDir);
        AtomicWriteJson(writer, NodePath.Join(baseDir, name), args.Member("doc") ?? JsonValue.Null);
        var mine = _core.Files.List(baseDir).Where(f => RosterCore.SnapshotStampOf(f, teamName) is not null).ToList();
        mine.Sort(StringComparer.Ordinal);
        var old = known ? mine.Take(Math.Max(0, mine.Count - RosterValues.SnapshotKeep)).Where(f => f != pinned).ToList() : [];
        foreach (var f in old)
        {
            UnlinkTeamFile(_core.Files, writer, NodePath.Join(baseDir, f));
        }

        return new JsonObject { ["file"] = name, ["removed"] = old.Count };
    }

    /// <summary>The file name of the snapshot the team's hold resumes from, through its checkpoint record; not known when a hold exists and that cannot be read, and then nothing is pruned.</summary>
    private (bool Known, string? Name) HeldSnapshot(string dir, string teamName)
    {
        var hold = _core.OwnHold(dir, teamName == "team" ? null : teamName);
        if (hold is null)
        {
            return (true, null);
        }

        if (hold.ParseError is not null)
        {
            return (false, null);
        }

        try
        {
            if (hold.Record.Member("checkpoint") is not { IsText: true } checkpoint)
            {
                return (true, null);
            }

            return JsonText.Parse(_core.Files.ReadText(checkpoint.AsString())).Member("snapshot") is { IsText: true } snapshot
                ? (true, NodePath.Basename(snapshot.AsString()))
                : (true, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return (false, null);
        }
    }

    /// <summary><c>/^[A-Za-z0-9._-]+$/</c>.</summary>
    private static bool IsSnapshotTeamName(string s) =>
        s.Length > 0 && s.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-');

    /// <summary><c>history</c> (replace): upserts the team-history entry for a fingerprint and evicts the oldest idle entries beyond five.</summary>
    private JsonValue HistoryOp(JsonValue args, IStoreWriteContext? ctx)
    {
        var writer = Writer(ctx);
        var dir = Text(args, "dir");
        var history = _core.ReadHistory(dir);
        var now = Timestamps.LocalIso(_clock);
        var members = args.Member("members");
        var count = members switch
        {
            { IsArray: true } a => a.AsArray().Count,
            { IsText: true } s => s.AsString().Length,
            _ => throw new InvalidOperationException("members must have a length"),
        };
        var alias = args.Member("alias") is { IsTruthy: true } al ? al : JsonValue.Null;
        var aliasText = alias.IsNull ? "default" : JsConvert.ToJsString(alias);
        var label = $"{aliasText} ({count} role{(count == 1 ? string.Empty : "s")})";
        var fingerprint = args.Member("fingerprint");
        var teams = history["teams"].AsArray();
        var index = -1;
        for (var i = 0; i < teams.Count; i++)
        {
            if (teams[i].IsNull)
            {
                throw new InvalidOperationException("cannot read a property of a null history entry");
            }

            if (KindHarness.JsStrictEquals(teams[i].Member("fingerprint"), fingerprint))
            {
                index = i;
                break;
            }
        }

        JsonValue Truthy(string field) => args.Member(field) is { IsTruthy: true } v ? v : JsonValue.Null;

        var rebuilt = new List<JsonValue>(teams);
        JsonValue upsertedId;
        if (index < 0)
        {
            upsertedId = Ids.NewId(_clock, _random);
            rebuilt.Add(new JsonObject
            {
                ["id"] = upsertedId,
                ["fingerprint"] = fingerprint ?? JsonValue.Null,
                ["alias"] = alias,
                ["label"] = label,
                ["created_at"] = now,
                ["last_used"] = now,
                ["last_team_id"] = Truthy("team_id"),
                ["roster_level"] = Truthy("roster_level"),
                ["transport"] = Truthy("transport"),
                ["members"] = members ?? JsonValue.Null,
            });
        }
        else
        {
            var entry = teams[index].IsMap ? teams[index].AsObject().Clone() : new JsonObject();
            upsertedId = entry.Get("id") ?? JsonValue.Null;
            entry["alias"] = alias;
            entry["label"] = label;
            entry["last_used"] = now;
            entry["last_team_id"] = Truthy("team_id");
            entry["roster_level"] = Truthy("roster_level");
            entry["transport"] = Truthy("transport");
            entry["members"] = members ?? JsonValue.Null;
            rebuilt[index] = entry;
        }

        rebuilt = Evict(dir, rebuilt, upsertedId);
        rebuilt = JsSortByLastUsedDescending(rebuilt);
        var arranged = new JsonArray();
        arranged.AddRange(rebuilt);
        history["teams"] = arranged;
        // the document is computed above, outside any transaction; the row is then written in one short one (files-off-disk §12.3)
        if (Database() is { } authority)
        {
            var stored = authority.WriteHistory(_core.TeamHomeDir(dir), System.Text.Encoding.UTF8.GetString(DocumentBytes(history)));
            if (!stored.Ok)
            {
                throw Refused(stored);
            }
        }
        else
        {
            AtomicWriteJson(writer, _core.HistoryPath(dir), history);
        }

        return new JsonObject { ["capExceeded"] = rebuilt.Count > 5 };
    }

    /// <summary><c>t !== victim</c>: objects by identity, everything else by value.</summary>
    private static bool SameEntry(JsonValue a, JsonValue b) =>
        a.IsMap && b.IsMap ? ReferenceEquals(a.AsObject(), b.AsObject()) : !a.IsMap && !b.IsMap && KindHarness.JsStrictEquals(a, b);

    private static string StringField(JsonValue entry, string field)
    {
        var v = entry.Member(field);
        return v is { IsText: true } t ? t.AsString() : JsConvert.ToJsString(v);
    }

    private List<JsonValue> Evict(string dir, List<JsonValue> teams, JsonValue justUpsertedId)
    {
        var current = new List<JsonValue>(teams);
        var now = _clock.UtcNow;
        while (current.Count > 5)
        {
            var candidates = current
                .Where(e => !KindHarness.JsStrictEquals(e.Member("id"), justUpsertedId) && !_core.HistoryEntryIsActive(dir, e, now))
                .ToList();
            if (candidates.Count == 0)
            {
                break;
            }

            var ordered = candidates
                .OrderBy(e => StringField(e, "last_used"), StringComparer.Ordinal)
                .ThenBy(e => StringField(e, "created_at"), StringComparer.Ordinal)
                .ThenBy(e => StringField(e, "id"), StringComparer.Ordinal)
                .ToList();
            var victim = ordered[0];
            current = current.Where(t => !SameEntry(t, victim)).ToList();
        }

        return current;
    }

    /// <summary>
    /// <c>teams.sort((a, b) =&gt; (a.last_used &lt; b.last_used ? 1 : -1))</c> as V8's array sort runs it. The comparator never
    /// answers zero, so which of two entries with the same <c>last_used</c> comes first follows the sort's own steps: a run
    /// is detected, then the rest is inserted by binary insertion.
    /// </summary>
    private static List<JsonValue> JsSortByLastUsedDescending(List<JsonValue> teams)
    {
        var work = new List<JsonValue>(teams);
        if (work.Count < 2)
        {
            return work;
        }

        static int Compare(JsonValue a, JsonValue b) =>
            string.CompareOrdinal(StringField(a, "last_used"), StringField(b, "last_used")) < 0 ? 1 : -1;

        var n = work.Count;
        var runLength = 2;
        var descending = Compare(work[1], work[0]) < 0;
        for (var idx = 2; idx < n; idx++)
        {
            var order = Compare(work[idx], work[idx - 1]);
            if (descending ? order >= 0 : order < 0)
            {
                break;
            }

            runLength++;
        }

        if (descending)
        {
            work.Reverse(0, runLength);
        }

        for (var start = runLength; start < n; start++)
        {
            var pivot = work[start];
            int left = 0, right = start;
            while (left < right)
            {
                var mid = left + ((right - left) >> 1);
                if (Compare(pivot, work[mid]) < 0)
                {
                    right = mid;
                }
                else
                {
                    left = mid + 1;
                }
            }

            for (var p = start; p > left; p--)
            {
                work[p] = work[p - 1];
            }

            work[left] = pivot;
        }

        return work;
    }
}
