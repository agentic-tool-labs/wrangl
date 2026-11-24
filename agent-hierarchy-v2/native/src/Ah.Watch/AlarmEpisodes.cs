using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Watch;

/// <summary>
/// What a daemon job has already told the Orchestrator, kept across daemon restarts: a map from an alarm's key to the episode it was
/// told for (a session id, a request interval, anything that names one occurrence) and when it was last seen. A job records an
/// episode only after the tell was delivered, asks for it before telling again, and clears it when the cause is resolved. Entries
/// not seen for a retention window are forgotten, so a key nothing refreshes any more cannot live for ever.
/// </summary>
public interface IAlarmEpisodes
{
    /// <summary>The episode last told for <paramref name="key"/>, or null when none is recorded.</summary>
    string? Told(string key);

    /// <summary>Records that <paramref name="episode"/> was told for <paramref name="key"/> and is seen now; telling the episode already held only refreshes when it was seen.</summary>
    void Tell(string key, string episode);

    /// <summary>Forgets what was told for <paramref name="key"/>, because its cause is resolved.</summary>
    void Clear(string key);

    /// <summary>Forgets every entry last seen longer than <paramref name="unseenFor"/> ago, and every entry that records no time.</summary>
    void Forget(TimeSpan unseenFor);

    /// <summary>The whole entry recorded for <paramref name="key"/> (a copy), or null when none is.</summary>
    JsonObject? Entry(string key);

    /// <summary>Replaces the entry for <paramref name="key"/> with <paramref name="fields"/>, stamped as seen now.</summary>
    void Put(string key, JsonObject fields);

    /// <summary>The keys recorded that start with <paramref name="prefix"/>.</summary>
    IReadOnlyList<string> Keys(string prefix);
}

/// <summary>
/// The map is one JSON object, <c>{key: {episode, at, told_at}}</c> (<c>told_at</c>: when the episode was first told, never refreshed). It is loaded once, kept in memory under one process-wide lock, and
/// written through on every change; the daemon is its only writer, so a second process that writes the same file needs a file lock
/// first. The saver is expected to replace the file atomically under a unique name. A file that cannot be parsed is handed to
/// <c>keep</c> before the map starts empty, so it is not overwritten unread.
/// </summary>
internal sealed class AlarmEpisodes(Func<string?> load, Action<string> save, Func<DateTimeOffset> now, Action<string>? keep = null) : IAlarmEpisodes
{
    /// <summary>How long a recorded episode keeps its place before a repeat of it only refreshes when it was seen.</summary>
    private static readonly TimeSpan Refresh = TimeSpan.FromHours(1);

    private static readonly object Gate = new();

    private JsonObject? _map;

    public string? Told(string key)
    {
        lock (Gate)
        {
            return Map().Get(key) is { } entry ? EpisodeOf(entry) : null;
        }
    }

    public void Tell(string key, string episode)
    {
        lock (Gate)
        {
            var map = Map();
            var at = now();
            if (map.Get(key) is { } held && EpisodeOf(held) == episode && SeenAt(held) is { } seen && at - seen < Refresh)
            {
                return;
            }

            // told_at is when this episode was first told; a refresh of the same episode keeps it (one held without one stays without)
            var entry = new JsonObject { ["episode"] = episode, ["at"] = Timestamps.ToIsoString(at) };
            if (map.Get(key) is not { } prior || EpisodeOf(prior) != episode)
            {
                entry["told_at"] = Timestamps.ToIsoString(at);
            }
            else if (prior.Member("told_at") is { IsText: true } toldAt)
            {
                entry["told_at"] = toldAt;
            }

            map[key] = entry;
            Save(map);
        }
    }

    public void Clear(string key)
    {
        lock (Gate)
        {
            var map = Map();
            if (map.Get(key) is null)
            {
                return;
            }

            map.Remove(key);
            Save(map);
        }
    }

    public void Forget(TimeSpan unseenFor)
    {
        lock (Gate)
        {
            var map = Map();
            var cutoff = now() - unseenFor;
            var stale = map.Keys.Where(k => map.Get(k) is not { } entry || SeenAt(entry) is not { } seen || seen < cutoff).ToList();
            if (stale.Count == 0)
            {
                return;
            }

            foreach (var key in stale)
            {
                map.Remove(key);
            }

            Save(map);
        }
    }

    public JsonObject? Entry(string key)
    {
        lock (Gate)
        {
            return Map().Get(key) is { } entry
                ? entry.IsMap ? JsonText.Parse(JsonText.Stringify(entry, 0)).AsObject() : EpisodeOf(entry) is { } episode ? new JsonObject { ["episode"] = episode } : null
                : null;
        }
    }

    public void Put(string key, JsonObject fields)
    {
        lock (Gate)
        {
            var map = Map();
            fields["at"] = Timestamps.ToIsoString(now());
            map[key] = fields;
            Save(map);
        }
    }

    public IReadOnlyList<string> Keys(string prefix)
    {
        lock (Gate)
        {
            return [.. Map().Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))];
        }
    }

    private static string? EpisodeOf(JsonValue entry) =>
        entry is { IsText: true } ? entry.AsString() : entry.Member("episode") is { IsText: true } episode ? episode.AsString() : null;

    private static DateTimeOffset? SeenAt(JsonValue entry) =>
        entry.Member("at") is { IsText: true } at && Timestamps.TryParseMs(at.AsString(), out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : null;

    private JsonObject Map() => _map ??= Read();

    private JsonObject Read()
    {
        string? text;
        try
        {
            text = load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new JsonObject();
        }

        if (text is null)
        {
            return new JsonObject();
        }

        try
        {
            return JsonText.Parse(text) is { IsMap: true } doc ? doc.AsObject() : Unreadable(text);
        }
        catch (JsonParseException)
        {
            return Unreadable(text);
        }
    }

    private JsonObject Unreadable(string text)
    {
        try
        {
            keep?.Invoke(text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the unreadable file is then overwritten by the next change
        }

        return new JsonObject();
    }

    private void Save(JsonObject map)
    {
        try
        {
            save(JsonText.Stringify(JsonValue.FromObject(map), 0));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // an unsaved entry only lets the next daemon start tell that episode once more
        }
    }
}
