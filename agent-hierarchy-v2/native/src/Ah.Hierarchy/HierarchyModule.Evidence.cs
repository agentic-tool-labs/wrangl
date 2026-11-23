using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ah.Abstractions;

namespace Ah.Hierarchy;

/// <summary>One evidence file a fill copies (adr-39): its number <c>e&lt;K&gt;</c>, where it is copied from, its stored name, its type and its size.</summary>
public sealed record EvidenceFile(int K, string Source, string Name, string Type, long Bytes);

public static partial class HierarchyModule
{
    /// <summary>Checks <paramref name="sources"/> against the evidence limits and numbers them after the files message <paramref name="id"/> already holds; throws <see cref="ArgumentException"/> naming the file and the limit.</summary>
    public static IReadOnlyList<EvidenceFile> EvidencePlan(IFileReader files, string ahHome, string id, IReadOnlyList<string> sources) => Evidence.Plan(files, ahHome, id, sources);

    /// <summary>The generated <c>## evidence</c> section for <paramref name="planned"/>.</summary>
    public static string EvidenceSection(string id, IReadOnlyList<EvidenceFile> planned) => Evidence.Section(id, planned);

    /// <summary>Copies <paramref name="planned"/> into the message's evidence directory, read-only; a copy that fails removes what this call copied and rethrows.</summary>
    public static void EvidenceStore(string ahHome, string id, IReadOnlyList<EvidenceFile> planned) => Evidence.Store(ahHome, id, planned);

    /// <summary>Removes the files <paramref name="planned"/> stored, for a fill that failed after the copy.</summary>
    public static void EvidenceRemove(string ahHome, string id, IReadOnlyList<EvidenceFile> planned) => Evidence.Remove(ahHome, id, planned);

    /// <summary>The stored file of <c>&lt;id&gt;/e&lt;k&gt;</c>, or null when either part is not of its shape or no such file is stored.</summary>
    public static string? EvidenceFind(string ahHome, string id, string ek) => Evidence.Find(ahHome, id, ek);

    /// <summary>The type an evidence file is served with, from its extension.</summary>
    public static string EvidenceType(string name) => Evidence.TypeOf(name);

    /// <summary>The number of evidence files under the ah home, and their bytes.</summary>
    public static (int Files, long Bytes) EvidenceTotals(string ahHome) => Evidence.Totals(ahHome);

    /// <summary>A size as the evidence section shows it: B, KiB or MiB.</summary>
    public static string EvidenceSize(long bytes) => Evidence.Size(bytes);
}

/// <summary>
/// Report evidence (adr-39): <c>ah msg fill --evidence</c> copies files into <c>&lt;ah home&gt;/evidence/&lt;msg id&gt;/e&lt;k&gt;-&lt;name&gt;</c>, read-only,
/// outside every checkout. Nothing here copies evidence out, parses it or runs it.
/// </summary>
internal static partial class Evidence
{
    internal const int MaxFiles = 20;

    internal const long MaxFileBytes = 20L * 1024 * 1024;

