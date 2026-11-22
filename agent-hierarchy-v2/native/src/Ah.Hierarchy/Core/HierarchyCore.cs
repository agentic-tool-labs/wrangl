using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Hierarchy;

internal sealed class HierarchyCore(IFileReader files, IAhEnvironment environment, IClock clock, IRandom random, IProcessInspector processes, PeerRecordsRoute? peerRoute = null, IHookErrorLog? errors = null, MsgsRoute? msgsRoute = null, Ah.Roster.IRosterCore? roster = null) : IHierarchyCore
{
    private const string PeerRecordsDomain = "peer-records";
    private const string MsgsDomain = "msgs";

    private bool _msgsUnavailableLogged;

    /// <summary>
    /// The message source. A database that cannot be read throws <see cref="StoreUnavailableException"/>, logged once until it reads again, and
    /// never answers empty or falls back to listing files (ah-msgs-db-only §7); so does a home still marked <c>mode.msgs = files</c>, which
    /// has one way forward, the cutover. The caller disposes the scope; all its reads share one connection. Inside a
    /// <see cref="ShareMsgReads"/> on this thread it lends the share's connection, and disposing it leaves that open.
    /// </summary>
    internal IMsgScope DbMsgs()
    {
        if (Interlocked.Exchange(ref _counted, 1) == 0)
        {
            AddToCensus();
        }

        if (_msgReadShares.GetValueOrDefault(Environment.CurrentManagedThreadId) is not { } share)
        {
            return OpenMsgs();
        }

        if (!share.Opened)
        {
            share.Db = OpenMsgs();
            share.Opened = true;
        }

        return new GuardedMsgs(share.Db!, this, lent: true);
    }

    private GuardedMsgs OpenMsgs()
    {
        if (msgsRoute is null)
        {
            throw new InvalidOperationException("this hierarchy core has no message store route");
        }

        try
        {
            if (MsgsNotReady() is { } why)
            {
                throw new StoreUnavailableException(why);
            }

            return new GuardedMsgs(msgsRoute.Source.Scope(), this);
        }
        catch (Exception error) when (IsStoreFailure(error))
        {
            throw MsgsUnavailable(error);
        }
    }

    /// <summary>What a home still marked <c>mode.msgs = files</c> is told: its one way forward.</summary>
    internal const string StillOnFilesMessage = "this home is still on message files (mode.msgs = files), which this build no longer reads; restore a backup from `ah db list` taken on the database store";

    /// <summary>What the mode reader answers during a daemon start's import of the message files.</summary>
    private const string MsgsImporting = "importing";

    /// <summary>What a reader or writer is told during that import.</summary>
    internal const string ImportingMessage = "the message store is unavailable: ahd is importing this home's message files; try again in a moment";

    private long _msgsReadFailures;

    /// <summary>How many message reads have failed on the store.</summary>
    internal long MsgsReadFailures => Interlocked.Read(ref _msgsReadFailures);

    private StoreUnavailableException MsgsUnavailable(Exception error)
    {
        Interlocked.Increment(ref _msgsReadFailures);
        if (!Interlocked.Exchange(ref _msgsUnavailableLogged, true))
        {
            errors?.Log("msgs-read", $"unavailable reason={error.Message}");
        }

        return new StoreUnavailableException(error.Message, error);
    }

    /// <summary>The source with a read that fails on the store throwing <see cref="StoreUnavailableException"/>, logged once until a read succeeds again.</summary>
    private sealed class GuardedMsgs(IMsgScope inner, HierarchyCore owner, bool lent = false) : IMsgScope
    {
        public IMsgScope Scope() => this;

        public void Dispose()
        {
            if (!lent)
            {
                inner.Dispose();
            }
        }

        private T Guard<T>(Func<T> read)
        {
            try
            {
                var value = read();
                Volatile.Write(ref owner._msgsUnavailableLogged, false);
                return value;
            }
            catch (Exception error) when (IsStoreFailure(error))
            {
                throw owner.MsgsUnavailable(error);
            }
        }

        public IReadOnlyList<MsgExchangePair> Exchanges(IReadOnlyList<string> poolDirs) => Guard(() => inner.Exchanges(poolDirs));

        public IReadOnlyList<MsgFileRow> Messages(string poolDir, string? id = null, string? type = null) => Guard(() => inner.Messages(poolDir, id, type));

        public IReadOnlyList<string> MessageNames(string poolDir) => Guard(() => inner.MessageNames(poolDir));

        public IReadOnlyList<MsgFileRow> ArchivedMessages(string poolDir) => Guard(() => inner.ArchivedMessages(poolDir));

        public IReadOnlyList<MsgFileRow> MessagesById(string id, string type) => Guard(() => inner.MessagesById(id, type));

        public IReadOnlyList<MsgFileRow> LiveMessages(string poolDir, long answeredSinceMs) => Guard(() => inner.LiveMessages(poolDir, answeredSinceMs));

        public IReadOnlyList<MsgFileRow>? RequestsOpenedSince(string poolDir, string recipient, string slug, long openedSinceMs) =>
            Guard(() => inner.RequestsOpenedSince(poolDir, recipient, slug, openedSinceMs));

        public IReadOnlyList<string> RequestIdsWithSlug(string poolDir, string slug) => Guard(() => inner.RequestIdsWithSlug(poolDir, slug));

        public IReadOnlyList<(string Id, string Slug)> ExchangeSlugs(string poolDir) => Guard(() => inner.ExchangeSlugs(poolDir));

        public IReadOnlyList<string> RunSlugs(string poolDir, string? team, long openedSinceMs) => Guard(() => inner.RunSlugs(poolDir, team, openedSinceMs));

        public IReadOnlyList<string> CharterCandidateIds(string poolDir, string team, IReadOnlyList<string> tos, long openedSinceMs) =>
            Guard(() => inner.CharterCandidateIds(poolDir, team, tos, openedSinceMs));

        public bool AnyAnswered(string poolDir, string recipient) => Guard(() => inner.AnyAnswered(poolDir, recipient));

        public IReadOnlyList<MsgFileRow> MessagesWithIds(string poolDir, IReadOnlyList<string> ids) => Guard(() => inner.MessagesWithIds(poolDir, ids));

        public IReadOnlyList<(string Id, string To, string? Team, string Pool)> ExchangeRowsBefore(
            IReadOnlyList<string> poolDirs, string? beforeId, int count, IReadOnlyList<string>? teams, string? recipient) =>
            Guard(() => inner.ExchangeRowsBefore(poolDirs, beforeId, count, teams, recipient));

        public long ExchangeCount(IReadOnlyList<string> poolDirs, IReadOnlyList<string>? teams, string? recipient) => Guard(() => inner.ExchangeCount(poolDirs, teams, recipient));

        public IReadOnlyList<string> RequestDescendants(string poolDir, IReadOnlyList<string> ids) => Guard(() => inner.RequestDescendants(poolDir, ids));

        public IReadOnlyDictionary<string, string?> TeamsByPath(IReadOnlyList<string> paths) => Guard(() => inner.TeamsByPath(paths));

        public MsgFileRow? ByPath(string path) => Guard(() => inner.ByPath(path));

        public IReadOnlyList<MsgFileRow>? MessagesWithBodies(string poolDir, string id) => Guard(() => inner.MessagesWithBodies(poolDir, id));

        public long Version() => Guard(inner.Version);

        public IReadOnlyList<string> ChangedIds(string poolDir, long sinceVersion) => Guard(() => inner.ChangedIds(poolDir, sinceVersion));

        public long MessageCount(string poolDir) => Guard(() => inner.MessageCount(poolDir));
    }

    private bool MsgsMarkerSaysDb()
    {
        try
        {
            return msgsRoute?.MarkerSaysDb?.Invoke() ?? true;
        }
        catch (Exception error) when (IsStoreFailure(error))
        {
            return true;
        }
    }

    /// <summary>The text of a message: its stored body; no file is ever read.</summary>
    internal static string ReadMsgText(string path, IMsgSource db) =>
        db.ByPath(path) is { Bytes: { } stored }
            ? System.Text.Encoding.UTF8.GetString(stored)
            : throw new FileNotFoundException($"no message {path} in the store", path);

    /// <summary>Whether messages live in the store here, so a message path names no file. A mode that cannot be read counts as yes: a store trouble never opens the files.</summary>
    public bool MessagesInDb()
    {
        try
        {
            return !StillOnFiles();
        }
        catch (Exception error) when (IsStoreFailure(error))
        {
            throw MsgsUnavailable(error);
        }
    }

    private bool StillOnFiles() => MsgsNotReady() is not null;

