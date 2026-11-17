using System.Text.RegularExpressions;

namespace Ah.Cli.Verbs;

/// <summary>
/// <c>ah herdr install [--uninstall | --replace-layout]</c>: copies the plugin's <c>herdr-desktop/mods/ah2/</c> and <c>herdr-desktop/layouts/wrangl.json</c>
/// into the herdr desktop home (<c>$HERDR_APP_HOME</c>, else <c>~/.config/herdr-app</c>) and marks them owned with
/// <c>mods/ah2/.ah2-owned</c>: line 1 the plugin version, then one <c>&lt;path&gt; sha256 &lt;hex&gt;</c> per file written. No
/// recursive delete: a file is overwritten or removed only while it still has its recorded hash; anything else is kept. A target
/// that exists without a marker that parses is never touched. The layout was <c>layouts/ah2.json</c> before the rename: an install
/// removes a recorded, unedited one (the rule for any file no longer shipped) and says so when an edited one stays.
/// Exit codes: 0 done, 1 refused or failed, 2 usage.
/// </summary>
internal static partial class HerdrVerb
{
    internal const string Marker = ".ah2-owned";
    internal const string MarkerTmp = Marker + ".tmp";
    private const string ModRel = "mods/ah2";
    private const string LayoutRel = "layouts/wrangl.json";
    private const string OldLayoutRel = "layouts/ah2.json";

    internal static int Run(CliContext context, IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(context);
        var uninstall = args is ["install", "--uninstall"];
        var replaceLayout = args is ["install", "--replace-layout"];
        if (!uninstall && !replaceLayout && args is not ["install"])
        {
            stderr.WriteLine("ah herdr: usage: ah herdr install [--uninstall | --replace-layout]");
            return 2;
        }

        if (Ah.Hooks.HooksModule.HerdrAppHome(context.Env) is not { } home)
        {
            stderr.WriteLine($"ah herdr: HERDR_APP_HOME must be an absolute path, not {context.Env.GetVariable("HERDR_APP_HOME")}; nothing changed");
            return 2;
        }

        var mod = Full(home, ModRel);
        var marker = Path.Combine(mod, Marker);
        var layout = Full(home, LayoutRel);
        var oldLayout = Full(home, OldLayoutRel);
        var owned = File.Exists(marker);
        if ((Directory.Exists(mod) || File.Exists(layout)) && !owned)
        {
            stderr.WriteLine($"ah herdr: {mod} or {layout} exists and was not installed by ah; nothing changed");
            return 1;
        }

        try
        {
            var recorded = owned ? ParseMarker(marker) : new Dictionary<string, string>(StringComparer.Ordinal);
            if (recorded is null)
            {
                stderr.WriteLine($"ah herdr: the marker {marker} is not one ah wrote; nothing changed");
                return 1;
            }

            if (uninstall)
            {
                if (!owned)
                {
                    stdout.WriteLine($"ah herdr: nothing installed in {home}");
                    return 0;
                }

                foreach (var (rel, hash) in recorded)
                {
                    var path = Full(home, rel);
                    if (File.Exists(path) && Sha256(path) == hash)
                    {
                        File.Delete(path);
                    }
                }

                ReportKept(mod, [layout, oldLayout], marker, keep: _ => true, stderr);
                Prune(mod, marker);
                stdout.WriteLine($"ah herdr: uninstalled from {home}");
                return 0;
            }

            var source = context.Env.Plugin?.Root is { Length: > 0 } root ? Path.Combine(root, "herdr-desktop") : null;
            var sourceMod = source is null ? null : Path.Combine(source, "mods", "ah2");
            var sourceLayout = source is null ? null : Path.Combine(source, "layouts", "wrangl.json");
            if (sourceMod is null || sourceLayout is null || !Directory.Exists(sourceMod) || !File.Exists(sourceLayout))
            {
                stderr.WriteLine($"ah herdr: the plugin has no herdr-desktop/mods/ah2 and layouts/wrangl.json{(source is null ? string.Empty : " under " + source)}");
                return 1;
            }

            // rel → source file, for every file the plugin ships.
            var shipped = Directory.GetFiles(sourceMod, "*", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f) != Marker)
                .ToDictionary(f => ModRel + "/" + Path.GetRelativePath(sourceMod, f).Replace('\\', '/'), f => f, StringComparer.Ordinal);
            shipped[LayoutRel] = sourceLayout;

            // A target is written when it is missing, still has its recorded hash, or already equals the shipped file (so a crash
            // between the marker write and the copy self-heals); otherwise it is kept and stays recorded as before.
            var write = shipped.Where(s => !File.Exists(Full(home, s.Key)) || Sha256(Full(home, s.Key)) is var t && ((recorded.TryGetValue(s.Key, out var h) && t == h) || t == Sha256(s.Value)))
                .ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);

            // --replace-layout: the existing layout moves to a timestamped backup beside it (never overwritten), then is written fresh.
            if (replaceLayout && File.Exists(layout))
            {
                File.Move(layout, $"{layout}.bak-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}", overwrite: false);
                write[LayoutRel] = sourceLayout;
            }

            // --replace-layout also sets a recorded old-name layout aside, edited or not; below it is then gone and forgotten.
            var oldAside = replaceLayout && recorded.ContainsKey(OldLayoutRel) && File.Exists(oldLayout);
            if (oldAside)
            {
                File.Move(oldLayout, $"{oldLayout}.bak-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}", overwrite: false);
            }

            var entries = recorded.Where(r => !write.ContainsKey(r.Key)).ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
            foreach (var (rel, from) in write)
            {
                entries[rel] = Sha256(from);
            }