    internal const long MaxMessageBytes = 50L * 1024 * 1024;

    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private const UnixFileMode SealedDir = FileMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private const UnixFileMode OpenDir = SealedDir | UnixFileMode.UserWrite;

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["png"] = "image/png", ["jpg"] = "image/jpeg", ["jpeg"] = "image/jpeg", ["gif"] = "image/gif", ["webp"] = "image/webp", ["svg"] = "image/svg+xml",
        ["wav"] = "audio/wav", ["mp3"] = "audio/mpeg", ["ogg"] = "audio/ogg", ["flac"] = "audio/flac",
        ["mp4"] = "video/mp4", ["webm"] = "video/webm", ["mov"] = "video/quicktime",
        ["txt"] = "text/plain", ["log"] = "text/plain", ["md"] = "text/markdown", ["json"] = "application/json", ["csv"] = "text/csv", ["pdf"] = "application/pdf",
    };

    [GeneratedRegex("^e([0-9]{1,6})\\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyRegex();

    [GeneratedRegex("^e([0-9]{1,6})-", RegexOptions.CultureInvariant)]
    private static partial Regex StoredRegex();

    internal static string TypeOf(string name) => Types.TryGetValue(Path.GetExtension(name).TrimStart('.'), out var type) ? type : "application/octet-stream";

    private static string Root(string ahHome) => Path.Join(ahHome, "evidence");

    private static string DirOf(string ahHome, string id) => Path.Join(Root(ahHome), id);

    /// <summary>The files a message's evidence <paramref name="dir"/> already holds, as (k, bytes).</summary>
    private static List<(int K, long Bytes)> Stored(string dir) =>
        !Directory.Exists(dir)
            ? []
            : [.. new DirectoryInfo(dir).EnumerateFiles().Select(f => (Match: StoredRegex().Match(f.Name), f.Length)).Where(s => s.Match.Success)
                .Select(s => (int.Parse(s.Match.Groups[1].Value, CultureInfo.InvariantCulture), s.Length))];

    internal static IReadOnlyList<EvidenceFile> Plan(IFileReader files, string ahHome, string id, IReadOnlyList<string> sources)
    {
        if (!MessageText.IsId(id))
        {
            throw new ArgumentException($"--evidence: {id} is not a message id");
        }

        var stored = Stored(DirOf(ahHome, id));
        var k = stored.Count == 0 ? 0 : stored.Max(s => s.K);
        var total = stored.Sum(s => s.Bytes);
        var planned = new List<EvidenceFile>();
        foreach (var source in sources)
        {
            if (stored.Count + planned.Count >= MaxFiles)
            {
                throw new ArgumentException($"--evidence: {source}: at most {MaxFiles} files per message");
            }

            if (files.Stat(source, followSymlinks: true) is not { } stat)
            {
                throw new ArgumentException($"--evidence: {source}: no such file");
            }

            if (stat.Kind != FileKind.File)
            {
                throw new ArgumentException($"--evidence: {source}: not a regular file");
            }

            if (stat.Size > MaxFileBytes)
            {
                throw new ArgumentException($"--evidence: {source}: {Size(stat.Size)}; at most {Size(MaxFileBytes)} per file");
            }

            total += stat.Size;
            if (total > MaxMessageBytes)
            {
                throw new ArgumentException($"--evidence: {source}: at most {Size(MaxMessageBytes)} per message across parts");
            }

            try
            {
                using var probe = File.OpenRead(source);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new ArgumentException($"--evidence: {source}: not readable", e);
            }

            var name = Path.GetFileName(source).Replace("/", string.Empty, StringComparison.Ordinal).Replace("\\", string.Empty, StringComparison.Ordinal);
            name = string.Concat(name.Select(static c => char.IsControl(c) ? '?' : c)); // E1: a name cannot inject lines
            planned.Add(new EvidenceFile(++k, source, name, TypeOf(name), stat.Size));
        }

        return planned;
    }

    internal static string Section(string id, IReadOnlyList<EvidenceFile> planned) =>
        "## evidence\n" + string.Concat(planned.Select(f => $"- [e{f.K}] {f.Name} · {f.Type} · {Size(f.Bytes)} · evidence:{id}/e{f.K}\n"));

    // only the name may hold " · "; type and size cannot, so the match is pinned to the last three separators
    [GeneratedRegex("^- \\[e([0-9]{1,6})\\] .+ · [^·]+ · [^·]+ · evidence:(\\S+)/e\\1\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex LineRegex();

    /// <summary>The longest line <see cref="TrailingSection"/> matches (F3c): a file name's limit plus a type, a size and a ref.</summary>
    private const int MaxLineBytes = 600;

    /// <summary>
    /// The trailing <see cref="Section"/> of a stored <paramref name="body"/>: the byte offset its heading line starts at and its text
    /// without the closing newline; null when the body does not end in that heading and 1 to <see cref="MaxFiles"/> lines of its shape.
    /// </summary>
    internal static (int Start, string Text)? TrailingSection(byte[] body)
    {
        var end = body.Length;
        while (end > 0 && body[end - 1] is (byte)'\n' or (byte)'\r')
        {
            end--;
        }

        var lines = new List<string>();
        while (end > 0 && lines.Count <= MaxFiles)
        {
            var start = Array.LastIndexOf(body, (byte)'\n', end - 1) + 1;
            if (end - start > MaxLineBytes)
            {
                return null;
            }

            var line = Encoding.UTF8.GetString(body, start, end - start).TrimEnd('\r');
            if (line == "## evidence" && lines.Count > 0)
            {
                lines.Add(line);
                lines.Reverse();
                return (start, string.Join('\n', lines));
            }

            Match m;
            try
            {
                m = LineRegex().Match(line);
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }

            if (!m.Success || !MessageText.IsId(m.Groups[2].Value))
            {
                return null;
            }

            lines.Add(line);
            end = start - 1;
        }

        return null;
    }

    private static string StoredPath(string dir, EvidenceFile f) => Path.Join(dir, $"e{f.K}-{f.Name}");

    // ponytail: two fills of one message at once can pick the same k; the second copy then fails on the existing name and is rolled back
    internal static void Store(string ahHome, string id, IReadOnlyList<EvidenceFile> planned)
    {
        var dir = DirOf(ahHome, id);
        _ = Directory.CreateDirectory(dir);
        Chmod(dir, OpenDir);
        var made = new List<string>();
        try
        {
            foreach (var f in planned)
            {
                var to = StoredPath(dir, f);
                File.Copy(f.Source, to, overwrite: false);
                made.Add(to);
                Chmod(to, FileMode);
            }
        }
        catch
        {
            foreach (var path in made)
            {
                File.Delete(path);
            }

            throw;
        }
        finally
        {
            Chmod(dir, SealedDir);
        }
    }

    internal static void Remove(string ahHome, string id, IReadOnlyList<EvidenceFile> planned)
    {
        var dir = DirOf(ahHome, id);
        if (!Directory.Exists(dir))
        {
            return;
        }

        Chmod(dir, OpenDir);
        try
        {
            foreach (var f in planned)
            {
                File.Delete(StoredPath(dir, f));
            }
        }
        finally
        {
            Chmod(dir, SealedDir);
        }
    }

    internal static string? Find(string ahHome, string id, string ek)
    {
        if (!MessageText.IsId(id) || KeyRegex().Match(ek) is not { Success: true } key)
        {
            return null;
        }

        var dir = DirOf(ahHome, id);
        var prefix = $"e{int.Parse(key.Groups[1].Value, CultureInfo.InvariantCulture)}-";
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir).FirstOrDefault(p => Path.GetFileName(p).StartsWith(prefix, StringComparison.Ordinal)) : null;
    }

    internal static (int Files, long Bytes) Totals(string ahHome)
    {
        var root = Root(ahHome);
        if (!Directory.Exists(root))
        {
            return (0, 0);
        }

        var all = new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        return (all.Count, all.Sum(f => f.Length));
    }

    internal static string Size(long bytes) =>
        bytes < 1024 ? $"{bytes} B"
        : bytes < 1024 * 1024 ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(bytes / 1024.0)} KiB")
        : string.Create(CultureInfo.InvariantCulture, $"{Math.Round(bytes / (1024.0 * 1024), 1)} MiB");

    private static void Chmod(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }
}