    /// <summary>Why messages cannot be served yet, or null when they can. The import's window holds whatever the marker says: a home that took a write has one.</summary>
    internal string? MsgsNotReady()
    {
        var mode = msgsRoute!.Modes.Read(MsgsDomain);
        return mode == MsgsImporting ? ImportingMessage : mode != "db" && !MsgsMarkerSaysDb() ? StillOnFilesMessage : null;
    }

    /// <summary>
    /// Whether the peer records are in <c>db</c> mode. A mode that cannot be read is not <c>db</c> here: the write engine re-reads the
    /// mode under its own rules and is the one that refuses.
    /// </summary>
    public bool PeerRecordsInDb()
    {
        try
        {
            return peerRoute is not null && peerRoute.Modes.Read(PeerRecordsDomain) == "db";
        }
        catch (Exception error) when (IsStoreFailure(error))
        {
            return false;
        }
    }

    private readonly object _unavailableGate = new();
    private bool _unavailableLogged;

    /// <summary>
    /// The records from the database when the peer-records domain is in <c>db</c> mode, else null and the file is in force. In
    /// <c>db</c> mode the peer-log file is frozen, so a read from it would be confidently stale: a database that cannot be read
    /// answers an empty log, logged once until it reads again, and never falls back to the file. The file's parse cache is not
    /// consulted in <c>db</c> mode and is keyed on the file's stat, so a file rewritten by a cutback is parsed again.
    /// </summary>
    private IReadOnlyList<JsonValue>? DbPeerRecords() => DbPeerRecords(_heldPeerRows.Read, out _);

    private readonly HeldPeerRows _heldPeerRows = new();

    /// <summary><see cref="DbPeerRecords()"/> that also says whether the database could not be read (the empty answer is then not a real one).</summary>
    private IReadOnlyList<JsonValue>? DbPeerRecords(out bool failed) => DbPeerRecords(_heldPeerRows.Read, out failed);

    /// <summary><see cref="DbPeerRecords()"/> with the read that <paramref name="read"/> makes of the store, so every read shares the one mode and failure handling.</summary>
    private IReadOnlyList<JsonValue>? DbPeerRecords(Func<IPeerRecordSource, IReadOnlyList<JsonValue>> read) => DbPeerRecords(read, out _);

    private IReadOnlyList<JsonValue>? DbPeerRecords(Func<IPeerRecordSource, IReadOnlyList<JsonValue>> read, out bool failed)
    {
        failed = false;
        if (peerRoute is null)
        {
            return null;
        }

        try
        {
            string mode;
            try
            {
                mode = peerRoute.Modes.Read(PeerRecordsDomain);
            }
            catch (Exception error) when (IsStoreFailure(error))
            {
                // The mode lives in the database that cannot be read. Only the authority marker can say the domain was never flipped.
                if (!MarkerSaysDb())
                {
                    return null;
                }

                throw;
            }

            if (mode != "db")
            {
                return null;
            }

            var rows = read(peerRoute.Source);
            lock (_unavailableGate)
            {
                _unavailableLogged = false;
            }

            return rows;
        }
        catch (Exception error) when (IsStoreFailure(error))
        {
            failed = true;
            lock (_unavailableGate)
            {
                if (!_unavailableLogged)
                {
                    _unavailableLogged = true;
                    errors?.Log("peer-read", $"unavailable reason={error.Message}");
                }
            }

            ThrowIfDeciding(error);
            return [];
        }
    }

    private static readonly AsyncLocal<int> s_decisionDepth = new();

    // The depth is per async flow, so the scope must be disposed in the same synchronous flow that opened it, never across an await.
    public IDisposable DecisionReads()
    {
        s_decisionDepth.Value++;
        return new DecisionScope();
    }

    /// <summary>Whether the current flow is inside <see cref="DecisionReads"/>.</summary>
    private static bool InDecisionReads => s_decisionDepth.Value > 0;

    /// <summary>Called where a failed store read is about to answer empty: inside a decision scope it throws instead.</summary>
    private static void ThrowIfDeciding(Exception error)
    {
        if (InDecisionReads)
        {
            throw new StoreUnavailableException(error.Message, error);
        }
    }

    private sealed class DecisionScope : IDisposable
    {
        private bool _left;

        public void Dispose()
        {
            if (!_left)
            {
                _left = true;
                s_decisionDepth.Value--;
            }
        }
    }

    /// <summary>The ways a store that cannot be opened or read fails; any other exception is a defect and propagates.</summary>
    private static bool IsStoreFailure(Exception error) => error is InvalidOperationException or IOException or UnauthorizedAccessException;

    private bool MarkerSaysDb()
    {
        try
        {
            return peerRoute?.MarkerSaysDb?.Invoke() ?? true;
        }
        catch (Exception error) when (IsStoreFailure(error))
        {
            return true;
        }
    }

    /// <summary>A response over this many bytes holds a report: no skeleton is that long, so it is never read to tell.</summary>
    private const long ReportBytes = 4096;

    public MsgFilenameParts? ParseMsgFilename(string name) => MessageText.ParseFilename(name);

    public string? ExtractMsgToken(string? text, string? contextDir = null, string? expectedTo = null) => ResolveMsgToken(text, contextDir, expectedTo)?.Key;

    public IReadOnlyList<string> ExtractMsgTokens(string? text, string? contextDir = null, string? expectedTo = null) =>
        [.. MessageText.TokenParts(text).Select(t => Resolve(t.Word, t.Type, contextDir, expectedTo).Key).OfType<string>()];

    public MsgTokenHit? ResolveMsgToken(string? text, string? contextDir = null, string? expectedTo = null) =>
        MessageText.TokenParts(text) is [var first, ..] ? Resolve(first.Word, first.Type, contextDir, expectedTo) : null;

    public MsgTokenHit ResolveMsgId(string id, string type, string? contextDir, string? pool, string? expectedTo)
    {
        var token = $"[hierarchy-msg {id} {type}]";
        var hit = ResolveMsgToken(token, pool ?? contextDir, expectedTo)!;
        return hit is { Key: null, Candidates.Count: > 1 } && pool is null ? ResolveMsgToken(token, null, expectedTo)! : hit;
    }

    /// <summary>
    /// A token word as its key: a legacy path is the key; an id is looked up by (id, type) across every pool, and more than one row is
    /// narrowed to <paramref name="contextDir"/>'s pool, then to <paramref name="expectedTo"/>. Not exactly one row left is unresolved,
    /// with every row as a candidate. A store failure throws.
    /// </summary>
    private MsgTokenHit Resolve(string word, string type, string? contextDir, string? expectedTo)
    {
        if (!MessageText.IsId(word))
        {
            return new(word, type, word, []);
        }

        using var db = DbMsgs();
        var all = db.MessagesById(word, type);
        var rows = all;
        if (rows.Count > 1 && contextDir is not null)
        {
            var pool = MsgRows.PoolKey(contextDir);
            rows = [.. rows.Where(r => r.PoolDir == pool)];
        }

        if (rows.Count > 1 && expectedTo is not null)
        {
            rows = [.. rows.Where(r => r.To == expectedTo || r.ToName == expectedTo)];
        }

        return rows.Count == 1 ? new(word, type, rows[0].Path, []) : new(word, type, null, [.. all.Select(static r => (r.To, r.PoolDir))]);
    }

    public string NewId() => Ids.NewId(clock, random);

    public bool PidAlive(JsonValue pid) =>
        pid.IsNumber && pid.AsNumber() is var n && n == Math.Floor(n) && n > 0 && n <= int.MaxValue && processes.PidAlive((int)n);

    public IMemberLiveness? Liveness { get; set; }

    public bool IdentityLive(JsonValue pid, JsonValue? recordedStart)
    {
        if (Liveness is not { } liveness)
        {
            return recordedStart is not { IsText: true } recorded || recorded.AsString().Length == 0 ? PidAlive(pid) : false;
        }

        return pid.IsNumber && pid.AsNumber() is var n && n == Math.Floor(n) && n > 0 && n <= int.MaxValue
            && liveness.PidIdentityLive((int)n, recordedStart is { IsText: true } start && start.AsString().Length > 0 ? start.AsString() : null);
    }

    public bool PidRunning(JsonValue pid, JsonValue? recordedStart, Ah.Core.Time.LivenessPolicy policy, bool claudeHarness = true)
    {
        if (Liveness is not { } liveness)
        {
            return PidAlive(pid);
        }

        return pid.IsNumber && pid.AsNumber() is var n && n == Math.Floor(n) && n > 0 && n <= int.MaxValue
            && liveness.PidRunning((int)n, recordedStart is { IsText: true } start && start.AsString().Length > 0 ? start.AsString() : null, policy, claudeHarness);
    }

    public JsonValue ReadMsgFile(string path) => ReadMsgFiles([path])[0];

