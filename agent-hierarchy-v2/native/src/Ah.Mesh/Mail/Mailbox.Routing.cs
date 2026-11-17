using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Roster;

namespace Ah.Mesh;

internal sealed partial class Mailbox
{
    private const int MaxEntryBytes = 4096;
    private const int MaxNoteChars = 1000;
    private const int MaxEventTextChars = 3000;
    private const string LegacyMessage = "legacy team.json is not addressable; use a named team";

    public bool IsNamedTeamPath(string path) => NodePath.Basename(NodePath.Dirname(path)) == "teams";

    /// <summary>Whether a team is kept at <paramref name="path"/>, as the roster reads it.</summary>
    internal bool TeamExistsAt(string path) => roster.TeamExistsAt(path);

    /// <summary>The team kept at <paramref name="path"/>, as the roster reads it; null JSON when it is no team record.</summary>
    internal JsonValue ReadTeamAt(string path) => roster.ReadTeamFileAt(path);

    public string TeamNameOf(string path)
    {
        var name = NodePath.Basename(path);
        return name.EndsWith(".json", StringComparison.Ordinal) ? name[..^".json".Length] : name;
    }

    /// <summary>A team file read by its own path; null when it is absent or not a record with a members array.</summary>
    private JsonObject? TeamRecord(string path)
    {
        try
        {
            return roster.ReadTeamFileAt(path) is { IsMap: true } v && v.AsObject().Get("members") is { IsArray: true } ? v.AsObject() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> HierarchyDirs(RequestContext context) =>
        new[] { context.HierarchyDirectory, context.MainHierarchyDirectory }.Where(d => !string.IsNullOrEmpty(d));

    /// <summary>
    /// A stamp of every team a caller in <paramref name="context"/> could resolve against: the teams change token when the teams live in
    /// the database, else each team file's stamp. Null when one cannot be read, so it is never trusted.
    /// </summary>
    public string? TeamsStamp(RequestContext context)
    {
        if (roster.TeamsChangeToken() is { } token)
        {
            return "db:" + token.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var stamp = string.Join(";", HierarchyDirs(context).SelectMany(d => roster.ListTeamNames(d).Select(n => n + "=" + roster.TeamStamp(roster.TeamPath(d, n)))));
        return stamp.Contains('?', StringComparison.Ordinal) ? null : stamp;
    }

    public IReadOnlyList<VisibleTeam> VisibleTeams(RequestContext context)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var teams = new List<VisibleTeam>();
        var token = roster.TeamsChangeToken();
        var dbStamp = token is { } t ? "db:" + t.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

        void Add(string path)
        {
            if (!IsNamedTeamPath(path))
            {
                return;
            }

            var (data, key) = CachedTeam(path, dbStamp ?? TeamFileStamp(path));
            if (data is not null && seen.Add(key))
            {
                var pid = data.Get("orchestrator") is { IsMap: true } o ? o.AsObject().Get("pid") : null;
                teams.Add(new VisibleTeam(TeamNameOf(path), path, data, hierarchy.Core.PidAlive(pid ?? JsonValue.Null)));
            }
        }

        foreach (var dir in HierarchyDirs(context))
        {
            var names = dbStamp is null ? roster.ListTeamNames(dir) : _teamNames.TryGetValue(dir, out var held) && held.Stamp == dbStamp ? held.Names : null;
            if (names is null)
            {
                names = roster.ListTeamNames(dir);
                _teamNames[dir] = (dbStamp!, names);
            }

            foreach (var name in names)
            {
                Add(roster.TeamPath(dir, name));
            }
        }

        foreach (var reg in LiveRegistrations())
        {
            foreach (var file in RegistrationTeams.Of(reg))
            {
                Add(file);
            }
        }

        return teams;
    }

    /// <summary>The team names of a hierarchy dir as last listed, by the teams change token they were listed at (database teams only).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Stamp, IReadOnlyList<string> Names)> _teamNames = new(StringComparer.Ordinal);

    /// <summary>The team record of each path as last read, with its dedupe key, by the team stamp it was read at.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Stamp, JsonObject? Data, string Key)> _teamRecords = new(StringComparer.Ordinal);

    /// <summary>
    /// <c>size:mtime:inode</c> of a team file, <c>-</c> when absent, and <c>?</c> (never served from the cache) when it cannot be
    /// stated or was modified in the last 2 s: on a filesystem with 1 s mtimes, two same-size writes in one second stamp alike.
    /// </summary>
    private string TeamFileStamp(string path)
    {
        try
        {
            if (files.Stat(path) is not { } st)
            {
                return "-";
            }

            // the file's mtime is wall time, so it is held to the wall clock, not the injected one
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - st.MtimeMs < 2000
                ? "?"
                : st.Size.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + st.MtimeMs.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ":" + st.Inode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "?";
        }
    }

    /// <summary>The record of the team at <paramref name="path"/> and its dedupe key, read again only when <paramref name="stamp"/> moved.</summary>
    private (JsonObject? Data, string Key) CachedTeam(string path, string stamp)
    {
        if (stamp != "?" && _teamRecords.TryGetValue(path, out var held) && held.Stamp == stamp)
        {
            return (held.Data, held.Key);
        }

        var data = TeamRecord(path);

        // a team the store holds has no file, so resolve the links of the path itself (as the store's team key does), not of a file
        var key = PathGuard.RealPath(path, strict: false);
        if (string.IsNullOrEmpty(key))
        {
            key = path;
        }

        _teamRecords[path] = (stamp, data, key);
        return (data, key);
    }

    /// <summary>
    /// The hierarchy dirs that can hold a team of the session, in order: those of the team files the session's own registration
    /// records, the caller's own (and the main checkout's), then those of every visible team, by real path.
    /// </summary>
    internal List<string> OwnerCandidateDirs(RequestContext context, string? sessionId)
    {
        var dirs = new List<string>();
        var seenDirs = new HashSet<string>(StringComparer.Ordinal);

        void AddDir(string? dir)
        {
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            var key = RealPath(dir);
            if (seenDirs.Add(string.IsNullOrEmpty(key) ? dir : key))
            {
                dirs.Add(dir);
            }
        }

        foreach (var file in RegistrationTeams.Of(ReadRegistration(sessionId)))
        {
            AddDir(RosterModule.TeamFileHome(file));
        }

        foreach (var dir in HierarchyDirs(context))
        {
            AddDir(dir);
        }

        var others = VisibleTeams(context)
            .Select(t => RosterModule.TeamFileHome(t.Path))
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => (Dir: d!, Key: RealPath(d!)))
            .OrderBy(x => string.IsNullOrEmpty(x.Key) ? x.Dir : x.Key, StringComparer.Ordinal);
        foreach (var (dir, _) in others)
        {
            AddDir(dir);
        }

        return dirs;
    }

    /// <summary>
    /// The named teams <paramref name="invoker"/> owns, wherever their repo is, as (team, team file). Candidate hierarchy dirs, in
    /// order: those of the team files the session's own registration records, the caller's own (and the main checkout's), then
    /// those of every visible team, by real path. Ownership is decided only by the roster's own rule. A team name seen in an
    /// earlier dir is not taken again from a later one, so one address <c>orchestrator@&lt;team&gt;</c> names one team file.
    /// </summary>
    internal IReadOnlyList<OwnedTeam> OwnedTeamsAnywhere(RequestContext context, TeamInvoker invoker)
    {
        if (invoker.Pid is null && string.IsNullOrEmpty(invoker.SessionId))
        {
            return [];
        }

        var dirs = OwnerCandidateDirs(context, invoker.SessionId);

        var owned = new List<OwnedTeam>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in dirs)
        {
            foreach (var team in roster.OwnedTeams(dir, invoker))
            {
                if (team is not null && names.Add(team))
                {
                    owned.Add(new OwnedTeam(team, roster.TeamPath(dir, team)));
                }
            }
        }

        return owned;
    }

