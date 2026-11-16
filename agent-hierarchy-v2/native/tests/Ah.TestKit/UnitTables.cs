using System.Text;
using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.TestKit;

/// <summary>
/// An environment of fixed variables. <c>HOME</c> is the home directory; <c>AH_HOME</c> or the default under it is <see cref="AhHome"/>.
/// <see cref="TestMode"/> is simply <c>AH_TEST</c> being <c>1</c>: the harness that builds one is the sandbox. The real predicate
/// (<c>AH_HOME</c> under a user-owned <c>ahprobe.*</c> directory) is Ah.Platform's; a consumer that must not trust <c>AH_TEST=1</c> alone
/// is tested through <c>PlatformModule.CreateEnvironment</c>.
/// </summary>
public sealed class FakeEnvironment(IReadOnlyDictionary<string, string> variables, PluginInfo? plugin = null) : IAhEnvironment
{
    public string? GetVariable(string name) => variables.TryGetValue(name, out var v) ? v : null;

    public bool TestMode => GetVariable("AH_TEST") == "1";

    public string? GetTestOnly(string name) => TestMode ? GetVariable(name) : null;

    public bool TestModeHolds(string? ahTest, string? ahHome) =>
        ahTest == "1" && ahHome is not null
        && (ahHome.StartsWith("/tmp/ahprobe.", StringComparison.Ordinal) || ahHome.StartsWith("/private/tmp/ahprobe.", StringComparison.Ordinal));

    public string HomeDirectory => GetVariable("HOME") ?? string.Empty;

    public string AhHome => GetVariable("AH_HOME") is { Length: > 0 } h ? h : HomeDirectory + "/.claude/ah2";

    public PluginInfo? Plugin => plugin;
}

/// <summary>Keeps every record it is given.</summary>
public sealed class RecordingHookErrorLog : IHookErrorLog
{
    public List<HookErrorRecord> Records { get; } = [];

    /// <summary>The rows a hook writes only to say how a run went (message starting "trace: "), kept apart so "no error" assertions stay about errors.</summary>
    public List<HookErrorRecord> Traces { get; } = [];

    public void Log(string source, string message) => Log(new HookErrorRecord(source, message));

    public void Log(HookErrorRecord record) => (record.Message.StartsWith("trace: ", StringComparison.Ordinal) ? Traces : Records).Add(record);
}

public sealed class SequenceRandom : IRandom
{
    private uint _state = 0x9E3779B9;

    public void Fill(Span<byte> destination)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            _state = unchecked((_state * 1664525) + 1013904223);
            destination[i] = (byte)(_state >> 24);
        }
    }
}

/// <summary>One unit-table case, as authored in <c>tests/contracts/units/&lt;lib&gt;/&lt;fn&gt;.json</c>.</summary>
public sealed class UnitCase(string id, JsonObject raw)
{
    public string Id { get; } = id;

    public JsonArray Args => raw.Get("args") is { IsArray: true } a ? a.AsArray() : new JsonArray();

    /// <summary>The files the case writes: its recorded <c>files</c>, each entry replaced by the case's <c>native_files</c> entry of the same path where the case records a ruled divergence.</summary>
    public JsonObject? Files
    {
        get
        {
            if (raw.Get("files") is not { IsMap: true } recorded)
            {
                return null;
            }

            var merged = new JsonObject();
            var recordedFiles = recorded.AsObject();
            foreach (var path in recordedFiles.Keys)
            {
                merged[path] = recordedFiles[path];
            }

            if (raw.Get("native_files") is { IsMap: true } native)
            {
                var nativeFiles = native.AsObject();
                foreach (var path in nativeFiles.Keys)
                {
                    merged[path] = nativeFiles[path];
                }
            }

            return merged;
        }
    }

    public JsonObject? Env => raw.Get("env") is { IsMap: true } e ? e.AsObject() : null;

    /// <summary>Symbolic links made after the files: path to target.</summary>
    public JsonObject? Links => raw.Get("links") is { IsMap: true } l ? l.AsObject() : null;

    /// <summary>Modification times set after the files and links: path to ISO instant.</summary>
    public JsonObject? Mtimes => raw.Get("mtimes") is { IsMap: true } m ? m.AsObject() : null;

    public string? Cwd => raw.Get("cwd") is { IsText: true } c ? c.AsString() : null;