    /// <summary><see cref="ReadMsgFile(string)"/> for each path, over one connection: opening one per message cost more than the read.</summary>
    public IReadOnlyList<JsonValue> ReadMsgFiles(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        IMsgScope db;
        try
        {
            db = DbMsgs();
        }
        catch (Exception error) when (error is not StoreUnavailableException)
        {
            return [.. paths.Select(_ => JsonValue.Null)];
        }

        using (db)
        {
            return [.. paths.Select(path => ReadMsgFile(path, db))];
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, MsgReadShare> _msgReadShares = new();

    /// <summary>Until disposed, message reads on this thread share one connection, opened by the first read. An inner call shares the outer one and returns null.</summary>
    public IDisposable? ShareMsgReads()
    {
        var thread = Environment.CurrentManagedThreadId;
        var share = new MsgReadShare(this, thread);
        return _msgReadShares.TryAdd(thread, share) ? share : null;
    }

    /// <summary>
    /// The stored text of a message. Inside a share it reads over the share's connection, and a text read while the store's <c>msgs</c>
    /// counter is unchanged is not read again: a watch tick re-read the same messages every pass. Outside a share it opens its own.
    /// </summary>
    internal string ReadMsgTextShared(string path)
    {
        if (_msgReadShares.GetValueOrDefault(Environment.CurrentManagedThreadId) is not { } share)
        {
            using var own = DbMsgs();
            return ReadMsgText(path, own);
        }

        if (!share.Opened)
        {
            share.Db = OpenMsgs();
            share.Opened = true;
        }

        // the counter is read once per share; a text read after it reflects that version or a later one, so it is served only while the counter stays put
        share.Version ??= share.Db!.Version();
        var memo = Volatile.Read(ref _texts);
        if (share.Version >= 0 && memo.Version != share.Version)
        {
            memo = new TextMemo(share.Version.Value, new(StringComparer.Ordinal));
            Volatile.Write(ref _texts, memo);
        }

        if (share.Version >= 0 && memo.Texts.TryGetValue(path, out var known))
        {
            return known;
        }

        var text = ReadMsgText(path, share.Db!);
        if (share.Version >= 0 && memo.Version == share.Version)
        {
            memo.Texts[path] = text;
        }

        return text;
    }

    // ponytail: holds every text read since the counter last moved, at most the store's messages; a size cap if a pool grows past memory
    private sealed record TextMemo(long Version, System.Collections.Concurrent.ConcurrentDictionary<string, string> Texts);

    private TextMemo _texts = new(-1, new(StringComparer.Ordinal));

    private int _counted;

    /// <summary>The long-lived caches of this core in the health census (b459): held listings, message texts, parsed peer rows.</summary>
    private void AddToCensus()
    {
        static long Locked<T>(HierarchyCore c, Func<HierarchyCore, T> read, Func<T, long> count)
        {
            lock (c._listingsGate)
            {
                return count(read(c));
            }
        }

        Ah.Core.CoreModule.CensusAdd(this, "hierarchy.listings", static c => Locked(c, static c => c._listings, static l => l.Count));
        Ah.Core.CoreModule.CensusAdd(this, "hierarchy.listings.exchanges", static c => Locked(c, static c => c._listings, static l => l.Values.Sum(static v => (long)v.Listing.Count)));
        Ah.Core.CoreModule.CensusAdd(this, "hierarchy.listings.rebuilds", static c => Interlocked.Read(ref c._listingRebuilds));
        Ah.Core.CoreModule.CensusAdd(
            this, "hierarchy.msg_texts", static c => Volatile.Read(ref c._texts).Texts.Count, static c => Volatile.Read(ref c._texts).Texts.Values.Sum(static t => 2L * t.Length));
        Ah.Core.CoreModule.CensusAdd(this, "hierarchy.peer_parsed", static c => Volatile.Read(ref c._peerParsed)?.Count ?? 0);
    }

    private sealed class MsgReadShare(HierarchyCore owner, int thread) : IDisposable
    {
        internal GuardedMsgs? Db { get; set; }

        internal bool Opened { get; set; }

        internal long? Version { get; set; }

        public void Dispose()
        {
            Db?.Dispose();
            owner._msgReadShares.TryRemove(thread, out _);
        }
    }

    internal static JsonValue ReadMsgFile(string path, IMsgSource db)
    {
        string text;
        try
        {
            text = ReadMsgText(path, db);
        }
        catch (Exception error) when (error is not StoreUnavailableException)
        {
            return JsonValue.Null;
        }

        var fm = MessageText.ParseFrontmatter(text);
        return new JsonObject
        {
            ["path"] = path,
            ["text"] = text,
            ["fm"] = fm is null ? JsonValue.Null : fm.Value.Fields,
            ["anchors"] = MessageText.IndexAnchors(text),
        };
    }

    /// <summary>
    /// The newest stored body of a message, sealed or draft; null for a draft with no body yet or a path with no row.
    /// A store that cannot be read throws.
    /// </summary>
    /// <param name="path">The message file's path.</param>
    /// <returns>The stored bytes, or null.</returns>
    public byte[]? StoredBytes(string path)
    {
        using var db = DbMsgs();
        return db.ByPath(path)?.Bytes;
    }

    /// <summary>
    /// The bytes a message hashes to: its stored body; null when there are none. A store that cannot be read inside a decision scope is not swallowed.
    /// </summary>
    /// <param name="path">The message file's path.</param>
    /// <returns>The bytes, or null.</returns>
    public byte[]? MsgBytes(string path)
    {
        try
        {
            using var db = DbMsgs();
            return db.ByPath(path)?.Bytes;
        }
        catch (Exception error) when (error is not StoreUnavailableException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a message has a row. A store that cannot be read inside a decision scope throws.
    /// </summary>
    /// <param name="path">The message file's path.</param>
    /// <returns>True when the message exists.</returns>
    public bool MsgExists(string path)
    {
        using var db = DbMsgs();
        return db.ByPath(path) is not null;
    }

    /// <summary>
    /// When a message was last written: its newest revision's time; null when that is not known. A store that cannot be read inside a
    /// decision scope throws.
    /// </summary>
    /// <param name="path">The message file's path.</param>
    /// <returns>Unix milliseconds, or null.</returns>
    public double? MsgTimeMs(string path)
    {
        using var db = DbMsgs();
        return db.ByPath(path)?.SealedAtMs;
    }

    /// <summary>Each message's stored text and <see cref="MsgTimeMs"/> from one read, over one connection: read apart, the time was 2.5 s of a 5.7 s status rebuild (2850 responses).</summary>
    public IReadOnlyList<(string? Text, double? TimeMs)> ReadMsgTextsAndTimes(IReadOnlyList<string> paths)
    {
        using var db = DbMsgs();
        return [.. paths.Select(path => ReadMsgTextAndTime(path, db))];
    }

    private static (string? Text, double? TimeMs) ReadMsgTextAndTime(string path, IMsgSource db)
    {
        try
        {
            return db.ByPath(path) is { } row ? (row.Bytes is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null, row.SealedAtMs) : (null, null);
        }
        catch (Exception error) when (error is not StoreUnavailableException)
        {
            return (null, null);
        }
    }

    public JsonArray ListExchanges(string dir) => ListExchanges([dir]);

    public JsonArray ListExchanges(IReadOnlyList<string> dirs) => ListExchanges(dirs, live: false);

    /// <summary>
    /// How far back a sealed response keeps its exchange in the live scope: the ten-minute "reported" window of the dispatch list, plus a
    /// minute because that window keys on the export's mtime, which is never before the seal.
    /// </summary>
    internal const long LiveAnsweredWindowMs = 11 * 60 * 1000;

    /// <summary>
    /// <see cref="ListExchanges(IReadOnlyList{string})"/>; with <paramref name="live"/> in db mode, only the exchanges that can still be open
    /// or were answered within <see cref="LiveAnsweredWindowMs"/>, so every exchange that comes back open is one the full list has open too.
    /// </summary>
    public JsonArray ListExchanges(IReadOnlyList<string> dirs, bool live) => live && Ah.Core.Files.ListingScope.IsOpen
        ? CopyOf(Ah.Core.Files.ListingScope.Once("live-exchanges\0" + string.Join('\0', dirs), () => ListExchanges(dirs, live, null)))
        : live ? ListExchanges(dirs, live, null) : FullListing(dirs);

    /// <summary>How many pool lists keep their full listing: each can hold thousands of exchanges (b459 step 16: one per live session, with room).</summary>
    private const int ListingsCap = 16;

    private readonly object _listingsGate = new();
    private readonly Dictionary<string, HeldListing> _listings = new(StringComparer.Ordinal);

    /// <summary>A held full listing with what it was listed from: each pool's rows, in the order of the pool list, and the open charters (b459 step 28).</summary>
    private sealed record HeldListing(string Version, JsonArray Listing, long Used, List<JsonObject>[] Rows, IReadOnlySet<string> Charters);

    /// <summary>The use counter of <see cref="_listings"/>: the entry with the lowest is the one evicted.</summary>
    private long _listingUses;

    /// <summary>Drops every held listing, so the next read of each pool list lists it again (the on-demand rebuild).</summary>
    public void ForgetListings()
    {
        lock (_listingsGate)
        {
            _listings.Clear();
            _poolRows.Clear();
        }
    }

    /// <summary>Each pool's message rows as the full listing reads them, with the message-store version they were read at. Guarded by <see cref="_listingsGate"/>.</summary>
    private readonly Dictionary<string, (long Version, long Count, List<JsonObject> Rows)> _poolRows = new(StringComparer.Ordinal);

    /// <summary>How many pools' rows were read rather than kept, for the tests of the per-pool hold.</summary>
    internal long PoolRowReads => Interlocked.Read(ref _poolRowReads);

    private long _poolRowReads;

    /// <summary>
    /// The message rows of pool <paramref name="dir"/> (b459 step 25a): kept across message-store versions while no message of the pool
    /// changed since, so a write in one pool re-reads that pool alone. <paramref name="version"/> is read before the rows: a write between
    /// the two leaves the rows on the older version, and the next read finds the write's id changed and reads the pool again.
    /// A delete leaves no changed id, so the pool's message count must also be unchanged (Amendment 3); it too is read before the rows.
    /// </summary>
    private List<JsonObject> HeldRequestFiles(string dir, IMsgSource db, long version)
    {
        var key = MsgRows.PoolKey(dir);
        (long Version, long Count, List<JsonObject> Rows) held;
        lock (_listingsGate)
        {
            _ = _poolRows.TryGetValue(key, out held);
        }

        if (held.Rows is not null && held.Version == version)
        {
            return held.Rows;
        }

        var count = db.MessageCount(dir);
        var changed = held.Rows is not null && held.Version < version ? db.ChangedIds(dir, held.Version) : null;
        if (changed is { Count: 0 } && held.Count == count)
        {
            lock (_listingsGate)
            {
                _poolRows[key] = (version, count, held.Rows!);
            }

            return held.Rows!;
        }

        _ = Interlocked.Increment(ref _poolRowReads);
        var rows = RequestFiles(dir, db);

        // b459 step 25c: a message whose id did not change keeps its held row, and with it the facts held on that row (NoReportOf)
        if (changed is not null)
        {
            var moved = changed.ToHashSet(StringComparer.Ordinal);
            var kept = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var row in held.Rows!)
            {
                _ = kept.TryAdd(row["path"].AsString(), row);
            }

            for (var i = 0; i < rows.Count; i++)
            {
                if (!moved.Contains(rows[i]["meta"].AsObject()["id"].AsString()) && kept.TryGetValue(rows[i]["path"].AsString(), out var same))
                {
                    rows[i] = same;
                }
            }
        }
        lock (_listingsGate)
        {
            _poolRows[key] = (version, count, rows);
        }

        return rows;
    }

    /// <summary>How many full listings were built rather than answered from <see cref="_listings"/>: the census shows what the cap costs.</summary>
    private long _listingRebuilds;

    /// <summary>The count of <see cref="_listingRebuilds"/>, for the tests of the cap.</summary>
    internal long ListingRebuilds => Interlocked.Read(ref _listingRebuilds);

    /// <summary>
    /// The full listing of <paramref name="dirs"/>, listed again only when the message store or the team records changed since (b459: a
    /// send listed every exchange of the pools once per brief of the sender's box, a tick once per link and event). Each caller gets its
    /// own copy. Without both change tokens (team files on disk, a store that cannot say) it is listed every time.
    /// </summary>
    // b459 step 16: up to ListingsCap pool lists, the least recently used one evicted when a new one would pass it (the clear-all thrashed
    // with more live sessions than the cap; per-pool listings cannot compose, since a request and its response may sit in different pools)
    private JsonArray FullListing(IReadOnlyList<string> dirs)
    {
        // read before the listing, so a write that lands while it runs leaves an older version held and the next read lists again
        var version = ListingVersion();
        if (version is null)
        {
            _ = Interlocked.Increment(ref _listingRebuilds);
            return ListExchanges(dirs, false, null);
        }

        var key = string.Join('\0', dirs);
        HeldListing? prior = null;
        lock (_listingsGate)
        {
            if (_listings.TryGetValue(key, out var held))
            {
                if (held.Version == version)
                {
                    _listings[key] = held with { Used = ++_listingUses };
                    return CopyOf(held.Listing);
                }

                prior = held;
            }
        }

        _ = Interlocked.Increment(ref _listingRebuilds);

        // the team records' token is the part after the store version: lanes read from them, so a moved token lists afresh
        var (listing, rows, charters) = ListingFrom(dirs, prior is not null && TeamsPart(prior.Version) == TeamsPart(version) ? prior : null);
        lock (_listingsGate)
        {
            if (!_listings.ContainsKey(key) && _listings.Count >= ListingsCap)
            {
                _ = _listings.Remove(_listings.MinBy(static l => l.Value.Used).Key);
            }

            _listings[key] = new HeldListing(version, listing, ++_listingUses, rows, charters);
        }

        return CopyOf(listing);
    }

    private static string TeamsPart(string version) => version[(version.IndexOf(':', StringComparison.Ordinal) + 1)..];

    /// <summary>How many exchanges a listing update listed again rather than kept, for the tests of the update.</summary>
    internal long ListingRelistedIds => Interlocked.Read(ref _listingRelistedIds);

    private long _listingRelistedIds;

    /// <summary>
    /// The full listing of <paramref name="dirs"/> (b459 step 28). With a <paramref name="prior"/> listing it is an update: a pool row that
    /// is not the very row the prior listing read (a pool's held rows keep the rows of unchanged messages, step 25c) marks its id changed,
    /// as does a row the prior listing read that is gone; those ids, and any whose open-charter mark moved, are paired and listed again,
    /// and every other exchange is the prior listing's. The result equals a fresh listing.
    /// </summary>
    private (JsonArray Listing, List<JsonObject>[] Rows, IReadOnlySet<string> Charters) ListingFrom(IReadOnlyList<string> dirs, HeldListing? prior)
    {
        using var db = DbMsgs();
        var version = db.Version();
        var rows = new List<JsonObject>[dirs.Count];
        var byDir = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        for (var i = 0; i < dirs.Count; i++)
        {
            rows[i] = HeldRequestFiles(dirs[i], db, version);
            _ = byDir.TryAdd(dirs[i], rows[i]);
        }

        bool NoReport(JsonObject e) => NoReportOf(e["response"].AsObject(), e["id"].AsString(), db);
        IReadOnlySet<string> Charters(bool mayHaveCharter) => mayHaveCharter ? OpenCharterIdsOf(dirs, db, freshLanes: true) : new HashSet<string>(StringComparer.Ordinal);
        if (prior is null || prior.Rows.Length != dirs.Count)
        {
            var withRequest = WithRequestNewestFirst(Paired(dirs, d => byDir[d]));
            var all = Charters(AnswersASubOrchestrator(withRequest));
            return (Listed(withRequest, all, NoReport), rows, all);
        }

        var changed = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < dirs.Count; i++)
        {
            if (!ReferenceEquals(prior.Rows[i], rows[i]))
            {
                ChangedIdsOf(prior.Rows[i], rows[i], changed);
            }
        }

        static string IdOf(JsonValue e) => e.AsObject()["id"].AsString();
        List<JsonObject> Redone() => WithRequestNewestFirst(Paired(dirs, d => byDir[d].Where(r => changed.Contains(r["meta"].AsObject()["id"].AsString()))));
        List<JsonValue> Kept() => [.. prior.Listing.Where(e => !changed.Contains(IdOf(e)))];
        var redone = Redone();
        var kept = Kept();
        var charters = Charters(AnswersASubOrchestrator(redone)
            || kept.Any(static e => e.AsObject()["response"].IsMap && e.AsObject()["to"].AsString() == "sub-orchestrator"));
        var flipped = kept.Select(IdOf).Where(id => prior.Charters.Contains(id) != charters.Contains(id)).ToList();
        if (flipped.Count > 0)
        {
            changed.UnionWith(flipped);
            redone = Redone();
            kept = Kept();
        }

        _ = Interlocked.Add(ref _listingRelistedIds, changed.Count);
        var merged = kept.Concat(Listed(redone, charters, NoReport)).ToList();
        merged.Sort(static (a, b) => string.CompareOrdinal(IdOf(a), IdOf(b)) < 0 ? 1 : -1);
        var listing = new JsonArray();
        foreach (var e in merged)
        {
            listing.Add(e);
        }

        return (listing, rows, charters);
    }

    /// <summary>Adds to <paramref name="changed"/> the id of each row in only one of <paramref name="before"/> and <paramref name="after"/>, by identity.</summary>
    private static void ChangedIdsOf(List<JsonObject> before, List<JsonObject> after, HashSet<string> changed)
    {
        var had = new HashSet<JsonObject>(before, ReferenceEqualityComparer.Instance);
        var has = new HashSet<JsonObject>(after, ReferenceEqualityComparer.Instance);
        foreach (var row in after.Where(r => !had.Contains(r)).Concat(before.Where(r => !has.Contains(r))))
        {
            _ = changed.Add(row["meta"].AsObject()["id"].AsString());
        }
    }

    /// <summary>The message store's version and the team records' change token, or null when either cannot be had.</summary>
    private string? ListingVersion()
    {
        try
        {
            if (roster?.TeamsChangeToken() is not { } teams)
            {
                return null;
            }

            using var db = DbMsgs();
            return db.Version().ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + teams.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>A list of the same exchanges that its caller may change: a shared listing is never handed out itself.</summary>
    private static JsonArray CopyOf(JsonArray kept)
    {
        var copy = new JsonArray();
        foreach (var e in kept)
        {
            copy.Add(e.AsObject().Clone());
        }

        return copy;
    }

    public JsonArray ListExchangesWithIds(IReadOnlyList<string> dirs, IReadOnlyList<string> ids) => ListExchanges(dirs, live: false, _ => ids);

    public IReadOnlyList<(string Id, string To, string? Team, string Pool)> ExchangeRowsBefore(
        IReadOnlyList<string> dirs, string? beforeId, int count, IReadOnlyList<string>? teams, string? recipient)
    {
        using var db = DbMsgs();
        return db.ExchangeRowsBefore(dirs, beforeId, count, teams, recipient);
    }

    public long ExchangeCount(IReadOnlyList<string> dirs, IReadOnlyList<string>? teams, string? recipient)
    {
        using var db = DbMsgs();
        return db.ExchangeCount(dirs, teams, recipient);
    }

    public (IReadOnlyList<MsgFileRow> Rows, IReadOnlySet<string> Seeds) DownstreamRequests(string dir, int newest)
    {
        using var db = DbMsgs();
        HashSet<string> seeds;
        if (newest > 0)
        {
            seeds = db.ExchangeRowsBefore([dir], null, newest, null, null).Select(static r => r.Id).ToHashSet(StringComparer.Ordinal);
        }
        else
        {
            // the live scope of §3.2: the live rows and the answered charters of running lanes (Orchestrator ruling, clarified on review F2)
            seeds = db.LiveMessages(dir, clock.UtcNow.ToUnixTimeMilliseconds() - LiveAnsweredWindowMs).Where(static m => m.Type == "request").Select(static m => m.Id).ToHashSet(StringComparer.Ordinal);
            seeds.UnionWith(OpenCharterIdsOf([dir], db));
            seeds.UnionWith(db.RequestDescendants(dir, [.. seeds]));
        }

        // the parent chains, a generation per query; a downstream walk stops at 32 hops, so no more are read
        var rows = db.MessagesWithIds(dir, [.. seeds]).Where(static m => m.Type == "request").ToList();
        var held = rows.Select(static m => m.Id).ToHashSet(StringComparer.Ordinal);
        for (var hops = 0; hops <= 32; hops++)
        {
            var parents = rows.Select(static m => m.Parent).OfType<string>().Where(p => p.Length > 0 && !held.Contains(p)).Distinct(StringComparer.Ordinal).ToList();
            if (parents.Count == 0)
            {
                break;
            }

            held.UnionWith(parents);
            rows.AddRange(db.MessagesWithIds(dir, parents).Where(static m => m.Type == "request"));
        }

        return (rows, seeds);
    }

    /// <summary>The <c>team</c> header of each message at <paramref name="paths"/> that has a row, as stored.</summary>
    public IReadOnlyDictionary<string, string?> MsgTeams(IReadOnlyList<string> paths)
    {
        using var db = DbMsgs();
        return db.TeamsByPath(paths);
    }

    /// <summary>
    /// The pipeline rounds a run of <paramref name="team"/> started at <paramref name="openedSinceMs"/> can count, with the live scope:
    /// every exchange whose slug one of the run's requests has, at any time and in any of the pools.
    /// </summary>
    internal JsonArray ListRunExchanges(IReadOnlyList<string> dirs, string? team, long openedSinceMs) =>
        ListExchanges(dirs, live: true, db => dirs.SelectMany(d => db.RunSlugs(d, team, openedSinceMs)).Distinct(StringComparer.Ordinal)
            .SelectMany(slug => dirs.SelectMany(d => db.RequestIdsWithSlug(d, slug))));

    /// <summary>The exchanges whose request slug is <paramref name="slug"/> in any of the pools, closed ones included.</summary>
    public JsonArray ListSlugExchanges(IReadOnlyList<string> dirs, string slug) =>
        ListExchanges(dirs, live: false, db => dirs.SelectMany(d => db.RequestIdsWithSlug(d, slug)));

    /// <summary>
    /// The exchanges of one pool as the listing pairs them before any filter: per id the pool's last request and response file rows, a
    /// response with no request included (status entries pair pools themselves, so a response filed in another pool still finds its request).
    /// </summary>
    public IReadOnlyList<JsonObject> PoolPairs(string dir)
    {
        using var db = DbMsgs();
        return Paired([dir], d => RequestFiles(d, db));
    }

    /// <summary>Whether the response at <paramref name="path"/> to request <paramref name="id"/> is a no-report body (the listing's open rule).</summary>
    public bool ResponseIsNoReport(string path, string id)
    {
        using var db = DbMsgs();
        return NoReportYet(path, id, db);
    }

    /// <summary>The front matter fields of the message at <paramref name="path"/> as the charter rule reads them, or null when it does not parse.</summary>
    public JsonValue? MessageFields(string path)
    {
        using var db = DbMsgs();
        return Frontmatter(path, db);
    }

    /// <summary>Whether pool <paramref name="dir"/> holds an answered request to a sub-orchestrator: the listing reads lanes only then.</summary>
    public bool AnyAnsweredSubOrchestrator(string dir)
    {
        using var db = DbMsgs();
        return db.AnyAnswered(dir, "sub-orchestrator");
    }

    /// <summary>How many exchanges in the pools have the request slug <paramref name="slug"/>, closed ones included: one indexed query per pool.</summary>
    public int CountSlugExchanges(IReadOnlyList<string> dirs, string slug)
    {
        using var db = DbMsgs();

        // an id in two pools is one exchange, as the listing pairs it
        return dirs.SelectMany(d => db.RequestIdsWithSlug(d, slug)).Distinct(StringComparer.Ordinal).Count();
    }

    /// <summary>
    /// <see cref="ListExchanges(IReadOnlyList{string}, bool)"/> that also lists the exchanges of the ids <paramref name="withIds"/>
    /// gives, and with <paramref name="live"/> off only those.
    /// </summary>
    private JsonArray ListExchanges(IReadOnlyList<string> dirs, bool live, Func<IMsgSource, IEnumerable<string>>? withIds, bool freshLanes = false)
    {
        using var db = DbMsgs();
        var scoped = live || withIds is not null;

        // an answered charter is not live, but a running lane keeps it open: the live scope adds the charters the per-lane query finds
        var liveCharters = scoped && live ? OpenCharterIdsOf(dirs, db) : null;
        var liveRows = scoped ? LiveRows(dirs, db, live, (withIds?.Invoke(db) ?? []).Concat(liveCharters ?? Enumerable.Empty<string>())) : null;
        var version = liveRows is null ? db.Version() : 0;
        var byId = Paired(dirs, dir => liveRows is null ? HeldRequestFiles(dir, db, version) : liveRows[dir]);
        var withRequest = WithRequestNewestFirst(byId);
        IReadOnlySet<string> CharterIds() => liveCharters ?? OpenCharterIdsOf(dirs, db, freshLanes);

        // a lane's charter is addressed to its sub-orchestrator, so no other answered request makes the team files worth reading; a scoped
        // list asks the pools, so it reads them exactly when the full list would
        var mayHaveCharter = AnswersASubOrchestrator(withRequest) || (scoped && dirs.Any(d => db.AnyAnswered(d, "sub-orchestrator")));
        return Listed(withRequest, mayHaveCharter ? CharterIds() : new HashSet<string>(StringComparer.Ordinal), e => NoReportOf(e["response"].AsObject(), e["id"].AsString(), db));
    }

    /// <summary>The exchanges of <paramref name="byId"/> that have a request, newest id first: the listing's order.</summary>
    internal static List<JsonObject> WithRequestNewestFirst(IEnumerable<JsonObject> byId)
    {
        var withRequest = byId.Where(e => e["request"].IsMap).ToList();
        withRequest.Sort(static (a, b) => string.CompareOrdinal(a["id"].AsString(), b["id"].AsString()) < 0 ? 1 : -1);
        return withRequest;
    }

    /// <summary>Whether any of <paramref name="withRequest"/> is an answered request to a sub-orchestrator.</summary>
    internal static bool AnswersASubOrchestrator(IEnumerable<JsonObject> withRequest) =>
        withRequest.Any(static e => e["response"].IsMap && e["request"].AsObject()["meta"].AsObject()["to"].AsString() == "sub-orchestrator");

    /// <summary>
    /// The listing of paired exchanges: each with <c>open</c> (no response, a no-report response <paramref name="noReport"/> says of a request
    /// not to the Orchestrator, or a running lane's charter), <c>charter</c>, <c>to</c> and <c>slug</c>.
    /// </summary>
    internal static JsonArray Listed(List<JsonObject> withRequest, IReadOnlySet<string> charterIds, Func<JsonObject, bool> noReport)
    {
        var result = new JsonArray();
        foreach (var e in withRequest)
        {
            var requestMeta = e["request"].AsObject()["meta"].AsObject();
            var to = requestMeta["to"].AsString();
            var response = e["response"];
            var open = !response.IsMap || (to != "orchestrator" && noReport(e)) || charterIds.Contains(e["id"].AsString());
            var item = e.Clone();
            item["open"] = open;
            if (charterIds.Contains(e["id"].AsString()))
            {
                item["charter"] = true;
            }

            item["to"] = to;
            item["slug"] = requestMeta["slug"];
            result.Add(item);
        }

        return result;
    }

    /// <summary>Whether request <paramref name="id"/> of pool <paramref name="dir"/> is the charter of a lane that is still running.</summary>
    public bool IsOpenLaneCharter(string dir, string id) => OpenLaneCharterIds(dir).Contains(id);

    /// <summary>
    /// Prune's closed set over <paramref name="dirs"/> by query (ah-msgs-fast-2 §3.8 item 7, ruling Q5): no longer live, not answered since
    /// <paramref name="answeredBeforeMs"/>, and not an open lane's charter.
    /// </summary>
    public IReadOnlySet<string> ClosedExchangeIds(IReadOnlyList<string> dirs, long answeredBeforeMs)
    {
        ArgumentNullException.ThrowIfNull(dirs);
        using var db = DbMsgs();
        var closed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in dirs)
        {
            var charters = OpenLaneCharterIds(dir);
            closed.UnionWith(db.ClosedIdsAnsweredBefore(dir, answeredBeforeMs).Where(id => !charters.Contains(id)));
        }

        return closed;
    }

    /// <summary>The ids of the requests of pool <paramref name="dir"/> that are the charter of a lane that is still running. One read of the pool answers every id.</summary>
    public IReadOnlySet<string> OpenLaneCharterIds(string dir)
    {
        using var db = DbMsgs();
        return OpenCharterIdsOf([dir], db);
    }

    /// <summary>How far before a lane opened a request's stored <c>opened_ms</c> may lie and the request still be read as a possible charter; the <c>created</c> header decides.</summary>
    internal const long CharterSlackMs = 60_000;

    /// <summary>The lanes of <paramref name="dirs"/>: their team files read once per listing scope.</summary>
    private LaneCharters Lanes(IReadOnlyList<string> dirs) =>
        Ah.Core.Files.ListingScope.Once("lane-charters\0" + string.Join('\0', dirs), () => LaneCharters.Build(files, dirs, roster));

    /// <summary>
    /// The charters of the running lanes of <paramref name="dirs"/>: each lane's possible charters come from a query and are read in id
    /// order until one is taken.
    /// </summary>
    /// <remarks><paramref name="freshLanes"/> reads the team records now instead of the listing scope's copy: a listing kept past the scope must not carry its lanes.</remarks>
    private IReadOnlySet<string> OpenCharterIdsOf(IReadOnlyList<string> dirs, IMsgSource db, bool freshLanes = false)
    {
        var charters = freshLanes ? LaneCharters.Build(files, dirs, roster) : Lanes(dirs);
        if (charters.IsEmpty)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var roles = charters.CharterRoles;
        var facts = new Dictionary<string, CharterFact?>(StringComparer.Ordinal);
        CharterFact? FactOf(string id) =>
            Paired(dirs, d => db.Messages(d, id).Select(Entry)).FirstOrDefault(static e => e["request"].IsMap) is { } e && CharterFacts([e], charters, db) is [var fact] ? fact : null;

        IEnumerable<CharterFact> Candidates(LaneEntry lane)
        {
            var since = LaneCharters.OpenedMs(lane) is { } ms ? ms - CharterSlackMs : long.MinValue;
            foreach (var id in dirs.SelectMany(d => db.CharterCandidateIds(d, lane.Manager, roles, since)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (!facts.TryGetValue(id, out var fact))
                {
                    fact = FactOf(id);
                    facts[id] = fact;
                }

                if (fact is { } f)
                {
                    yield return f;
                }
            }
        }

        return charters.OpenCharterIds(Candidates);
    }

    /// <summary>
    /// The exchanges of the rows <paramref name="rowsOf"/> gives per pool, in the order their ids first appear: per pool the last row of each
    /// id and type, and across pools the first pool that has one.
    /// </summary>
    private static List<JsonObject> Paired(IReadOnlyList<string> dirs, Func<string, IEnumerable<JsonObject>> rowsOf)
    {
        var byId = new List<JsonObject>();
        var index = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var filled = new HashSet<(string Id, string Type)>();
        foreach (var dir in dirs)
        {
            var own = MsgRows.LastBy(rowsOf(dir), file => (file["meta"].AsObject()["id"].AsString(), file["meta"].AsObject()["type"].AsString()));

            foreach (var ((id, type), file) in own)
            {
                if (!index.TryGetValue(id, out var e))
                {
                    e = new JsonObject { ["id"] = id, ["request"] = JsonValue.Null, ["response"] = JsonValue.Null };
                    index[id] = e;
                    byId.Add(e);
                }

                if (filled.Add((id, type)))
                {
                    e[type] = file;
                }
            }
        }

        return byId;
    }

    private static List<CharterFact> CharterFacts(List<JsonObject> exchanges, LaneCharters charters, IMsgSource db) =>
        CharterFacts(exchanges, charters, path => Frontmatter(path, db));

    /// <summary>The charter facts of the exchanges a running lane could take, with each message's front matter from <paramref name="fieldsOf"/> (null: unreadable).</summary>
    internal static List<CharterFact> CharterFacts(List<JsonObject> exchanges, LaneCharters charters, Func<string, JsonValue?> fieldsOf)
    {
        var facts = new List<CharterFact>();
        foreach (var e in exchanges)
        {
            var request = e["request"].AsObject();
            var to = request["meta"].AsObject()["to"].AsString();
            if (!charters.CouldBeCharter(to))
            {
                continue;
            }

            var fields = fieldsOf(request["path"].AsString());
            if (fields is null)
            {
                continue;
            }

            var team = Text(fields.Value, "team") ?? (Text(fields.Value, "team_file") is { } teamFile ? Path.GetFileNameWithoutExtension(teamFile) : null);
            var closed = e["response"].IsMap && fieldsOf(e["response"].AsObject()["path"].AsString()) is { } reply
                && (reply.Member("withdrawn") is { IsTruthy: true } || reply.Member("closed") is { IsTruthy: true });
            facts.Add(new CharterFact(e["id"].AsString(), to, Text(fields.Value, "to_name"), team, Text(fields.Value, "created"), closed));
        }

        return facts;
    }

    private static JsonValue? Frontmatter(string path, IMsgSource db)
    {
        try
        {
            return MessageText.ParseFrontmatter(ReadMsgText(path, db)) is { } parsed ? (JsonValue?)parsed.Fields : null;
        }
        catch (Exception error) when (error is not StoreUnavailableException)
        {
            return null;
        }
    }

    private static string? Text(JsonValue fields, string key) => fields.Member(key) is { IsText: true } v && v.AsString().Length > 0 ? v.AsString() : null;

    private const double PeerCacheUntrustedMs = 1000;

    private const int PeerTailWindow = 64;
    private const long PeerExtendLimitBytes = 32L * 1024 * 1024;

    private readonly object _peerGate = new();
    private FileStat? _peerStat;
    private double _peerReadMs;
    private List<JsonValue>? _peerParsed;
    private HashSet<string> _peerSeen = new(StringComparer.Ordinal);
    private long _peerConsumed;
    private byte[] _peerTail = [];

    /// <summary>
    /// The peer records, each distinct <c>op_id</c> once, as a shallow copy of each record, so a caller setting or removing a field
    /// does not change what the next read returns. A caller that only reads uses <see cref="PeerRecordsShared"/>.
    /// </summary>
    public JsonArray ReadPeerRecords()
    {
        var shared = PeerRecordsShared();
        var cloneStart = Ah.Core.Time.PhaseTrace.Begin();
        JsonArray copy = [.. shared.Select(static r => r.IsMap ? r.AsObject().Clone() : r)];
        if (Ah.Core.Time.PhaseTrace.On)
        {
            Ah.Core.Time.PhaseTrace.End("peer.clone rows=" + shared.Count, cloneStart);
        }

        return copy;
    }

    /// <summary>The peer records as <see cref="ReadPeerRecords"/> returns them, or null when the database that holds them could not be read.</summary>
    public JsonArray? ReadPeerRecordsKnown()
    {
        var fromDb = DbPeerRecords(out var failed);
        if (failed)
        {
            return null;
        }

        return fromDb is null ? ReadPeerRecords() : [.. fromDb.Select(static r => r.IsMap ? r.AsObject().Clone() : r)];
    }

    /// <summary>
    /// The peer records, each distinct <c>op_id</c> once, with no copy of the records: the caller must not change them. The parse of
    /// the file is kept with the stat it was read under, so a file nobody has appended to costs one <c>stat</c>; a file that has only
    /// been appended to costs the read of the appended bytes, never a parse of every line again. A stat whose modification time lies
    /// within a second of the read is not trusted as unchanged, because a write in the same clock tick would leave it unchanged; the
    /// appended-bytes check then decides, and a file replaced, shortened or changed before its old end is parsed from the start.
    /// </summary>
    public long? MsgsVersion()
    {
        try
        {
            using var db = DbMsgs();
            return db.Version();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    public void ForgetHeldPeerRows() => peerRoute?.Source.ForgetHeld();

    public long? PeerRecordsVersion()
    {
        try
        {
            return peerRoute is not null && peerRoute.Modes.Read(PeerRecordsDomain) == "db" ? peerRoute.Source.Version() : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    public IReadOnlyList<JsonValue> PeerRecordsShared()
    {
        var dbStart = Ah.Core.Time.PhaseTrace.Begin();
        var fromDb = DbPeerRecords(out _);
        Ah.Core.Time.PhaseTrace.End(fromDb is null ? "peer.mode-check (file mode)" : "peer.db-read", dbStart);
        if (fromDb is not null)
        {
            return fromDb;
        }

        return PeerRecordsFromFile();
    }

    private IReadOnlyList<JsonValue> PeerRecordsFromFile()
    {
        var path = AhPaths.UserEntry(environment.HomeDirectory, AhPaths.PeerPendingFile);
        var stat = files.Stat(path);
        var atMs = (double)clock.UtcNow.ToUnixTimeMilliseconds();
        lock (_peerGate)
        {
            if (_peerParsed is { } held && stat is { } now && _peerStat == now && _peerReadMs - now.MtimeMs >= PeerCacheUntrustedMs)
            {
                return [.. held];
            }

            if (stat is { } current && _peerParsed is not null && _peerStat is { } before && before.Inode == current.Inode && before.Device == current.Device
                && current.Size >= _peerConsumed && current.Size - _peerConsumed <= PeerExtendLimitBytes && TryExtendPeerParse(path, current.Size))
            {
                _peerStat = current;
                _peerReadMs = atMs;
                return [.. _peerParsed];
            }

            _peerStat = stat;
            _peerReadMs = atMs;
            _peerSeen = new HashSet<string>(StringComparer.Ordinal);
            _peerConsumed = 0;
            _peerTail = [];
            _peerParsed = stat is null ? null : ReadPeerFile(path);
            return [.. _peerParsed ?? []];
        }
    }

    public IReadOnlyList<JsonValue> PeerRecordsWhere(PeerQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return DbPeerRecords(source => source.Rows(query)) ?? [.. PeerRecordsFromFile().Where(row => PeerRows.Matches(row, query))];
    }

    public IReadOnlyList<JsonValue> PeerRecordsLatestByKeyFor(JsonValue? sessionId) =>
        DbPeerRecords(source => source.LatestByKeyFor(sessionId)) ?? PeerRows.LatestByKey(PeerRecordsFromFile());

    private const int TailBlockBytes = 256 * 1024;
    private const int EarlierScanChunkBytes = 4 * 1024 * 1024;

    public JsonValue? LastBriefRecord(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        var sessionRows = new PeerQuery("session_id", sessionId);
        if (DbPeerRecords(source => source.Rows(sessionRows)) is { } fromDb)
        {
            return LastBriefIn(fromDb, sessionId);
        }

        var path = AhPaths.UserEntry(environment.HomeDirectory, AhPaths.PeerPendingFile);
        if (files.Stat(path) is not { } stat)
        {
            return null;
        }

        var scanStart = Ah.Core.Time.PhaseTrace.Begin();
        JsonValue? found = null;
        var decided = false;
        try
        {
            decided = LastBriefFromTail(path, stat.Size, sessionId, out found);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            decided = false;
        }

        Ah.Core.Time.PhaseTrace.End(decided ? "peer.last-brief tail-scan" : "peer.last-brief tail-scan undecided", scanStart);
        return decided ? found : LastBriefIn(PeerRecordsShared(), sessionId);
    }

    private static bool IsBriefOf(JsonValue record, string sessionId) =>
        record.Member("session_id") is { IsText: true } s && s.AsString() == sessionId && record.Member("type") is null;

    private static JsonValue? LastBriefIn(IEnumerable<JsonValue> records, string sessionId)
    {
        JsonValue? last = null;
        foreach (var record in records)
        {
            if (IsBriefOf(record, sessionId))
            {
                last = record;
            }
        }

        return last;
    }

    /// <summary>
    /// Reads the file backwards in blocks, whole lines only, and takes the first line that is a record of the session without a type.
    /// False when the answer cannot be given exactly from the tail: the record carries an <c>op_id</c> that an earlier line might also
    /// carry (the first line with an <c>op_id</c> is the one the full read keeps).
    /// </summary>
    private bool LastBriefFromTail(string path, long size, string sessionId, out JsonValue? found)
    {
        found = null;
        long blockEnd = size;
        byte[] carry = [];
        while (blockEnd > 0 || carry.Length > 0)
        {
            var blockStart = Math.Max(0, blockEnd - TailBlockBytes);
            byte[] buffer;
            if (blockEnd > blockStart)
            {
                var read = files.ReadRange(path, blockStart, (int)(blockEnd - blockStart)).Bytes;
                if (read.Length != blockEnd - blockStart)
                {
                    return false;
                }

                buffer = [.. read, .. carry];
            }
            else
            {
                buffer = carry;
            }

            var lineEnd = buffer.Length;
            while (true)
            {
                var newline = buffer.AsSpan(0, lineEnd).LastIndexOf((byte)'\n');
                if (newline < 0 && blockStart > 0)
                {
                    carry = buffer[..lineEnd];
                    break;
                }

                var lineStart = newline + 1;
                if (BriefLine(buffer.AsSpan(lineStart, lineEnd - lineStart), sessionId) is { } record)
                {
                    found = record;
                    var opId = record.Member("op_id")?.NonEmptyText;
                    return opId is null || !EarlierLinesMayCarry(path, blockStart + lineStart, opId);
                }

                if (newline < 0)
                {
                    carry = [];
                    break;
                }

                lineEnd = newline;
            }

            blockEnd = blockStart;
            if (blockStart == 0)
            {
                break;
            }
        }

        found = null;
        return true;
    }

    private static JsonValue? BriefLine(ReadOnlySpan<byte> bytes, string sessionId)
    {
        if (bytes.IsEmpty)
        {
            return null;
        }

        var text = System.Text.Encoding.UTF8.GetString(bytes);
        if (JsText.Trim(text).Length == 0 || (!text.Contains(sessionId, StringComparison.Ordinal) && !text.Contains('\\', StringComparison.Ordinal)))
        {
            return null;
        }

        JsonValue record;
        try
        {
            record = JsonText.Parse(text);
        }
        catch (JsonParseException)
        {
            return null;
        }

        return PeerRows.IsRecord(record) && IsBriefOf(record, sessionId) ? record : (JsonValue?)null;
    }

    /// <summary>
    /// Whether the bytes before <paramref name="end"/> could hold a line with <paramref name="opId"/>: its text appears in them, it is
    /// written with a character a JSON writer may escape, or the bytes hold a <c>\u</c> escape that might spell it.
    /// </summary>
    private bool EarlierLinesMayCarry(string path, long end, string opId)
    {
        if (opId.Any(static c => c is < ' ' or > '~' or '"' or '\\' or '/'))
        {
            return true;
        }

        var needle = System.Text.Encoding.UTF8.GetBytes(opId);
        ReadOnlySpan<byte> unicodeEscape = "\\u"u8;
        var overlap = Math.Max(needle.Length, unicodeEscape.Length) - 1;
        for (long at = 0; at < end;)
        {
            var count = (int)Math.Min(EarlierScanChunkBytes, end - at);
            var chunk = files.ReadRange(path, at, count).Bytes;
            if (chunk.Length != count || chunk.AsSpan().IndexOf(needle) >= 0 || chunk.AsSpan().IndexOf(unicodeEscape) >= 0)
            {
                return true;
            }

            at += count;
            if (at < end)
            {
                at -= overlap;
            }
        }

        return false;
    }

    /// <summary>Parses the bytes appended after the last parsed line; false when the file no longer starts with what was parsed.</summary>
    private bool TryExtendPeerParse(string path, long size)
    {
        var window = (int)Math.Min(PeerTailWindow, _peerConsumed);
        FileRange range;
        try
        {
            range = files.ReadRange(path, _peerConsumed - window, (int)(size - _peerConsumed) + window);
        }
        catch (Exception)
        {
            return false;
        }

        var bytes = range.Bytes;
        if (bytes.Length < window || !bytes.AsSpan(0, window).SequenceEqual(_peerTail.AsSpan(_peerTail.Length - window)))
        {
            return false;
        }

        ConsumePeerBytes(bytes.AsSpan(window), _peerParsed!);
        return true;
    }

    private List<JsonValue> ReadPeerFile(string path)
    {
        var records = new List<JsonValue>();
        byte[] raw;
        try
        {
            if (!files.Exists(path))
            {
                return records;
            }

            var readStart = Ah.Core.Time.PhaseTrace.Begin();
            raw = files.ReadBytes(path);
            if (Ah.Core.Time.PhaseTrace.On)
            {
                Ah.Core.Time.PhaseTrace.End("peer.read-file bytes=" + raw.Length, readStart);
            }
        }
        catch (Exception)
        {
            return records;
        }

        var parseStart = Ah.Core.Time.PhaseTrace.Begin();
        ConsumePeerBytes(raw, records);
        if (Ah.Core.Time.PhaseTrace.On)
        {
            Ah.Core.Time.PhaseTrace.End("peer.parse rows=" + records.Count, parseStart);
        }

        return records;
    }

    /// <summary>
    /// Parses the lines of <paramref name="bytes"/> into <paramref name="records"/> and moves the consumed offset past them. An
    /// unterminated last line is taken when it parses, and otherwise left for the next read, when its writer may have finished it.
    /// </summary>
    private void ConsumePeerBytes(ReadOnlySpan<byte> bytes, List<JsonValue> records)
    {
        var lastNewline = bytes.LastIndexOf((byte)'\n');
        var consumed = lastNewline + 1;
        foreach (var line in System.Text.Encoding.UTF8.GetString(bytes[..consumed]).Split('\n'))
        {
            AddPeerLine(line, records);
        }

        if (consumed < bytes.Length)
        {
            var rest = System.Text.Encoding.UTF8.GetString(bytes[consumed..]);
            if (JsText.Trim(rest).Length == 0 || AddPeerLine(rest, records))
            {
                consumed = bytes.Length;
            }
        }

        var all = _peerTail.Length > 0 || consumed > 0 ? [.. _peerTail, .. bytes[..consumed].ToArray()] : _peerTail;
        _peerTail = all.Length > PeerTailWindow ? all[^PeerTailWindow..] : all;
        _peerConsumed += consumed;
    }

    private bool AddPeerLine(string line, List<JsonValue> records)
    {
        if (JsText.Trim(line).Length == 0)
        {
            return true;
        }

        JsonValue rec;
        try
        {
            rec = JsonText.Parse(line);
        }
        catch (JsonParseException)
        {
            return false;
        }

        if (!PeerRows.IsRecord(rec))
        {
            return true;
        }

        if (rec.Member("op_id")?.NonEmptyText is { } opId && !_peerSeen.Add(opId))
        {
            return true;
        }

        records.Add(rec);
        return true;
    }


    /// <summary>
    /// The message files of a pool's <c>msgs</c> directory. The parsed names are kept with the stat of the directory they were listed
    /// under, so a directory nobody has added to or removed from costs one <c>stat</c> and not a listing; a stat whose modification
    /// time lies within a second of the listing is not trusted as unchanged, because a change in the same clock tick would leave it
    /// unchanged. Every call builds new objects, since a JSON node has a single parent.
    /// </summary>
    /// <summary>
    /// The message entries of each pool in the live scope. An id live in one pool is read from every pool, so the pairing across pools
    /// sees the same rows for it as the full list does.
    /// </summary>
    private Dictionary<string, List<JsonObject>> LiveRows(IReadOnlyList<string> dirs, IMsgSource db, bool live, IEnumerable<string> extraIds)
    {
        var since = clock.UtcNow.ToUnixTimeMilliseconds() - LiveAnsweredWindowMs;
        var rows = new Dictionary<string, List<MsgFileRow>>(StringComparer.Ordinal);
        foreach (var dir in dirs)
        {
            rows.TryAdd(dir, live ? [.. db.LiveMessages(dir, since)] : []);
        }

        var ids = rows.Values.SelectMany(static r => r.Select(static m => m.Id)).Concat(extraIds).ToHashSet(StringComparer.Ordinal);
        foreach (var (dir, own) in rows)
        {
            var held = own.Select(static m => m.Id).ToHashSet(StringComparer.Ordinal);
            var missing = ids.Where(id => !held.Contains(id)).ToList();
            if (missing.Count == 0)
            {
                continue;
            }

            own.AddRange(db.MessagesWithIds(dir, missing));
            own.Sort(static (a, b) => string.CompareOrdinal(a.Id + "\0" + a.Type + "\0" + a.To + "\0" + a.Slug, b.Id + "\0" + b.Type + "\0" + b.To + "\0" + b.Slug));
        }

        return rows.ToDictionary(static p => p.Key, static p => p.Value.Select(Entry).ToList(), StringComparer.Ordinal);
    }

    private static JsonObject Entry(MsgFileRow m)
    {
        var meta = new JsonObject { ["id"] = m.Id, ["to"] = m.To, ["slug"] = m.Slug, ["type"] = m.Type };
        if (m.ToName is { } n)
        {
            meta["to_name"] = n;
        }

        return new() { ["path"] = m.Path, ["meta"] = meta };
    }

    /// <summary>The listing's file row of the one stored message at <paramref name="path"/>, or null when it is no message.</summary>
    public JsonObject? FileRowOf(string path)
    {
        using var db = DbMsgs();
        return db.ByPath(path) is { } stored ? Entry(stored) : null;
    }

    private static List<JsonObject> RequestFiles(string dir, IMsgSource db) => [.. db.Messages(dir).Select(Entry)];

    /// <summary>
    /// <see cref="NoReportYet"/> of response row <paramref name="response"/>, held with the row (b459 step 25c): a pool's rows are kept by
    /// <see cref="HeldRequestFiles"/> until one of its messages changes (a fill updates its message row), and the re-read makes new rows, so a
    /// held fact never outlives the body it was read from.
    /// </summary>
    private bool NoReportOf(JsonObject response, string id, IMsgSource db)
    {
        if (_noReport.TryGetValue(response, out var held))
        {
            return held.Value;
        }

        var noReport = NoReportYet(response["path"].AsString(), id, db);
        _noReport.AddOrUpdate(response, new System.Runtime.CompilerServices.StrongBox<bool>(noReport));
        return noReport;
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<JsonObject, System.Runtime.CompilerServices.StrongBox<bool>> _noReport = new();

    /// <summary>Whether a member's response holds no report yet, by <c>reportStatus</c> unless its size already says it does.</summary>
    private static bool NoReportYet(string path, string id, IMsgSource db) =>
        db.ByPath(path) is not { Bytes: { } stored } || (stored.Length <= ReportBytes && ReportStatusOf(System.Text.Encoding.UTF8.GetString(stored), id) == "no-report")
            || MsgRows.IsPartial(System.Text.Encoding.UTF8.GetString(stored));

    private static string ReportStatusOf(string text, string id) => MsgRows.ReportStatusOf(text, id);
}