    public Func<string, MeshTeam?> TeamLookup(RequestContext context)
    {
        var teams = VisibleTeams(context);
        return name =>
        {
            var t = teams.FirstOrDefault(x => x.Team == name);
            return t is null ? null : new MeshTeam(t.Data, RosterModule.TeamFileHome(t.Path));
        };
    }

    private static IEnumerable<JsonObject> Members(JsonValue data) =>
        data.IsMap && data.AsObject().Get("members") is { IsArray: true } list
            ? list.AsArray().Where(m => m.IsMap).Select(m => m.AsObject())
            : [];

    private static bool NamedIs(JsonObject member, string name) => member.Get("name") is { IsText: true } n && n.AsString() == name;

    public IReadOnlyList<MemberMatch> MembersNamed(RequestContext context, string name)
    {
        var found = new List<MemberMatch>();
        foreach (var t in VisibleTeams(context))
        {
            if (Members(t.Data).FirstOrDefault(m => NamedIs(m, name)) is { } m)
            {
                found.Add(new MemberMatch(name, t.Team, m.Get("role") is { IsTruthy: true } r ? JsConvert.ToJsString(r) : null, $"{name}@{t.Team}"));
            }
        }

        return found;
    }

    private static bool Defines(VisibleTeam t, string name) => name is "orchestrator" or "chief" || Members(t.Data).Any(m => NamedIs(m, name));

