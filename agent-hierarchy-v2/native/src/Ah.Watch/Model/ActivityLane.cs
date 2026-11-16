using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>
/// The activity lane: one key per (canonical hierarchy directory, session or pane), one slice per key, <c>activity:&lt;dirId&gt;/&lt;subject&gt;</c>.
/// A key is re-read with one <c>activity.read</c> through the state reader; the key set comes from <c>activity.keys</c>, so the lane
/// never lists a directory or opens a file itself. The slice carries the record's whitelisted fields and an <c>as_of</c> taken from the
/// record, never from the clock; a record that is gone removes the slice.
/// </summary>
internal sealed class ActivityLane(IViewStateReader reader, Func<IReadOnlyList<string>> canonicalDirs, Func<IReadOnlyList<string>>? knownDirs = null, Action<string, string>? changed = null) : IModelLane
{
    public const string LaneName = "activity";

    private const char Separator = '\u001f';

    public string Name => LaneName;

    /// <summary>The key of (<paramref name="dir"/>, <paramref name="subject"/>).</summary>
    public static string KeyOf(string dir, string subject) => dir + Separator + subject;

    /// <summary>A short stable id of a directory, the same in every process.</summary>
    public static string DirId(string dir)
    {
        if (DirIds.TryGetValue(dir, out var id))
        {
            return id;
        }

        // b459 step 11: a pure function of the string, asked on every manifest; the few hierarchy directories make the bound a backstop
        if (DirIds.Count >= DirIdsCap)
        {
            DirIds.Clear();
        }

        return DirIds[dir] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dir)))[..8].ToLowerInvariant();
    }

    private const int DirIdsCap = 256;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> DirIds = new(StringComparer.Ordinal);

    public static string SliceIdOf(string dir, string subject) => "activity:" + DirId(dir) + "/" + subject;

    public IReadOnlyList<string> SliceIds(string key)
    {
        var (dir, subject) = Split(key);
        return [SliceIdOf(dir, subject)];
    }

    public LaneResult Reread(string key, long nowMs)
    {
        var (dir, subject) = Split(key);
        var id = SliceIdOf(dir, subject);
        changed?.Invoke(dir, subject);
        if (knownDirs is not null && !knownDirs().Contains(dir, StringComparer.Ordinal))
        {
            return new LaneResult([new SliceChange(id, null)]) { Record = LaneRecord.Absent };
        }

        var record = reader.Read("activity", "read", dir, new JsonObject { ["dir"] = dir, ["subject"] = subject });
        if (!record.IsMap)
        {
            return new LaneResult([new SliceChange(id, null)]) { Record = LaneRecord.Absent };
        }

        var body = BodyOf(dir, subject, record, nowMs);
        return new LaneResult([new SliceChange(id, JsonText.ToUtf8(JsonValue.FromObject(body), 0), AsOf(record))]) { Record = new LaneRecord(JsonValue.FromObject(body), AsOf(record)) };
    }

    public IReadOnlyList<string> Keys()
    {
        var keys = new List<string>();
        foreach (var dir in canonicalDirs().Distinct(StringComparer.Ordinal))
        {
            JsonValue reply;
            try
            {
                reply = reader.Read("activity", "keys", dir, new JsonObject { ["dir"] = dir });
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (reply.Member("subjects") is { IsArray: true } subjects)
            {
                keys.AddRange(subjects.AsArray().Where(static s => s.IsText).Select(s => KeyOf(dir, s.AsString())));
            }
        }

        return keys;
    }

    /// <summary>The whitelisted, validated fields of a stored record; anything else in the file is dropped. A member idle on open background work reads as working.</summary>
    internal static JsonObject BodyOf(string dir, string subject, JsonValue record, double nowMs)
    {
        var onBackground = Ah.Hierarchy.HierarchyModule.IdleOnBackgroundWork(record, nowMs);
        var body = new JsonObject
        {
            ["dir_id"] = DirId(dir),
            ["subject"] = subject,
            ["activity"] = onBackground ? "working" : Text(record, "activity") ?? Text(record, "status") ?? JsonValue.Null,
            ["bg_open"] = record.Member("bg_open") is { IsNumber: true } open ? open : JsonValue.Null,
            ["tool"] = Text(record, "tool") ?? JsonValue.Null,
            ["tool_at"] = Text(record, "tool_at") ?? JsonValue.Null,
            ["turn_at"] = Text(record, "turn_at") ?? JsonValue.Null,
        };
        body["context"] = record.Member("context") is { IsMap: true } context
            ? JsonValue.FromObject(new JsonObject
            {
                ["used"] = context.Member("used") is { IsNumber: true } used && double.IsFinite(used.AsNumber()) ? used : JsonValue.Null,
                ["window_source"] = Text(context, "window_source") ?? JsonValue.Null,
                ["at"] = Text(context, "at") ?? JsonValue.Null,
                ["model"] = Text(context, "model") ?? JsonValue.Null,
            })
            : JsonValue.Null;
        if (Text(record, "blocked_by") == "question")
        {
            body["question"] = Text(record, "note") ?? string.Empty;
        }

        // Only an open turn is carried, so a record that never had one keeps its slice as it was (ah2-62).
        if (record.Member("turn_open") is { IsBool: true } turnOpen && turnOpen.AsBool())
        {
            body["turn_open"] = true;
        }

        return body;
    }

    /// <summary>The latest of the record's own timestamps (tool_at, turn_at, context.at, at), in epoch ms; 0 when it has none.</summary>
    internal static long AsOf(JsonValue record)
    {
        var latest = 0L;
        foreach (var text in new[] { Text(record, "tool_at"), Text(record, "turn_at"), record.Member("context") is { IsMap: true } c ? Text(c, "at") : null, Text(record, "at") })
        {
            if (text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
            {
                latest = Math.Max(latest, when.ToUnixTimeMilliseconds());
            }
        }

        return latest;
    }

    private static string? Text(JsonValue record, string name) =>
        record.Member(name) is { IsText: true } value && value.AsString().Length > 0 ? value.AsString() : null;

    internal static (string Dir, string Subject) Split(string key)
    {
        var at = key.IndexOf(Separator, StringComparison.Ordinal);
        return at < 0 ? throw new ArgumentException("not an activity key", nameof(key)) : (key[..at], key[(at + 1)..]);
    }
}

/// <summary>What the path table made of a changed path.</summary>
internal enum PathClass
{
    /// <summary>A path the table has a row for: it maps to a lane key.</summary>
    Classified,

    /// <summary>An activity record under a directory that is not canonical (a linked work tree's own copy): dropped, the canonical copy's own write emits its own hint.</summary>
    NonCanonical,

    /// <summary>A path under a watched root that no row classifies.</summary>
    Unclassified,

    /// <summary>A path the table knows to be noise (the daemon's own scratch files).</summary>
    Ignored,
}

/// <summary>
/// One registered row of the path table: the verdict for a path it recognises (and the lane key it maps to when that verdict is
/// <see cref="PathClass.Classified"/>), else null so the next row is tried.
/// </summary>
internal delegate PathClass? PathRow(string path, out string lane, out string key);

/// <summary>
/// The path to lane table: one place that turns a changed file path into <c>{lane, key}</c>. The messages rows are a file of the pool, <c>&lt;canonicalDir&gt;/msgs/&lt;file&gt;</c>,
/// and a file of its archive, <c>&lt;canonicalDir&gt;/msgs/archive/&lt;file&gt;</c>; both map to the exchange the file name carries, which
/// <paramref name="messageIdOf"/> reads with the parser the message library uses (null when the name is not a message file). A
/// <c>.tmp</c> write is ignored, because its rename arrives as the file's own path.
/// </summary>
internal sealed class PathLaneTable(Func<IReadOnlyCollection<string>> canonicalDirs, Func<string, string?>? messageIdOf = null, string? home = null, string? knownDirsFile = null, string? peerPendingFile = null)
{
    private readonly object _gate = new();
    private (string Owner, PathRow Row)[] _rows = [];

    /// <summary>Adds a row for <paramref name="owner"/> that is tried before the built-in rules; several rows may share an owner. Rows can be added while hints arrive on other threads.</summary>
    public void Register(string owner, PathRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (_gate)
        {
            _rows = [.. _rows, (owner, row)];
        }
    }

    /// <summary>Removes every row <paramref name="owner"/> registered.</summary>
    public void Unregister(string owner)
    {
        lock (_gate)
        {
            _rows = [.. _rows.Where(r => r.Owner != owner)];
        }
    }

    public PathClass Classify(string path, out string lane, out string key)
    {
        foreach (var (_, row) in Volatile.Read(ref _rows))
        {
            if (row(path, out lane, out key) is { } verdict)
            {
                return verdict;
            }
        }

        // activity records are rows since per-dir-files-to-db S2: the write relay names their lane, no file path does
        var message = ClassifyMessage(path, out lane, out key);
        if (message != PathClass.Unclassified)
        {
            return message;
        }

        var progress = ClassifyProgress(path, out lane, out key);
        return progress == PathClass.Unclassified ? ClassifyTree(path, out lane, out key) : progress;
    }

    /// <summary>
    /// The rows of the Tree's lanes: <c>&lt;dir&gt;/team.json</c> and <c>&lt;dir&gt;/teams/&lt;alias&gt;.json</c> (teams), <c>&lt;dir&gt;/pipeline/&lt;run&gt;/decisions.jsonl</c> (decisions), <c>&lt;home&gt;/registrations/&lt;session&gt;.json</c> (registrations) and
    /// the known-dirs file under the home directory. A <c>.tmp</c> path, a run directory itself, <c>&lt;dir&gt;/status.json</c> and everything under <c>&lt;dir&gt;/jobs</c>, <c>&lt;dir&gt;/tmp</c> and the state engine's <c>&lt;dir&gt;/.state-*</c> directories are ignored.
    /// </summary>
    private PathClass ClassifyTree(string path, out string lane, out string key)
    {
        lane = string.Empty;
        key = string.Empty;
        if (path.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return PathClass.Ignored;
        }

        if (knownDirsFile is not null && path == knownDirsFile)
        {
            lane = KnownDirsLane.LaneName;
            key = KnownDirsLane.Key;
            return PathClass.Classified;
        }

        if (home is not null)
        {
            var registrations = home + "/registrations/";
            if (path.StartsWith(registrations, StringComparison.Ordinal))
            {
                var file = path[registrations.Length..];
                if (file.Contains('/', StringComparison.Ordinal) || !file.EndsWith(".json", StringComparison.Ordinal) || file.Length <= 5)
                {
                    return PathClass.Unclassified;
                }

                lane = RegistrationLane.LaneName;
                key = file[..^5];
                return PathClass.Classified;
            }
        }

        var dir = canonicalDirs().Where(d => path.StartsWith(d + "/", StringComparison.Ordinal)).OrderByDescending(static d => d.Length).FirstOrDefault();
        if (dir is null)
        {
            return PathClass.Unclassified;
        }

        var parts = path[(dir.Length + 1)..].Split('/');
        switch (parts)
        {
            case ["team.json"]:
            case ["teams", { Length: > 5 } alias] when alias.EndsWith(".json", StringComparison.Ordinal):
                lane = TeamLane.LaneName;
                key = TeamLane.KeyOf(dir, path);
                return PathClass.Classified;
            case ["pipeline", _]:
            case ["status.json"]:
            case ["jobs", ..]:
            case ["tmp", ..]:
            case [var stateDir, ..] when stateDir.StartsWith(".state-", StringComparison.Ordinal):
                return PathClass.Ignored;
            case ["pipeline", var run, "decisions.jsonl"]:
                lane = DecisionsLane.LaneName;
                key = DecisionsLane.KeyOf(dir, run);
                return PathClass.Classified;
            default:
                return PathClass.Unclassified;
        }
    }

    private PathClass ClassifyMessage(string path, out string lane, out string key)
    {
        lane = string.Empty;
        key = string.Empty;
        if (peerPendingFile is not null && path == peerPendingFile)
        {
            lane = MessagesLane.LaneName;
            key = MessagesLane.KeyOf(string.Empty, MessagesLane.HeardId);
            return PathClass.Classified;
        }

        var msgs = path.LastIndexOf("/msgs/", StringComparison.Ordinal);
        if (msgs < 0 || messageIdOf is null)
        {
            return messageIdOf is not null && path.EndsWith("/msgs", StringComparison.Ordinal) ? PathClass.Ignored : PathClass.Unclassified;
        }

        var dir = path[..msgs];
        var rest = path[(msgs + "/msgs/".Length)..];
        if (rest == "archive")
        {
            return PathClass.Ignored;
        }

        var file = rest.StartsWith("archive/", StringComparison.Ordinal) ? rest["archive/".Length..] : rest;
        if (file.Length == 0 || file.Contains('/', StringComparison.Ordinal))
        {
            return PathClass.Unclassified;
        }

        if (file.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return PathClass.Ignored;
        }

        if (messageIdOf(file) is not { } id)
        {
            return PathClass.Unclassified;
        }

        if (!canonicalDirs().Contains(dir, StringComparer.Ordinal))
        {
            return PathClass.NonCanonical;
        }

        lane = MessagesLane.LaneName;
        key = MessagesLane.KeyOf(dir, id);
        return PathClass.Classified;
    }

    private static readonly string[] ProgressNoiseSuffixes = [".journal.md", ".tmp"];

    private static readonly string[] ProgressNoiseNames = ["rollup.md", "sessions.jsonl", ".nudged"];

    /// <summary>
    /// No progress file feeds a lane: the plan, checklist, ledger and off switch are rows, relayed by their writes (files-off-disk §4). The team's rollup, journals,
    /// session inventory and nudge file, and <c>.tmp</c> writes, are noise; so is the team's own directory (its appearance re-syncs the watchers).
    /// </summary>
    private static PathClass ClassifyProgress(string path, out string lane, out string key)
    {
        lane = string.Empty;
        key = string.Empty;
        var progress = path.LastIndexOf("/progress/", StringComparison.Ordinal);
        if (progress < 0)
        {
            return PathClass.Unclassified;
        }

        var parts = path[(progress + "/progress/".Length)..].Split('/');
        if (parts.Length == 1 && parts[0].Length > 0)
        {
            return PathClass.Ignored;
        }

        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            return PathClass.Unclassified;
        }

        var file = parts[1];
        if (ProgressNoiseNames.Contains(file, StringComparer.Ordinal) || ProgressNoiseSuffixes.Any(s => file.EndsWith(s, StringComparison.Ordinal)))
        {
            return PathClass.Ignored;
        }

        return PathClass.Unclassified;
    }
}
