using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Watch.Model;

/// <summary>A lane that wants to hear when a subscription token starts, and to be told to re-read keys that come due on their own (a lapsed subscription, a held-back refresh).</summary>
internal interface ILeaseLane
{
    /// <summary>The lane key a subscription token names, or null when it names nothing of this lane.</summary>
    string? KeyOfToken(string token);

    /// <summary>The keys the lane wants re-read now: held state whose subscription lapsed, or a refresh held back that is due.</summary>
    IReadOnlyList<string> LapsedKeys();
}

/// <summary>
/// The transcript lane: one key per activity key (canonical directory, subject), one header slice <c>transcript:&lt;dirId&gt;/&lt;subject&gt;</c>.
/// It reads a transcript only while the slice is subscribed, and only as byte windows through <see cref="TranscriptTail"/>. The file path is
/// the activity record's <c>transcript_path</c>, validated again before every open; without a valid path the slice says so and nothing is opened.
/// The newest entries stay in a ring per subscribed key, exposed through <see cref="EntriesOf"/> and never published here.
/// </summary>
internal sealed class TranscriptLane(
    IViewStateReader reader,
    IFileReader files,
    IHierarchyUsage usage,
    Func<string, string?> validPath,
    Func<IReadOnlyList<string>> canonicalDirs,
    Func<string, bool> subscribed,
    Action<string>? onReset = null,
    Action? onRootsChanged = null,
    string? projects = null,
    Func<string, string, bool>? owns = null) : IModelLane, ILeaseLane, ITranscriptSource
{
    public const string LaneName = "transcript";

    private readonly object _gate = new();
    private readonly Dictionary<string, (string Path, TranscriptTail Tail)> _held = new(StringComparer.Ordinal);
    private int _counted;

    public string Name => LaneName;

    public static string SliceIdOf(string dir, string subject) => "transcript:" + ActivityLane.DirId(dir) + "/" + subject;

    public IReadOnlyList<string> SliceIds(string key)
    {
        var (dir, subject) = Split(key);
        return [SliceIdOf(dir, subject)];
    }

    /// <summary>The keys that hold a tail now: nothing is enumerated from a directory, a transcript is read only after a client asked for it.</summary>
    public IReadOnlyList<string> Keys()
    {
        lock (_gate)
        {
            return [.. _held.Keys];
        }
    }

    public string? KeyOfToken(string token)
    {
        const string Head = "transcript:";
        if (!token.StartsWith(Head, StringComparison.Ordinal))
        {
            return null;
        }

        var slash = token.IndexOf('/', Head.Length);
        if (slash < 0)
        {
            return null;
        }

        var dirId = token[Head.Length..slash];
        var dir = canonicalDirs().FirstOrDefault(d => ActivityLane.DirId(d) == dirId);
        return dir is null ? null : ActivityLane.KeyOf(dir, token[(slash + 1)..]);
    }

    public IReadOnlyList<string> LapsedKeys()
    {
        var held = Keys();
        return [.. held.Where(k => !subscribed(SliceIds(k)[0]))];
    }

    /// <summary>The newest entries of <paramref name="key"/>, oldest first; empty when the key holds no tail.</summary>
    public IReadOnlyList<TranscriptEntry> EntriesOf(string key)
    {
        lock (_gate)
        {
            return _held.TryGetValue(key, out var held) ? [.. held.Tail.Entries] : [];
        }
    }

    public (IReadOnlyList<TranscriptEntry> Ring, string? Path) Held(string dirId, string subject)
    {
        if (DirOf(dirId) is not { } dir)
        {
            return ([], null);
        }

        lock (_gate)
        {
            return _held.TryGetValue(ActivityLane.KeyOf(dir, subject), out var held) ? (held.Tail.Entries, held.Path) : ([], null);
        }
    }

    /// <summary>
    /// The path of a key nobody leased: the activity record's <c>transcript_path</c>, validated as before every open. It is read through the
    /// lane's own read op on the caller's thread; nothing is held or published.
    /// </summary>
    public string? ColdPath(string dirId, string subject)
    {
        if (DirOf(dirId) is not { } dir)
        {
            return null;
        }

        var record = reader.Read("activity", "read", dir, new JsonObject { ["dir"] = dir, ["subject"] = subject });
        return PathOf(record, dir, subject);
    }

    /// <summary>
    /// Whether a member row of the team directory <paramref name="dirId"/> in <paramref name="slices"/> carries the session
    /// <paramref name="subject"/>: the sessions a directory's teams own are the ones their tree rows name.
    /// </summary>
    internal static bool TreeRowOwns(IEnumerable<Slice> slices, string dirId, string subject)
    {
        var head = "member:" + dirId + "/";
        foreach (var slice in slices)
        {
            if (slice.Id.StartsWith(head, StringComparison.Ordinal)
                && JsonText.TryParse(slice.Body, out var body) && body.Member("session") is { IsText: true } session && session.AsString() == subject)
            {
                return true;
            }
        }

        return false;
    }

    private readonly Dictionary<string, string> _found = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _missedAt = new(StringComparer.Ordinal);

    /// <summary>
    /// The valid transcript path of a session: the one its activity record names, else, for a session the team in <paramref name="dir"/>
    /// owns, the file <c>&lt;session&gt;.jsonl</c> in some project directory. A session that works from another repository than its
    /// team's has its activity record in that repository's directory, not in the team's, but its transcript is still under the user's
    /// projects. A session no team of the directory owns is never looked up, so a name a client makes up reads nothing. A found path is
    /// kept; a miss is kept until the projects directory changes.
    /// </summary>
    private string? PathOf(JsonValue record, string dir, string subject)
    {
        if (record.IsMap && record.Member("transcript_path") is { IsText: true } raw)
        {
            return validPath(raw.AsString());
        }

        if (projects is null || owns is null || subject.Length is 0 or > 64 || !subject.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            || !owns(ActivityLane.DirId(dir), subject))
        {
            return null;
        }

        lock (_gate)
        {
            if (_found.TryGetValue(subject, out var kept) && validPath(kept) is { } still)
            {
                return still;
            }

            _ = _found.Remove(subject);
        }

        double? changed = null;
        try
        {
            changed = files.Stat(projects)?.MtimeMs;
            lock (_gate)
            {
                if (changed is { } now && _missedAt.TryGetValue(subject, out var then) && then == now)
                {
                    return null;
                }
            }

            foreach (var project in files.List(projects))
            {
                var candidate = projects + "/" + project + "/" + subject + ".jsonl";
                if (files.Exists(candidate) && validPath(candidate) is { } found)
                {
                    lock (_gate)
                    {
                        _found[subject] = found;
                        _ = _missedAt.Remove(subject);
                    }

                    return found;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // an unreadable projects directory has no transcripts to offer
        }

        if (changed is { } at)
        {
            lock (_gate)
            {
                _missedAt[subject] = at;
            }
        }

        return null;
    }

    private string? DirOf(string dirId) => canonicalDirs().FirstOrDefault(d => ActivityLane.DirId(d) == dirId);

    /// <summary>The directories of the transcript files held now, so the watcher can watch them only while they are subscribed.</summary>
    public IReadOnlyList<string> LeasedDirs()
    {
        lock (_gate)
        {
            return [.. _held.Values.Select(static h => Path.GetDirectoryName(h.Path)!).Distinct(StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// The path row: a <c>.jsonl</c> in the directory of a held transcript is that transcript's key when it is the held file and noise otherwise;
    /// a path under no held directory is not ours.
    /// </summary>
    internal PathRow Row() =>
        (string path, out string lane, out string key) =>
        {
            lane = string.Empty;
            key = string.Empty;
            if (!path.EndsWith(".jsonl", StringComparison.Ordinal))
            {
                return null;
            }

            lock (_gate)
            {
                var dir = Path.GetDirectoryName(path);
                foreach (var (heldKey, held) in _held)
                {
                    if (held.Path == path)
                    {
                        lane = LaneName;
                        key = heldKey;
                        return PathClass.Classified;
                    }
                }

                return _held.Values.Any(h => Path.GetDirectoryName(h.Path) == dir) ? PathClass.Ignored : null;
            }
        };

    public LaneResult Reread(string key, long nowMs)
    {
        var (dir, subject) = Split(key);
        var id = SliceIdOf(dir, subject);
        if (!subscribed(id))
        {
            return Drop(key, id);
        }

        var record = reader.Read("activity", "read", dir, new JsonObject { ["dir"] = dir, ["subject"] = subject });
        var path = PathOf(record, dir, subject);
        if (path is null)
        {
            return record.IsMap ? Unreadable(key, id) : Drop(key, id);
        }

        var tail = TailFor(key, path);
        var resets = tail.Resets;
        var present = tail.Advance(path);
        if (tail.Resets != resets && tail.LastResetReason is { } reason)
        {
            onReset?.Invoke(reason);
        }

        if (!present)
        {
            return Unreadable(key, id);
        }

        var turnAt = record.IsMap && record.Member("turn_at") is { IsText: true } ta
            && DateTimeOffset.TryParse(ta.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
            ? when.ToUnixTimeMilliseconds() : 0;
        var body = new JsonObject
        {
            ["path_ok"] = true,
            ["length"] = tail.Length,
            ["newest_offset"] = tail.NewestOffset,
            ["oldest_held_offset"] = tail.OldestHeldOffset,
            ["turn_usage"] = tail.UsageSince(turnAt).ToJson(),
            ["resets"] = tail.Resets,
            ["as_of"] = tail.AsOfMs,
        };
        return new LaneResult([new SliceChange(id, JsonText.ToUtf8(JsonValue.FromObject(body), 0), tail.AsOfMs)]);
    }

    private TranscriptTail TailFor(string key, string path)
    {
        TranscriptTail fresh;
        lock (_gate)
        {
            if (_held.TryGetValue(key, out var held) && held.Path == path)
            {
                return held.Tail;
            }

            fresh = new TranscriptTail(files, usage);
            if (Interlocked.Exchange(ref _counted, 1) == 0)
            {
                Ah.Core.CoreModule.CensusAdd(this, "watch.transcript_tails", static l => { lock (l._gate) { return l._held.Count; } });
            }

            _held[key] = (path, fresh);
        }

        onRootsChanged?.Invoke();
        return fresh;
    }

    private LaneResult Drop(string key, string id)
    {
        bool had;
        lock (_gate)
        {
            had = _held.Remove(key);
        }

        if (had)
        {
            onRootsChanged?.Invoke();
        }

        return new LaneResult([]) { Evict = [id] };
    }

    private LaneResult Unreadable(string key, string id)
    {
        bool had;
        lock (_gate)
        {
            had = _held.Remove(key);
        }

        if (had)
        {
            onRootsChanged?.Invoke();
        }

        var body = new JsonObject { ["path_ok"] = false, ["as_of"] = 0 };
        return new LaneResult([new SliceChange(id, JsonText.ToUtf8(JsonValue.FromObject(body), 0), 0)]);
    }

    private static (string Dir, string Subject) Split(string key)
    {
        var at = key.IndexOf('\u001f', StringComparison.Ordinal);
        return at < 0 ? throw new ArgumentException("not a transcript key", nameof(key)) : (key[..at], key[(at + 1)..]);
    }
}
