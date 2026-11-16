using System.Security.Cryptography;
using System.Text;
using Ah.Core.Json;

namespace Ah.Core.Ui;

public enum LooksOutcome
{
    /// <summary>Done; the reply holds the file as it now stands.</summary>
    Ok,

    /// <summary>The write named a revision that is no longer the file's.</summary>
    Stale,

    /// <summary>The file cannot be read as looks. It is left alone, and nothing writes to it.</summary>
    Corrupt,

    /// <summary>The input has the wrong shape; nothing was written.</summary>
    Invalid,
}

/// <summary>What a read, write or merge of the looks file came to. <see cref="Rev"/> is empty while the file does not exist.</summary>
public sealed record LooksReply(LooksOutcome Outcome, string Rev, JsonObject? Looks, string? Detail = null, int Added = 0, int Already = 0, int Skipped = 0);

/// <summary>
/// The saved looks and the active choice: <c>&lt;ah home&gt;/looks.json</c>, outside the plugin, so a reinstall never touches it. A write is
/// a temp file and a rename, under a lock the daemon and <c>ah looks import</c> share, and at most hourly keeps the version before it as
/// <c>looks.json.bak</c>. A file that does not read as looks is never overwritten.
/// </summary>
public static class LooksFile
{
    public const int MaxLooks = 500;

    /// <summary>The longest look name the page shows; a merge's suffix stays inside it.</summary>
    private const int NameMax = 40;

    private const int MaxTextLength = 256;

    /// <summary>How long <c>looks.json.bak</c> stands before a write may refresh it, so a run of bad writes cannot take the only copy with them.</summary>
    private static readonly TimeSpan BackupEvery = TimeSpan.FromHours(1);

    private static readonly string[] Modes = ["light-ink", "light-emissive", "dark-emissive"];

    public static string PathOf(string ahHome) => Path.Combine(ahHome, "looks.json");

    private static string? Str(this JsonObject o, string key) => o.Get(key) is { IsText: true } v ? v.AsString() : null;

    public static LooksReply Read(string ahHome)
    {
        var (reply, _) = Load(PathOf(ahHome));
        return reply;
    }

    /// <summary>Replaces the looks and the active choice when <paramref name="rev"/> is still the file's revision. The merge record stays.</summary>
    public static LooksReply Write(string ahHome, string rev, JsonValue looks)
    {
        ArgumentNullException.ThrowIfNull(rev);
        if (Shape(looks) is { } bad)
        {
            return new LooksReply(LooksOutcome.Invalid, string.Empty, null, bad);
        }

        var path = PathOf(ahHome);
        using var held = Lock(path);
        var (now, doc) = Load(path);
        if (now.Outcome != LooksOutcome.Ok || now.Rev != rev)
        {
            return now.Outcome == LooksOutcome.Ok ? now with { Outcome = LooksOutcome.Stale } : now;
        }

        var next = looks.AsObject();
        return Commit(path, Compose(next.Get("active"), next.Get("technique"), next.Get("custom")!.Value.AsArray(), doc.Get("merged")));
    }

    /// <summary>
    /// Adds a looks object's custom looks and takes nothing away. A look the file already holds under any id is not added again. Nor,
    /// on the page's automatic merge, is one it has taken before, so a look deleted or renamed since does not come back on the next
    /// load; <paramref name="explicitImport"/> is someone asking for it, and adds any look the file does not hold. A clashing id or name
    /// keeps both, the incoming one suffixed. The active choice and technique are taken only when the file has none.
    /// </summary>
    public static LooksReply Merge(string ahHome, JsonValue incoming, bool explicitImport = false)
    {
        if (!incoming.IsMap || incoming.AsObject().Get("custom") is not { IsArray: true } list)
        {
            return new LooksReply(LooksOutcome.Invalid, string.Empty, null, "the body must be an object with a custom array");
        }

        var path = PathOf(ahHome);
        using var held = Lock(path);
        var (now, doc) = Load(path);
        if (now.Outcome != LooksOutcome.Ok)
        {
            return now;
        }

        var custom = new JsonArray();
        custom.AddRange(doc.Get("custom") is { IsArray: true } had ? had.AsArray() : []);
        var seen = new HashSet<string>(doc.Get("merged") is { IsArray: true } m ? m.AsArray().Select(static h => h.AsString()) : [], StringComparer.Ordinal);
        var merged = new JsonArray();
        merged.AddRange(seen.Select(static h => (JsonValue)h));
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var look in custom)
        {
            foreach (var known in HashesOf(look))
            {
                _ = owners.TryAdd(known, look.AsObject().Str("id")!);
            }
        }

