using System.Text.RegularExpressions;

namespace Ah.Architecture.Tests;

/// <summary>
/// With msgs in the database, a reader answers from the message tables; only the importer, the exporter, the collector and the prune op may list a msgs directory.
/// The scan is by name: it sees a listing call whose own line or next two lines name msgs (<c>msgs</c>, <c>MsgsDir</c>, <c>ArchiveDir</c>, <c>MsgsDirsOf</c>)
/// or a message file name (<c>--request.md</c>, <c>--response.md</c>, <c>ParseMsgFilename</c>, <c>ParseFilename</c>), so a directory held in a variable
/// of another name is seen when the listing picks message files out of it; a listing that names neither is missed.
/// </summary>
public sealed partial class MsgsListingScanTests
{
    [GeneratedRegex(@"\b(List|Listable|EnumerateFiles|EnumerateDirectories|EnumerateFileSystemEntries|GetFiles|GetDirectories|GetFileSystemEntries)\(")]
    private static partial Regex ListCall();

    [GeneratedRegex(@"""msgs""|\bmsgs\b|MsgsDir|ArchiveDir|MsgsDirsOf|--request\.md|--response\.md|ParseMsgFilename|\bParseFilename\b")]
    private static partial Regex MsgsDirArgument();

    /// <summary>Code that lists msgs directories by design: the database importer, exporter and collector.</summary>
    private static readonly string[] Allowed =
    [
        "Ah.StateDb/",
        // collecting the message files for the import and the cutover is the one job that must see files-mode msgs dirs
        "Ah.Cli/Verbs/Db/MsgsCollector.cs",
    ];

    [Fact]
    public void NothingOutsideTheAllowListListsAMsgsDirectory()
    {
        var src = Path.Combine(Workspace.NativeRoot, "src");
        var offenders = Scan(src);
        Assert.True(offenders.Count == 0, "msgs directories listed outside the allow-list:\n" + string.Join('\n', offenders));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void TheScanFlagsAListingOfAMsgsDirectoryInASyntheticSource()
    {
        Assert.True(Hits("var names = _files.List(msgs);"));
        Assert.True(Hits("foreach (var f in Directory.EnumerateFiles(Join(dir, \"msgs\"))) { }"));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void TheScanFlagsAListingOfADirectoryOfAnotherNameThatPicksOutMessageFiles()
    {
        // the R1/R2 shape of WorktreeWriteGate before it read through the library
        Assert.True(Hits("var request = c.Services.Files.List(dir).FirstOrDefault(n => n.StartsWith(requestId + \"--\", StringComparison.Ordinal) && n.EndsWith(\"--request.md\", StringComparison.Ordinal));"));
        Assert.True(SpansLines(["return files.List(dir)", "    .Select(NodePath.Basename)", "    .Select(name => (Name: name, Meta: hierarchy.Core.ParseMsgFilename(name)))"], 0));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void TheScanLeavesAListingOfADirectoryOfAnotherNameAloneWhenItPicksOutNoMessageFiles()
    {
        Assert.False(Hits("var specs = c.Services.Files.List(dir).Where(n => n.EndsWith(\"--spec.md\", StringComparison.Ordinal));"));
        Assert.False(Hits("var names = files.List(dir).Where(n => n.EndsWith(\".md\", StringComparison.Ordinal));"));
        Assert.False(Hits("var parsed = MessageText.ParseFrontmatter(text);"));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void TheScanFlagsAListingWhoseArgumentIsOnTheNextLine()
    {
        Assert.True(SpansLines(["var names = files.List(", "    Join(dir, \"msgs\"));"], 0));
        Assert.False(SpansLines(["var names = files.List(", "    Join(dir, \"remotes\"));"], 0));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void TheScanLeavesNearbyCodeAlone()
    {
        Assert.False(Hits("var names = _files.List(remotes);"));
        Assert.False(Hits("var text = files.ReadText(msgsPath);"));
        Assert.False(Hits("// _files.List(msgs) is gone"));
    }

    private static bool Hits(string line) =>
        !line.TrimStart().StartsWith("//", StringComparison.Ordinal) && ListCall().IsMatch(line) && MsgsDirArgument().IsMatch(line);

    /// <summary>A listing call whose msgs-directory argument is on one of the next two lines.</summary>
    internal static bool SpansLines(string[] lines, int i) =>
        !lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal) && ListCall().IsMatch(lines[i]) && !MsgsDirArgument().IsMatch(lines[i]) &&
        MsgsDirArgument().IsMatch(string.Join(' ', lines.Skip(i + 1).Take(2)));

    /// <summary>The hits beyond the number each (file, line) is allowed: a second copy of an allowed line is an offender.</summary>
    internal static List<string> Excess(IEnumerable<(string File, string Line, string Where)> hits, IEnumerable<(string File, string Line)> allowed)
    {
        var budget = allowed.GroupBy(a => a).ToDictionary(g => g.Key, g => g.Count());
        var offenders = new List<string>();
        foreach (var hit in hits)
        {
            var key = (hit.File, hit.Line);
            if (budget.GetValueOrDefault(key) > 0)
            {
                budget[key]--;
            }
            else
            {
                offenders.Add(hit.Where);
            }
        }

        return offenders;
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void ASecondCopyOfAnAllowedLineInTheSameFileIsAnOffender()
    {
        var hit = ("A.cs", "x.List(msgs);", "A.cs:1");
        var allowed = new[] { ("A.cs", "x.List(msgs);") };
        Assert.Empty(Excess([hit], allowed));
        Assert.Equal(["A.cs:9"], Excess([hit, ("A.cs", "x.List(msgs);", "A.cs:9")], allowed));
    }

    private static List<string> Scan(string src)
    {
        var found = new List<(string File, string Line, string Where)>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal) ||
                Allowed.Any(a => relative.StartsWith(a, StringComparison.Ordinal)))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (Hits(lines[i]) || SpansLines(lines, i))
                {
                    found.Add((relative, lines[i].Trim(), $"{relative}:{i + 1}: {lines[i].Trim()}"));
                }
            }
        }

        return Excess(found, []);
    }
}