    public string? Now => raw.Get("now") is { IsText: true } n ? n.AsString() : null;

    public string? Stdin => raw.Get("stdin") is { IsText: true } s ? s.AsString() : null;

    public string? StdinBase64 => raw.Get("stdinBase64") is { IsText: true } s ? s.AsString() : null;

    /// <summary>What the native unit returns: the case's <c>native_expect</c> where the case records a ruled divergence from the Node recording, else its recorded <c>expect</c>.</summary>
    public JsonValue? Expect => raw.Get("native_expect") ?? raw.Get("expect");

    public string? ExpectPrefix => raw.Get("expectPrefix") is { IsText: true } p ? p.AsString() : null;

    /// <summary>The expected files: the recorded <c>expectFiles</c>, each entry replaced by the case's <c>native_expectFiles</c> entry of the same path where the case records a ruled divergence.</summary>
    public JsonObject? ExpectFiles
    {
        get
        {
            if (raw.Get("expectFiles") is not { IsMap: true } recorded)
            {
                return null;
            }

            var merged = new JsonObject();
            var recordedFiles = recorded.AsObject();
            foreach (var path in recordedFiles.Keys)
            {
                merged[path] = recordedFiles[path];
            }

            if (raw.Get("native_expectFiles") is { IsMap: true } native)
            {
                var nativeFiles = native.AsObject();
                foreach (var path in nativeFiles.Keys)
                {
                    merged[path] = nativeFiles[path];
                }
            }

            return merged;
        }
    }

    public bool ExpectThrows => raw.Get("expectThrows") is { IsTruthy: true };

    /// <summary>The message of the error a throwing case recorded, when the table holds one.</summary>
    public string? ExpectError => raw.Get("expectError") is { IsText: true } e ? e.AsString() : null;

    /// <summary>True when <c>"$any"</c> in <see cref="Expect"/> stands for any value (a result with a generated part).</summary>
    public bool ExpectWild => raw.Get("expectWild") is { IsTruthy: true };
}

public sealed class UnitTable
{
    public string Lib { get; private init; } = string.Empty;

    public string Fn { get; private init; } = string.Empty;

    public IReadOnlyList<UnitCase> Cases { get; private init; } = [];

    public static UnitTable Load(string path)
    {
        var doc = JsonText.Parse(RecordedNamespace.ReadAllBytes(path)).AsObject();
        var cases = new List<UnitCase>();
        foreach (var c in doc["cases"].AsArray())
        {
            cases.Add(new UnitCase(c.AsObject()["id"].AsString(), c.AsObject()));
        }

        return new UnitTable { Lib = doc["lib"].AsString(), Fn = doc["fn"].AsString(), Cases = cases };
    }

    /// <summary>Every table file under <c>tests/contracts/units</c> of the agent-hierarchy root that holds <paramref name="nativeRoot"/>.</summary>
    public static IEnumerable<string> TableFiles(string nativeRoot) =>
        Directory.EnumerateFiles(Path.Combine(nativeRoot, "..", "tests", "contracts", "units"), "*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal);
}

/// <summary>The sandbox directory of one case, and the translation of table values to and from it.</summary>
public sealed partial class UnitSandbox
{
    private static readonly Lazy<string> Parent = new(() =>
    {
        var baseDir = Directory.Exists("/private/tmp") ? "/private/tmp" : "/tmp";
        var path = $"{baseDir}/ahprobe.{Guid.NewGuid().ToString("N")[..6]}";
        Directory.CreateDirectory(path);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Ah.TestProcess.SandboxDirectory.Remove(path);
        return path;
    });

    private static int _next;