    private bool LegacyDefines(RequestContext context, string name) =>
        HierarchyDirs(context).Any(dir =>
        {
            var path = roster.TeamPath(dir, null);
            return TeamRecord(path) is { } t && Members(t).Any(m => NamedIs(m, name));
        });

    public MailboxOutcome<ResolvedAddress> ResolveAddress(string? address, RequestContext context)
    {
        try
        {
            return new MailboxOutcome<ResolvedAddress>(Resolve(address, context), null);
        }
        catch (MailboxError e)
        {
            return new MailboxOutcome<ResolvedAddress>(null, e.ToFailure());
        }
    }

    /// <summary>Resolves an address, throwing <see cref="MailboxError"/> when it names nothing.</summary>
    internal ResolvedAddress ResolveCore(string? to, RequestContext context) => Resolve(to, context);

    /// <summary>
    /// <c>role@team</c> when no member of the team has that name: the member name when exactly one member has that role (added to
    /// <paramref name="hits"/>), else the unknown-address refusal that lists the team's member addresses.
    /// </summary>
    private static string ByRole(VisibleTeam team, string role, string address, List<VisibleTeam> hits)
    {
        var named = Members(team.Data).Where(m => m.Get("name") is { IsText: true }).ToList();
        var holders = named.Where(m => m.Get("role") is { IsText: true } r && r.AsString() == role).ToList();
        if (holders.Count == 1)
        {
            hits.Add(team);
            return holders[0].Get("name")!.Value.AsString();
        }

        var message = $"no team defines {JsonText.Quote(address)}";
        if (named.Count > 0)
        {
            message += $"; members of {team.Team}: {string.Join(", ", named.Select(m => $"{m.Get("name")!.Value.AsString()}@{team.Team}"))}";
        }

        throw new MailboxError("unknown_address", message);
    }