        var ids = custom.Select(static l => l.AsObject().Str("id")!).ToHashSet(StringComparer.Ordinal);
        var names = custom.Select(static l => l.AsObject().Str("name") ?? string.Empty).ToHashSet(StringComparer.Ordinal);
        var moved = new Dictionary<string, string>(StringComparer.Ordinal);
        int added = 0, already = 0, skipped = 0;
        var changed = false;
        foreach (var look in list.AsArray())
        {
            if (LookShape(look) is not null)
            {
                skipped++;
                continue;
            }

            var hash = HashOf(look);
            var id = look.AsObject().Str("id")!;
            var hashes = HashesOf(look);
            var owner = hashes.Select(h => owners.GetValueOrDefault(h)).FirstOrDefault(static o => o is not null);
            var inFile = owner is not null;
            if (inFile)
            {
                moved[id] = owner!;
            }

            if (inFile || (hashes.Any(seen.Contains) && !explicitImport))
            {
                changed |= Record(hash, seen, merged);
                already++;
                continue;
            }

            if (custom.Count >= MaxLooks)
            {
                skipped++;
                continue;
            }

            _ = Record(hash, seen, merged);
            changed = true;
            var copy = look.AsObject().Clone();
            var freeId = Free(id, ids, static (b, n) => n == 1 ? $"{b}-browser" : $"{b}-browser-{n}");
            copy["id"] = freeId;
            moved[id] = freeId;
            var name = copy.Str("name") ?? string.Empty;
            var freeName = Free(name, names, static (b, n) => Suffixed(b, n == 1 ? " (browser)" : $" (browser {n})"));
            copy["name"] = freeName;
            custom.Add(copy);
            foreach (var known in hashes)
            {
                owners[known] = freeId;
            }

            added++;
        }

        var source = incoming.AsObject();
        var active = doc.Get("active");
        if (active is null && ActiveOf(source, moved) is { } taken)
        {
            active = taken;
            changed = true;
        }

        var technique = doc.Get("technique");
        if (technique is null && source.Str("technique") is "shadow" or "dual")
        {
            technique = source.Get("technique");
            changed = true;
        }