    /// <summary>
    /// This test process's start time as <c>ps -o lstart=</c> prints it in UTC, the form a registration or team record stores
    /// beside the pid; <c>$selfstart</c> in a table stands for it.
    /// </summary>
    private static readonly Lazy<string> SelfStart = new(() =>
    {
        var info = new System.Diagnostics.ProcessStartInfo("/bin/ps", ["-o", "lstart=", "-p", System.Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        info.Environment["TZ"] = "UTC";
        info.Environment["LC_ALL"] = "C";
        using var ps = System.Diagnostics.Process.Start(info)!;
        var text = ps.StandardOutput.ReadToEnd().Trim();
        ps.WaitForExit();
        return text;
    });

    private UnitSandbox(string root) => Root = root;

    public string Root { get; }

    public string Home => Root + "/home";

    public static UnitSandbox Create()
    {
        var root = $"{Parent.Value}/c{Interlocked.Increment(ref _next)}";
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(root + "/home");
        return new UnitSandbox(root);
    }

    // import: whether the planted state files are taken by the import pass (a rig that plants a dir's stores itself passes false: the
    // pass would take its gates and peers files a second time)
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void WriteFiles(UnitCase c, bool import = true)
    {
        WriteFileEntries(c);
        WriteBackPointers();
        if (c.Links is { } links)
        {
            foreach (var rel in links.Keys)
            {
                var path = Path.Combine(Root, ReplaceRoot(rel));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.CreateSymbolicLink(path, ReplaceRoot(links[rel].AsString()));
            }
        }

        if (c.Mtimes is { } mtimes)
        {
            foreach (var rel in mtimes.Keys)
            {
                var when = DateTimeOffset.Parse(mtimes[rel].AsString(), System.Globalization.CultureInfo.InvariantCulture).UtcDateTime;
                File.SetLastWriteTimeUtc(Path.Combine(Root, ReplaceRoot(rel)), when);
            }
        }

        StoreMessages(c);
        // only a case that plants a state file reads its environment here, as StoreMessages does for message files
        var planted = (c.Files?.Keys ?? []).Select(rel => Path.Combine(Root, ReplaceRoot(rel))).Where(p => TestImport.HierDirOf(p) is not null).ToList();
        if (import && planted.Count > 0)
        {
            TestImport.Planted(Environment(c).TryGetValue("AH_HOME", out var set) && set.Length > 0 ? set : Path.Combine(Home, ".claude", "ah2"), planted);
        }
    }

    /// <summary>The case's message files, as the store holds them in db mode (<see cref="TestMsgStore.Store(string, string)"/>), in the case's AH_HOME.</summary>
    private void StoreMessages(UnitCase c)
    {
        var paths = (c.Files?.Keys ?? []).Concat(c.Links?.Keys ?? []).Select(rel => Path.Combine(Root, ReplaceRoot(rel)))
            .Where(p => TestMsgStore.IsMessagePath(p) && File.Exists(p)).ToList();
        if (paths.Count == 0)
        {
            return;
        }

        var ahHome = Environment(c).TryGetValue("AH_HOME", out var set) && set.Length > 0 ? set : Path.Combine(Home, ".claude", "ah2");
        foreach (var path in paths)
        {
            TestMsgStore.Store(ahHome, path);
        }
    }

    /// <summary>
    /// Git writes <c>&lt;gitdir&gt;/gitdir</c> naming the worktree's <c>.git</c> for every linked worktree, and a pointer without it
    /// is no worktree natively. The recorded tables fabricate pointers without it, so a pointer whose git directory exists is
    /// given the back-pointer git would have written (divergence units.json, "lib-config.mainCheckoutRoot").
    /// </summary>
    private void WriteBackPointers()
    {
        foreach (var dotgit in Directory.EnumerateFiles(Root, ".git", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllText(dotgit).Split('\n');
            var at = Array.FindIndex(lines, static l => l.StartsWith("gitdir:", StringComparison.Ordinal));
            if (at < 0)
            {
                continue;
            }

            var rest = string.Join('\n', lines.Skip(at))["gitdir:".Length..].TrimStart();
            var pointer = rest.Split('\n')[0].Trim();
            if (pointer.Length == 0)
            {
                continue;
            }

            var worktreeRoot = Path.GetDirectoryName(dotgit)!;
            var gitdir = Path.GetFullPath(Path.Combine(worktreeRoot, pointer));
            var back = Path.Combine(gitdir, "gitdir");
            if (Directory.Exists(gitdir) && !File.Exists(back))
            {
                File.WriteAllText(back, dotgit + "\n");
            }
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private void WriteFileEntries(UnitCase c)
    {
        if (c.Files is null)
        {
            return;
        }

        foreach (var rel in c.Files.Keys)
        {
            var path = Path.Combine(Root, ReplaceRoot(rel));
            var content = c.Files[rel];
            if (content.IsMap && content.AsObject().Get("dir") is { IsTruthy: true })
            {
                Directory.CreateDirectory(path);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (content.IsMap && content.AsObject().Get("base64") is { IsText: true } b)
            {
                File.WriteAllBytes(path, Convert.FromBase64String(b.AsString()));
            }
            else if (content.IsMap && content.AsObject().Get("text") is { IsText: true } text)
            {
                File.WriteAllBytes(path, Ah.Core.Text.Utf8Text.Encode(ReplaceRoot(text.AsString())));
            }
            else
            {
                File.WriteAllBytes(path, Ah.Core.Text.Utf8Text.Encode(ReplaceRoot(content.AsString())));
            }

            if (content.IsMap && content.AsObject().Get("mode") is { IsNumber: true } mode)
            {
                File.SetUnixFileMode(path, (UnixFileMode)(int)mode.AsNumber());
            }
        }
    }

    /// <summary>The environment of the case: <c>HOME</c> inside the sandbox, <c>TZ</c> UTC, then the case's own values.</summary>
    public Dictionary<string, string> Environment(UnitCase c)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { ["HOME"] = Home, ["TZ"] = "UTC" };
        if (c.Env is not null)
        {
            foreach (var k in c.Env.Keys)
            {
                env[k] = ReplaceRoot(c.Env[k].AsString());
            }

            if (c.Env.ContainsKey("WRANGL_HIERARCHY_DIR") && !env.ContainsKey("AH_TEST"))
            {
                env["AH_TEST"] = "1";
            }
        }

        return env;
    }

    public string WorkingDirectory(UnitCase c) => c.Cwd is null ? Root : Path.Combine(Root, ReplaceRoot(c.Cwd));

    private string Slug => Root.Replace('/', '-');

    public string ReplaceRoot(string s) =>
        s.Replace("$ROOTSLUG", Slug, StringComparison.Ordinal)
            .Replace("$ROOT", Root, StringComparison.Ordinal)
            .Replace("$SELFPID", System.Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("$SELFSTART", s.Contains("$SELFSTART", StringComparison.Ordinal) ? SelfStart.Value : string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// ah2 reads ah v1's committed conventions path when its own is absent, which the recordings predate: the probe of that path is
    /// not part of what a recorded git log is compared on (divergence units.json, "lib-conventions.loadConventions").
    /// </summary>
    /// <summary>
    /// A registration file the native writer produced carries <c>bound</c>, which the recorded Node file does not; the comparison
    /// leaves it out of files directly under <c>ah/registrations</c> and nowhere else.
    /// </summary>
    private static string DropRegistrationBound(string relativePath, string text) =>
        relativePath.StartsWith("ah/registrations/", StringComparison.Ordinal)
        && relativePath.EndsWith(".json", StringComparison.Ordinal)
        && relativePath.IndexOf('/', "ah/registrations/".Length) < 0
            ? System.Text.RegularExpressions.Regex.Replace(text, ",\"bound\":(?:true|false)", string.Empty)
            : text;

    private static string DropV1ConventionsProbe(string text) =>
        string.Join('\n', text.Split('\n').Where(l => !l.EndsWith(":.claude/ah-conventions.json", StringComparison.Ordinal)));

    private string Contract(string s) => RecordedCliForm.Map(s.Replace(Slug, "$ROOTSLUG", StringComparison.Ordinal).Replace(Root, "$ROOT", StringComparison.Ordinal));

    /// <summary>A case argument with <c>$ROOT</c> expanded and the special tokens resolved; null is JavaScript <c>undefined</c>.</summary>
    public JsonValue? Decode(JsonValue v)
    {
        if (v.IsText)
        {
            return ReplaceRoot(v.AsString());
        }

        if (v.IsArray)
        {
            var array = new JsonArray();
            foreach (var item in v.AsArray())
            {
                array.Add(Decode(item) ?? JsonValue.Null);
            }

            return array;
        }

        if (!v.IsMap)
        {
            return v;
        }

        var o = v.AsObject();
        if (o.Get("$undefined") is not null)
        {
            return null;
        }

        if (o.Get("$nan") is not null)
        {
            return double.NaN;
        }

        if (o.Get("$inf") is not null)
        {
            return double.PositiveInfinity;
        }

        if (o.Get("$neginf") is not null)
        {
            return double.NegativeInfinity;
        }

        if (o.Get("$date") is { } d)
        {
            return d;
        }

        if (o.Get("$selfpid") is not null)
        {
            return System.Environment.ProcessId;
        }

        if (o.Get("$selfstart") is not null)
        {
            return SelfStart.Value;
        }

        var result = new JsonObject();
        foreach (var key in o.Keys)
        {
            if (Decode(o[key]) is { } decoded)
            {
                result[ReplaceRoot(key)] = decoded;
            }
        }

        return result;
    }

    /// <summary>A function result as the table records it: the sandbox directory written back as <c>$ROOT</c>, special values tagged.</summary>
    public JsonValue Encode(JsonValue? v)
    {
        if (v is null)
        {
            return new JsonObject { ["$undefined"] = 1 };
        }

        var value = v.Value;
        if (value.IsText)
        {
            return Contract(value.AsString());
        }

        if (value.IsNumber)
        {
            var n = value.AsNumber();
            if (double.IsNaN(n))
            {
                return new JsonObject { ["$nan"] = 1 };
            }

            if (double.IsPositiveInfinity(n))
            {
                return new JsonObject { ["$inf"] = 1 };
            }

            if (double.IsNegativeInfinity(n))
            {
                return new JsonObject { ["$neginf"] = 1 };
            }

            return value;
        }

        if (value.IsArray)
        {
            var array = new JsonArray();
            foreach (var item in value.AsArray())
            {
                array.Add(Encode(item));
            }

            return array;
        }

        if (value.IsMap)
        {
            var result = new JsonObject();
            foreach (var key in value.AsObject().Keys)
            {
                result[Contract(key)] = Encode(value.AsObject()[key]);
            }

            return result;
        }

        return value;
    }

    /// <summary>An expectation with only its <c>$selfpid</c> and <c>$selfstart</c> tags replaced by this process's pid and start; every other tag stays encoded.</summary>
    private static JsonValue Selves(JsonValue v)
    {
        if (v.IsArray)
        {
            var array = new JsonArray();
            foreach (var item in v.AsArray())
            {
                array.Add(Selves(item));
            }

            return array;
        }

        if (!v.IsMap)
        {
            return v;
        }

        var o = v.AsObject();
        if (o.Get("$selfpid") is not null)
        {
            return System.Environment.ProcessId;
        }

        if (o.Get("$selfstart") is not null)
        {
            return SelfStart.Value;
        }

        var result = new JsonObject();
        foreach (var key in o.Keys)
        {
            result[key] = Selves(o[key]);
        }

        return result;
    }

    /// <summary>Null when the outcome matches the case; otherwise what differed.</summary>
    public string? Verify(UnitCase c, JsonValue? result, bool threw)
    {
        if (c.ExpectThrows != threw)
        {
            return threw ? "the call threw" : "expected the call to throw";
        }

        if (!threw)
        {
            var encoded = Encode(result);
            if (c.ExpectPrefix is not null)
            {
                if (!(encoded.IsText && encoded.AsString().StartsWith(c.ExpectPrefix, StringComparison.Ordinal)))
                {
                    return $"expected a string starting {c.ExpectPrefix}, got {JsonText.Stringify(encoded, 0)}";
                }
            }
            else if (c.Expect is { } raw && Selves(raw) is var expected && (c.ExpectWild ? !WildEqual(encoded, expected) : JsonText.Stringify(expected, 0) != JsonText.Stringify(encoded, 0)))
            {
                return $"expected {JsonText.Stringify(expected, 0)}, got {JsonText.Stringify(encoded, 0)}";
            }
        }

        if (c.ExpectFiles is not null)
        {
            foreach (var rel in c.ExpectFiles.Keys)
            {
                var path = Path.Combine(Root, ReplaceRoot(rel));
                var actual = File.Exists(path)
                    ? DropRegistrationBound(rel, DropV1ConventionsProbe(Contract(Ah.Core.Text.Utf8Text.Decode(File.ReadAllBytes(path)))))
                    : null;
                var want = c.ExpectFiles[rel];
                if (want.IsMap && want.AsObject().Get("$json") is { } wantJson)
                {
                    if (actual is null || !WildEqual(TryParse(Ah.Core.Text.Utf8Text.Encode(actual)), wantJson))
                    {
                        return $"file {rel}: expected JSON {JsonText.Stringify(wantJson, 0)}, got {(actual is null ? "no file" : JsonText.Stringify(actual, 0))}";
                    }

                    continue;
                }

                var wantText = want.IsNull ? null : want.AsString();
                if (actual != wantText)
                {
                    return $"file {rel}: expected {JsonText.Stringify(want, 0)}, got {(actual is null ? "no file" : JsonText.Stringify(actual, 0))}";
                }
            }
        }

        return null;
    }

    private static JsonValue? TryParse(byte[] bytes)
    {
        try
        {
            return JsonText.Parse(bytes);
        }
        catch (JsonParseException)
        {
            return null;
        }
    }

    /// <summary>JSON equality in which the string <c>$any</c> in <paramref name="want"/> matches any value; object keys must come in the same order.</summary>
    private static bool WildEqual(JsonValue? got, JsonValue want)
    {
        if (want.IsText && want.AsString() == "$any")
        {
            return got is not null;
        }

        if (got is not { } g)
        {
            return false;
        }

        if (want.IsArray)
        {
            if (!g.IsArray || g.AsArray().Count != want.AsArray().Count)
            {
                return false;
            }

            return want.AsArray().Select((w, i) => WildEqual(g.AsArray()[i], w)).All(x => x);
        }

        if (want.IsMap)
        {
            if (!g.IsMap || !g.AsObject().Keys.SequenceEqual(want.AsObject().Keys))
            {
                return false;
            }

            return want.AsObject().Keys.All(k => WildEqual(g.AsObject()[k], want.AsObject()[k]));
        }

        return JsonText.Stringify(g, 0) == JsonText.Stringify(want, 0);
    }
}

/// <summary>
/// The recordings name the Node plugin's agents and skills (<c>ah:architect</c>, <c>/ah:agent-role</c>, <c>ah:*</c>); the native
/// plugin names its own. A table is read with that namespace rewritten, so its inputs and its expectations both carry the
/// native name: the named divergence of the agent prefix.
/// </summary>
public static partial class RecordedNamespace
{
    // The directive's literal tag `ah:<class>` is the prefix the ask gate matches, not a plugin-qualified agent type, so it keeps its spelling.
    [GeneratedRegex(@"(?<![A-Za-z0-9_-])ah:(?=[a-z*<{""])(?!<class>)", RegexOptions.CultureInvariant)]
    private static partial Regex Recorded();

    /// <summary>The end of the orphaned-team line of the state block as recorded; the native line also says how to take a team over.</summary>
    private const string OrphanLineRecorded = "their orchestrator process is gone. `roster.mjs reap` to list, `reap --commit` to remove.";

    private const string OrphanLineNative =
        "their orchestrator process is gone. To take one over, ask the user, then `roster.mjs adopt --orchestrator-pid <your pid> --team <name>` via /wrangl:agent-team; `roster.mjs reap` lists them and `reap --commit` removes them.";

    public static string Map(string text)
    {
        var mapped = Recorded().Replace(text, PluginInfo.DefaultName + ":");
        mapped = UserHierarchy().Replace(mapped, "$1/.claude/ah2/hierarchy");
        mapped = DottedName().Replace(mapped, ".claude/ah2/agent-hierarchy");
        mapped = OldHome().Replace(mapped, ".claude/ah2");
        mapped = OldProjectDir().Replace(mapped, ".claude/ah2");
        mapped = mapped.Replace(OrphanLineRecorded, OrphanLineNative, StringComparison.Ordinal);
        return Conventions().Replace(mapped, ".claude/ah2/ah-conventions.json");
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9_-])(home|pw|<ROOT:home>|~|\$HOME|\$\{HOME\})/\.claude/hierarchy(?![A-Za-z0-9._-])", RegexOptions.CultureInvariant)]
    private static partial Regex UserHierarchy();

    [GeneratedRegex(@"\.claude/agent-hierarchy(?=\.[A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex DottedName();

    [GeneratedRegex(@"\.claude/agent-hierarchy(?![A-Za-z0-9._-])", RegexOptions.CultureInvariant)]
    private static partial Regex OldHome();

    [GeneratedRegex(@"\.claude/hierarchy(?![A-Za-z0-9._-])", RegexOptions.CultureInvariant)]
    private static partial Regex OldProjectDir();

    [GeneratedRegex(@"\.claude/ah-conventions\.json", RegexOptions.CultureInvariant)]
    private static partial Regex Conventions();

    public static byte[] ReadAllBytes(string path) => Encoding.UTF8.GetBytes(Map(File.ReadAllText(path, Encoding.UTF8)));
}