    private ResolvedAddress Resolve(string? to, RequestContext context)
    {
        var s = to ?? string.Empty;
        var parts = s.Split('@');
        if (parts.Length > 2 || parts.Any(p => !IsName(p)))
        {
            throw new MailboxError("bad_address", $"address {JsonText.Quote(s)} must be name@team (letters, digits, . _ -)");
        }

        var teams = VisibleTeams(context);
        List<VisibleTeam> hits;
        if (parts.Length == 2)
        {
            var named = teams.Where(t => t.Team == parts[1]).ToList();
            if (named.Count > 1)
            {
                throw new MailboxError(
                    "ambiguous_address",
                    $"team {parts[1]} has {named.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} team files: {string.Join(", ", named.Select(t => t.Path))}",
                    named.Select(t => t.Path).ToList());
            }

            hits = named.Where(t => Defines(t, parts[0])).ToList();
            if (hits.Count == 0 && named.Count == 1)
            {
                parts[0] = ByRole(named[0], parts[0], s, hits);
            }
        }
        else
        {
            hits = teams.Where(t => t.Live && Defines(t, parts[0])).ToList();
            if (hits.Count > 1)
            {
                var addresses = hits.Select(t => $"{parts[0]}@{t.Team}").ToList();
                throw new MailboxError(
                    "ambiguous_address",
                    $"{parts[0]} is defined by {hits.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} live teams: {string.Join(", ", addresses)}",
                    addresses);
            }
        }

        if (hits.Count == 0)
        {
            if (parts.Length == 1 && LegacyDefines(context, parts[0]))
            {
                throw new MailboxError("unknown_address", LegacyMessage);
            }

            throw new MailboxError("unknown_address", $"no team defines {JsonText.Quote(s)}");
        }

        var team = hits[0];
        JsonValue? member = null;
        if (Members(team.Data).FirstOrDefault(m => NamedIs(m, parts[0])) is { } found)
        {
            member = found;
        }

        JsonValue? orchestrator = null;
        if (team.Data.AsObject().Get("orchestrator") is { IsTruthy: true } o)
        {
            orchestrator = o;
        }

        return new ResolvedAddress(
            parts[0],
            team.Team,
            $"{parts[0]}@{team.Team}",
            member,
            orchestrator,
            team.Path,
            RosterModule.TeamFileHome(team.Path),
            BoxDirFor(team.Path, parts[0]));
    }

    // ---------------------------------------------------------------- entries

    private int _counted;

    /// <summary>The long-lived caches of the mailbox in the health census (b459); a count read races a writer at worst by one.</summary>
    private void AddToCensus()
    {
        Ah.Core.CoreModule.CensusAdd(this, "mail.stored_scans", static m => { lock (m._jsonCacheGate) { return m._heldScans.Count; } });
        Ah.Core.CoreModule.CensusAdd(this, "mail.stored_scans.rows", static m => { lock (m._jsonCacheGate) { return m._heldScans.Values.Sum(static v => (long)(v.Scan?.Rows.Count ?? 0)); } });
        // b459 step 9: the attempts kept are the open ladders' rows; a settled entry keeps only its id and newest state
        Ah.Core.CoreModule.CensusAdd(this, "mail.stored_attempts", static m => { lock (m._jsonCacheGate) { return m._storedSweeps.Values.Sum(static v => v.Value.Open.Sum(static e => (long)e.Rows.Count)); } });
        Ah.Core.CoreModule.CensusAdd(this, "mail.stored_attempts.settled_ids", static m => { lock (m._jsonCacheGate) { return m._storedSweeps.Values.Sum(static v => (long)v.Value.Settled.Count); } });
        Ah.Core.CoreModule.CensusAdd(this, "mail.scan_cache", static m => { lock (m._jsonCacheGate) { return m._scanCache.Count; } });
        Ah.Core.CoreModule.CensusAdd(this, "mail.scan_cache.rows", static m => { lock (m._jsonCacheGate) { return m._scanCache.Values.Sum(static v => (long)v.Value.Rows.Count); } });
        Ah.Core.CoreModule.CensusAdd(this, "mail.attempts_cache", static m => { lock (m._jsonCacheGate) { return m._attemptsCache.Values.Sum(static v => (long)v.Value.Count); } });
        Ah.Core.CoreModule.CensusAdd(this, "mail.json_cache", static m => { lock (m._jsonCacheGate) { return m._jsonCache.Count; } }, static m => { lock (m._jsonCacheGate) { return m._jsonCache.Values.Sum(static v => 2L * v.Text.Length); } });
        Ah.Core.CoreModule.CensusAdd(this, "mail.team_records", static m => m._teamRecords.Count);
        Ah.Core.CoreModule.CensusAdd(this, "mail.team_names", static m => m._teamNames.Count);
        Ah.Core.CoreModule.CensusAdd(this, "mail.gone_processes", static m => m._goneProcesses.Count);
    }