        var result = changed ? Commit(path, Compose(active, technique, custom, merged)) : now;
        return result with { Added = added, Already = already, Skipped = skipped };
    }

    private static bool Record(string hash, HashSet<string> seen, JsonArray merged)
    {
        if (!seen.Add(hash))
        {
            return false;
        }

        merged.Add(hash);
        return true;
    }

    private static JsonObject? ActiveOf(JsonObject source, Dictionary<string, string> moved)
    {
        if (source.Get("active") is not { IsMap: true } active)
        {
            return null;
        }

        var o = new JsonObject();
        foreach (var slot in new[] { "light", "dark" })
        {
            if (active.AsObject().Str(slot) is { Length: > 0 and <= MaxTextLength } id)
            {
                o[slot] = moved.GetValueOrDefault(id, id);
            }
        }

        return o.Count == 0 ? null : o;
    }

    private static string Suffixed(string name, string suffix) =>
        (name.Length + suffix.Length > NameMax ? name[..(NameMax - suffix.Length)] : name) + suffix;

    /// <summary><paramref name="wanted"/> when it is free, else its first free variant; either way it is now taken.</summary>
    private static string Free(string wanted, HashSet<string> taken, Func<string, int, string> variant)
    {
        var value = wanted;
        for (var n = 1; !taken.Add(value); n++)
        {
            value = variant(wanted, n);
        }

        return value;
    }

    private static JsonObject Compose(JsonValue? active, JsonValue? technique, JsonArray custom, JsonValue? merged)
    {
        var doc = new JsonObject { ["v"] = 1 };
        if (active is { } a)
        {
            doc["active"] = a;
        }

        if (technique is { } t)
        {
            doc["technique"] = t;
        }

        doc["custom"] = custom;
        if (merged is { } m)
        {
            doc["merged"] = m;
        }

        return doc;
    }

    private static JsonObject Compose(JsonValue? active, JsonValue? technique, JsonArray custom, JsonArray merged) =>
        Compose(active, technique, custom, merged.Count == 0 ? (JsonValue?)null : merged);

    private static (LooksReply Reply, JsonObject Doc) Load(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return (new LooksReply(LooksOutcome.Ok, string.Empty, new JsonObject { ["custom"] = new JsonArray() }), new JsonObject());
        }

        if (!JsonText.TryParse(bytes, out var parsed) || Shape(parsed) is not null || !MergedOk(parsed))
        {
            return (new LooksReply(LooksOutcome.Corrupt, string.Empty, null, "looks.json does not read as looks"), new JsonObject());
        }

        return (new LooksReply(LooksOutcome.Ok, RevOf(bytes), Public(parsed.AsObject())), parsed.AsObject());
    }

    /// <summary>The file without its merge record, which only the merge reads.</summary>
    private static JsonObject Public(JsonObject doc)
    {
        var o = doc.Clone();
        _ = o.Remove("merged");
        _ = o.Remove("v");
        return o;
    }

    private static bool MergedOk(JsonValue doc) =>
        doc.AsObject().Get("merged") is not { } m || (m.IsArray && m.AsArray().All(static h => h.IsText));

    /// <summary>Why <paramref name="looks"/> is not a looks object, or null.</summary>
    private static string? Shape(JsonValue looks)
    {
        if (!looks.IsMap)
        {
            return "looks must be an object";
        }

        var o = looks.AsObject();
        if (o.Get("active") is { } active
            && (!active.IsMap || active.AsObject().Keys.Any(k => k is not ("light" or "dark") || active.AsObject().Str(k) is not { Length: > 0 and <= MaxTextLength })))
        {
            return "active must hold light and dark look ids";
        }

        if (o.Get("technique") is { } technique && (!technique.IsText || technique.AsString() is not ("shadow" or "dual")))
        {
            return "technique must be shadow or dual";
        }

        if (o.Get("custom") is not { IsArray: true } custom)
        {
            return "custom must be an array";
        }

        if (custom.AsArray().Count > MaxLooks)
        {
            return $"at most {MaxLooks} looks";
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var look in custom.AsArray())
        {
            if (LookShape(look) is { } bad)
            {
                return bad;
            }

            if (!ids.Add(look.AsObject().Str("id")!))
            {
                return "two looks share an id";
            }
        }

        return null;
    }

    /// <summary>Why <paramref name="look"/> is not a custom look, or null. The controls inside are the page's to clamp.</summary>
    private static string? LookShape(JsonValue look)
    {
        if (!look.IsMap)
        {
            return "a look must be an object";
        }

        var o = look.AsObject();
        if (o.Str("id") is not { Length: > 0 and <= MaxTextLength })
        {
            return "a look needs an id";
        }

        if (o.Get("name") is { } name && (!name.IsText || name.AsString().Length > MaxTextLength))
        {
            return "a look's name must be text";
        }

        if (o.Str("mode") is not { } mode || !Modes.Contains(mode))
        {
            return "a look needs a known mode";
        }

        if (o.Get("profiles") is not { IsMap: true } profiles
            || profiles.AsObject().Get("shadow") is not { IsMap: true }
            || profiles.AsObject().Get("dual") is not { IsMap: true })
        {
            return "a look needs shadow and dual profiles";
        }

        return (o.Get("backdrop") is { IsMap: false } || o.Get("ink") is { IsMap: false }) ? "a look's backdrop and ink must be objects" : null;
    }

    private static LooksReply Commit(string path, JsonObject doc)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bak = path + ".bak";
        var now = DateTime.UtcNow;
        if (File.Exists(path) && (!File.Exists(bak) || now - File.GetLastWriteTimeUtc(bak) >= BackupEvery))
        {
            var bakTmp = bak + ".tmp";
            File.Copy(path, bakTmp, overwrite: true);
            File.SetLastWriteTimeUtc(bakTmp, now); // a copy keeps its source's time, and this time says when the backup was taken
            File.Move(bakTmp, bak, overwrite: true);
        }

        var bytes = JsonText.ToUtf8(doc, 2);
        var tmp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(tmp, options))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tmp, path, overwrite: true);
        return new LooksReply(LooksOutcome.Ok, RevOf(bytes), Public(doc));
    }

    private static string RevOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];

    /// <summary>
    /// What a look is known by: its hash and, when its profile and graph shadow are the defaults, its hash without them. A ui-port build
    /// merged looks before the page's clean-up added those two fields, so the same look reads both ways.
    /// </summary>
    private static string[] HashesOf(JsonValue look)
    {
        var o = look.AsObject();
        if (o.Str("profile") is not (null or "shadow") || (o.Get("graphShadow") is { IsBool: true } shadow && shadow.AsBool()))
        {
            return [HashOf(look)];
        }

        var bare = o.Clone();
        _ = bare.Remove("profile");
        _ = bare.Remove("graphShadow");
        return [HashOf(look), HashOf(JsonValue.FromObject(bare))];
    }

    /// <summary>A look's content, ignoring its id and the order of its keys.</summary>
    private static string HashOf(JsonValue look)
    {
        var text = new StringBuilder();
        Canon(look, text, skipId: true);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }

    private static void Canon(JsonValue value, StringBuilder text, bool skipId = false)
    {
        if (value.IsMap)
        {
            var o = value.AsObject();
            _ = text.Append('{');
            foreach (var key in o.Keys.Where(k => !skipId || k != "id").Order(StringComparer.Ordinal))
            {
                _ = text.Append(JsonText.Stringify(key, 0)).Append(':');
                Canon(o[key], text);
                _ = text.Append(',');
            }

            _ = text.Append('}');
        }
        else if (value.IsArray)
        {
            _ = text.Append('[');
            foreach (var item in value.AsArray())
            {
                Canon(item, text);
                _ = text.Append(',');
            }

            _ = text.Append(']');
        }
        else
        {
            _ = text.Append(JsonText.Stringify(value, 0));
        }
    }

    /// <summary>The lock the daemon and the CLI share, beside the file. Throws <see cref="IOException"/> when it stays held.</summary>
    private static FileStream Lock(string path)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var until = Environment.TickCount64 + 5_000;
        while (true)
        {
            try
            {
                return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (Environment.TickCount64 < until)
            {
                Thread.Sleep(20);
            }
        }
    }
}