            // A recorded file the plugin no longer ships goes, while it still has its recorded hash.
            foreach (var (rel, hash) in recorded.Where(r => !shipped.ContainsKey(r.Key)))
            {
                var path = Full(home, rel);
                if (File.Exists(path) && Sha256(path) == hash)
                {
                    File.Delete(path);
                    entries.Remove(rel);
                }
            }

            if (oldAside)
            {
                entries.Remove(OldLayoutRel);
            }

            Directory.CreateDirectory(mod);
            var tmp = Path.Combine(mod, MarkerTmp);
            File.WriteAllText(tmp, $"{context.Env.Plugin?.Version ?? "unknown"}\n{string.Concat(entries.Select(e => $"{e.Key} sha256 {e.Value}\n"))}");
            File.Move(tmp, marker, overwrite: true);
            foreach (var (rel, from) in write)
            {
                var to = Full(home, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to, overwrite: true);
            }

            ReportKept(mod, [layout, oldLayout], marker, keep: path => !write.ContainsKey(Rel(home, path)), stderr);
            if (File.Exists(oldLayout) && recorded.ContainsKey(OldLayoutRel))
            {
                stderr.WriteLine("ah herdr: the layout is now wrangl.json; your edited ah2.json stays beside it and is no longer updated");
            }

            if (!write.ContainsKey(LayoutRel) && File.Exists(layout) && DeskMissing(sourceLayout, layout) is { Count: > 0 } missing)
            {
                stderr.WriteLine($"ah herdr: layout wrangl.json is missing the Desk column ({missing[0]}); run 'ah herdr install --replace-layout' to replace it (a backup is kept)");
            }

            Prune(mod, marker);
            stdout.WriteLine($"ah herdr: installed into {home}; switch with layout.switch wrangl (ctrl-shift-l)");
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"ah herdr: {e.Message}");
            return 1;
        }
    }

    [GeneratedRegex("^(?<path>.+) sha256 (?<hash>[0-9a-f]{64})$")]
    private static partial Regex EntryLine();

    /// <summary>
    /// The marker's recorded hashes, or null unless it is exactly a non-empty version line, at least one entry under
    /// <c>mods/ah2/</c> or a layout (<c>layouts/wrangl.json</c>, or the old <c>layouts/ah2.json</c>), and a final newline.
    /// </summary>
    private static Dictionary<string, string>? ParseMarker(string marker)
    {
        var text = File.ReadAllText(marker);
        if (!text.EndsWith('\n'))
        {
            return null;
        }

        var lines = text[..^1].Split('\n');
        if (lines.Length < 2 || lines[0].Trim().Length == 0)
        {
            return null;
        }

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            var m = EntryLine().Match(line);
            var rel = m.Groups["path"].Value;
            var inTree = rel is LayoutRel or OldLayoutRel || (rel.StartsWith(ModRel + "/", StringComparison.Ordinal) && rel != ModRel + "/" + Marker && rel != ModRel + "/" + MarkerTmp);
            if (!m.Success || !inTree || rel.IndexOfAny(['\\', ':']) >= 0 || rel.Split('/').Any(s => s is "" or "." or "..") || !entries.TryAdd(rel, m.Groups["hash"].Value))
            {
                return null;
            }
        }

        return entries;
    }

    /// <summary>One stderr line for each file left in the mod tree or at a layout path that <paramref name="keep"/> selects.</summary>
    private static void ReportKept(string mod, string[] layouts, string marker, Func<string, bool> keep, TextWriter stderr)
    {
        var left = (Directory.Exists(mod) ? Directory.GetFiles(mod, "*", SearchOption.AllDirectories) : [])
            .Where(f => f != marker && f != Path.Combine(mod, MarkerTmp))
            .Concat(layouts.Where(File.Exists))
            .Where(keep);
        foreach (var path in left.Order(StringComparer.Ordinal))
        {
            stderr.WriteLine($"ah herdr: {path} was modified after install and was kept (edited, or not written by ah)");
        }
    }

    /// <summary>Removes empty directories under <paramref name="mod"/>, then the marker and the directory once nothing else is left.</summary>
    private static void Prune(string mod, string marker)
    {
        if (!Directory.Exists(mod))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(mod, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }

        var tmp = Path.Combine(mod, MarkerTmp);
        if (Directory.EnumerateFileSystemEntries(mod).All(e => e == marker || e == tmp))
        {
            File.Delete(marker);
            File.Delete(tmp);
            Directory.Delete(mod);
        }
    }

    /// <summary>
    /// The Desk ids (every id starting <c>ah2-panel</c>: the column and its stack) the shipped layout has and the installed one
    /// lacks. An installed layout that is not JSON has none of them.
    /// </summary>
    private static List<string> DeskMissing(string shipped, string installed)
    {
        var have = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            Ids(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(installed)), have);
        }
        catch (System.Text.Json.JsonException)
        {
            // not JSON: no ids
        }

        var want = new HashSet<string>(StringComparer.Ordinal);
        Ids(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(shipped)), want);
        return [.. want.Where(id => id.StartsWith("ah2-panel", StringComparison.Ordinal) && !have.Contains(id)).Order(StringComparer.Ordinal)];
    }

    private static void Ids(System.Text.Json.Nodes.JsonNode? node, HashSet<string> into)
    {
        if (node is System.Text.Json.Nodes.JsonObject o)
        {
            if (o["id"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var id))
            {
                into.Add(id);
            }

            if (o["children"] is System.Text.Json.Nodes.JsonArray children)
            {
                foreach (var c in children)
                {
                    Ids(c, into);
                }
            }
        }
    }

    private static string Full(string home, string rel) => Path.Combine(home, rel.Replace('/', Path.DirectorySeparatorChar));

    private static string Rel(string home, string path) => Path.GetRelativePath(home, path).Replace('\\', '/');

    private static string Sha256(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
}