    public EntriesResult ReadEntries(string dir, long from = 0)
    {
        if (Interlocked.Exchange(ref _counted, 1) == 0)
        {
            AddToCensus();
        }

        var scan = ScanEntries(dir, from);
        return new EntriesResult(scan.Rows.Select(r => r.Entry).ToList(), scan.Next);
    }

    public EntriesResult Inbox(string dir, InboxOptions? options = null)
    {
        var o = options ?? new InboxOptions();
        var now = o.NowMs ?? NowMs;
        var read = ReadEntries(dir, o.Since);
        JsonValue? heartbeat = null;
        if (ReadHeartbeat(dir) is { } hb)
        {
            heartbeat = hb;
        }

        var limit = _core.PullTimeoutMs(heartbeat);
        var rows = new List<JsonValue>();
        var drained = o.All
            ? []
            : LadderSweep(dir).Settled.Where(static s => DrainedState.IsClosedState(s.Value)).Select(static s => s.Key).ToHashSet(StringComparer.Ordinal);

        // b459 step 15: the box's held ack set, read after the entries, in place of one ack lookup per entry
        IReadOnlySet<string>? heldAcked = !o.All && Stored is { } store ? HeldAcked(store, dir) : null;
        foreach (var e in read.Entries)
        {
            if (!o.All)
            {
                var id = e.AsObject().Get("id")!.Value.AsString();
                if ((heldAcked is not null ? IsName(id) && heldAcked.Contains(id) : IsAcked(dir, id)) || drained.Contains(id))
                {
                    continue;
                }

                if (o.Overdue)
                {
                    var created = ParseMs(e.AsObject().Get("created"));
                    if (!(double.IsFinite(created) && now - created > limit))
                    {
                        continue;
                    }
                }
            }

            rows.Add(e);
        }

        return new EntriesResult(rows, read.Next);
    }

    public IReadOnlyList<JsonValue> Pending(string dir) => Inbox(dir).Entries;

    public string BuildPrompt(string address, IReadOnlyList<JsonValue> entries)
    {
        var list = new JsonArray();
        foreach (var e in entries)
        {
            list.Add(MarkRelayVoid(e));
        }

        var group = new JsonArray();
        group.Add(new JsonObject { ["address"] = address, ["entries"] = list });
        return _core.BuildPrompt(group);
    }

    /// <summary>The `ladder` tag a caller attached to an entry, as <c>renderInbox</c> prints it.</summary>
    private static string LadderTagOf(JsonObject l)
    {
        var rung = l.Get("rung");
        var isFour = rung is { IsNumber: true } r && r.AsNumber() == 4;
        var isThree = rung is { IsNumber: true } r3 && r3.AsNumber() == 3;
        var attempt = isFour && l.Get("attempt") is { IsTruthy: true } a ? $" attempt {JsConvert.ToJsString(a)}/4" : string.Empty;
        var next = l.Get("next_at") is { IsTruthy: true } n ? $" next {JsConvert.ToJsString(n)}" : string.Empty;
        var carrier = isThree && l.Get("carrier") is { IsTruthy: true } c ? $" carrier {JsConvert.ToJsString(c)}" : string.Empty;
        var ended = l.Get("state") is { IsText: true } st && st.AsString() is "superseded" or "declined" or "withdrawn";
        var why = ended && l.Get("reason") is { IsText: true } reason ? $" ({reason.AsString()})" : string.Empty;
        var by = ended && l.Get("by") is { IsText: true } byText ? $" by {byText.AsString()}" : string.Empty;
        var declinedAt = ended && l.Get("state") is { IsText: true } declined && declined.AsString() is "declined" or "withdrawn" && l.Get("at") is { IsText: true } at ? $" at {at.AsString()}" : string.Empty;
        return $"(rung {JsConvert.ToJsString(rung)} {JsConvert.ToJsString(l.Get("state"))}{why}{attempt}{next}{carrier}{by}{declinedAt})";
    }

    public string LadderTag(JsonObject ladder) => LadderTagOf(ladder);

    public bool IsLadderRow(JsonValue row) => row.IsMap && row.AsObject().Get("ladder") is { IsBool: true } l && l.AsBool();

    public IReadOnlyList<JsonObject> LadderRowsOf(string dir, string id) =>
        AttemptsOf(dir, JsonValue.FromString(id)).Where(r => IsLadderRow(r) && r.Get("id") is { IsText: true } rid && rid.AsString() == id).ToList();

    public IReadOnlyList<LadderEntryRows> LadderRowsByEntry(string dir) => ByEntry(ReadAttempts(dir));

    private List<LadderEntryRows> ByEntry(IReadOnlyList<JsonObject> attempts)
    {
        var order = new List<string>();
        var rows = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        foreach (var r in attempts)
        {
            if (IsLadderRow(r) && r.Get("id") is { IsText: true } id)
            {
                if (!rows.TryGetValue(id.AsString(), out var list))
                {
                    order.Add(id.AsString());
                    rows[id.AsString()] = list = [];
                }

                list.Add(r);
            }
        }

        return order.Select(id => new LadderEntryRows(id, rows[id])).ToList();
    }

    public JsonValue MarkRelayVoid(JsonValue entry) => RelayVoid.Mark(this, entry);

    public string? RelayVoidNote(string dir, string entry) => RelayVoid.Check(this, dir, entry) is { } marker ? RelayVoid.Note(marker) : null;

    public JsonObject? EntryStatus(string id, IReadOnlyList<CallerBox> callers) =>
        MsgStatus.Locate(this, id) is { } at && MsgStatus.InScope(this, at, callers.Select(static b => b.Address).ToHashSet(StringComparer.Ordinal)) ? MsgStatus.Of(this, at) : null;

    public bool RecordsReadable(string dir, string id)
    {
        if (Stored is { } store)
        {
            try
            {
                _ = store.BoxStamp(BoxKey(dir));
                return true;
            }
            catch (StoreUnavailableException)
            {
                return false;
            }
        }

        var paths = new List<string> { NodePath.Join(dir, "attempts.jsonl"), NodePath.Join(dir, "entries.jsonl") };
        if (IsName(id))
        {
            paths.Add(AckFile(dir, id));
        }

        foreach (var path in paths.Where(files.Exists))
        {
            try
            {
                _ = files.ReadText(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return true;
    }

    public bool AppendLadderRow(string dir, JsonObject row, long seq)
    {
        var step = new JsonObject();
        foreach (var key in row.Keys)
        {
            step[key] = row[key];
        }

        step["seq"] = seq;
        step["ladder"] = true;
        return Invoke("attempt", new JsonObject { ["dir"] = dir, ["row"] = step, ["seq"] = seq }).Member("written") is { IsTruthy: true };
    }

    public string RenderInbox(IReadOnlyList<JsonValue> entries)
    {
        static string NoLink(string s) => s.Replace("[ah-link", "(ah-link", StringComparison.Ordinal);

        var lines = new List<string>();
        foreach (var v in entries)
        {
            var e = v.AsObject();
            var envelope = e.Get("link") is { IsTruthy: true } link
                ? _core.EnvelopeLine(link.Member("id"), link.Member("from")) + "\n"
                : string.Empty;
            string tail;
            if (e.Get("ref") is { IsTruthy: true } reference)
            {
                tail = MsgPointer.Display(JsConvert.ToJsString(reference));
            }
            else if (e.Get("event") is { IsTruthy: true } ev)
            {
                var text = e.Get("text") is { IsTruthy: true } t ? $": {JsConvert.ToJsString(t)}" : string.Empty;
                tail = JsConvert.ToJsString(ev) + text;
            }
            else
            {
                tail = e.Get("text") is { IsTruthy: true } t2 ? JsConvert.ToJsString(t2) : string.Empty;
            }

            var ladder = e.Get("ladder") is { IsTruthy: true } l && l.IsMap ? $"  {LadderTagOf(l.AsObject())}" : string.Empty;
            if (e.Get("claim") is { IsMap: true } claim)
            {
                ladder += "  " + ClaimTagOf(claim.AsObject());
            }

            lines.Add($"{envelope}{JsConvert.ToJsString(e.Get("id"))}  {JsConvert.ToJsString(e.Get("kind"))}  from {JsConvert.ToJsString(e.Get("from"))}  {JsConvert.ToJsString(e.Get("created"))}  {NoLink(tail)}{ladder}");
        }

        return string.Join("\n", lines);
    }

    public MailboxOutcome<JsonObject> AppendEntry(string dir, JsonObject entry, JsonObject? attempt = null)
    {
        try
        {
            return new MailboxOutcome<JsonObject>(Append(dir, entry, attempt), null);
        }
        catch (MailboxError e)
        {
            return new MailboxOutcome<JsonObject>(null, e.ToFailure());
        }
    }

    /// <summary>The id check, the entry and its routing row run under the box's append lock; the result is <c>{entry, existed}</c>.</summary>
    private JsonObject Append(string dir, JsonObject entry, JsonObject? attempt, TimeSpan? lockWait = null, JsonObject? claim = null)
    {
        var line = JsonText.Stringify(entry, 0) + "\n";
        var bytes = Utf8Text.Encode(line).Length;
        if (bytes > MaxEntryBytes)
        {
            throw new MailboxError("bad_request", $"entry would be {bytes.ToString(System.Globalization.CultureInfo.InvariantCulture)} bytes; the limit is {MaxEntryBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        var args = new JsonObject { ["dir"] = dir, ["entry"] = entry, ["attempt"] = attempt is null ? JsonValue.Null : attempt, ["hold_ms"] = 0 };
        if (claim is not null)
        {
            args["claim"] = claim;
        }

        var outcome = Invoke("append", args, lockWait);
        var existed = outcome.Member("existed") is { IsTruthy: true };
        if (!existed)
        {
            return new JsonObject { ["entry"] = entry, ["existed"] = false };
        }

        if (outcome.Member("same") is { IsText: true } sameId)
        {
            var original = ReadEntries(dir).Entries.FirstOrDefault(e => StrictEquals(e.AsObject().Get("id"), sameId));
            return new JsonObject { ["entry"] = original.IsMap ? original : entry, ["existed"] = true, ["same"] = true };
        }

        var id = entry.Get("id");
        var stored = ReadEntries(dir).Entries.FirstOrDefault(e => StrictEquals(e.AsObject().Get("id"), id));
        return new JsonObject { ["entry"] = stored.IsMap ? stored : entry, ["existed"] = true };
    }

    /// <summary>Record that a <c>star-warn</c> send would have been refused. The row is evidence only; a failed write does not stop the send.</summary>
    public void LogWouldRefuse(string dir, string id, string? would)
    {
        try
        {
            var row = new JsonObject { ["id"] = id, ["rung"] = 0, ["state"] = "would_refuse" };
            if (would is not null)
            {
                row["would"] = would;
            }

            AppendAttempt(dir, row);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // evidence only
        }
    }

    /// <summary>Record that a member's SendMessage named a recipient no team, registration or peer record could place.</summary>
    public void LogUnclassified(string dir, string id, JsonObject detail)
    {
        try
        {
            var row = new JsonObject { ["id"] = id, ["rung"] = 0, ["state"] = "unclassified_recipient" };
            foreach (var key in detail.Keys)
            {
                row[key] = detail[key];
            }

            AppendAttempt(dir, row);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // evidence only
        }
    }
}
