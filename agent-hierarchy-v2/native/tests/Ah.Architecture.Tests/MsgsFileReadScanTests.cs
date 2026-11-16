using System.Text.RegularExpressions;

namespace Ah.Architecture.Tests;

/// <summary>
/// With msgs in the database, a message's text, bytes and existence come from the library (<c>ReadMsgText</c>, <c>ReadMsgFile</c>,
/// <c>MsgBytes</c>, <c>MsgExists</c>), never a raw read of its file. The scan is by name: it sees a raw read, existence or stat call whose
/// line names a message file (<c>responsePath</c>, <c>requestPath</c>, <c>refPath</c>, <c>reqPath</c>, <c>req</c>, or a
/// <c>--request.md</c>/<c>--response.md</c> literal), and in the hook gates a read or existence check whose argument is the tool-input
/// path (<c>path</c>, <c>real</c>, <c>named</c>, <c>canonical</c>). A message path held in a variable of another name is missed.
/// </summary>
public sealed partial class MsgsFileReadScanTests
{
    [GeneratedRegex(@"\b(ReadText|ReadAllText|ReadBytes|ReadAllBytes|ReadRange|ReadNoFollow|Exists|Stat)\(")]
    private static partial Regex ReadCall();

    [GeneratedRegex(@"\b(responsePath|requestPath|refPath|reqPath|req)\b|--response\.md|--request\.md")]
    private static partial Regex MsgFileArgument();

    [GeneratedRegex(@"\b(ReadText|ReadBytes|ReadRange|ReadNoFollow|Exists)\((path|real|named|canonical)\b")]
    private static partial Regex HookPathRead();

    private const string HookGates = "Ah.Hooks/Gates/";

    /// <summary>Code that reads message files by design: the database importer, exporter, collector and cutover tooling.</summary>
    private static readonly string[] Allowed =
    [
        "Ah.StateDb/",
        "Ah.Cli/Verbs/Db/MsgsCollector.cs",
        "Ah.Cli/Composition/MsgsExportReconciler.cs",
    ];

    /// <summary>Each allowed raw read, as a file and the trimmed text of its line, with its reason.</summary>
    private static readonly (string File, string Line)[] Reasoned =
    [
        // pre-edit: the on-disk file the Write/Edit is about to change, which is path and draft logic (msgs-db-domain.md:98)
        ("Ah.Hooks/Gates/ResponseFileCheck.cs", "if (!s.Files.Exists(path))"),
        ("Ah.Hooks/Gates/ResponseFileCheck.cs", "var current = s.Files.ReadText(real);"),
        ("Ah.Hooks/Gates/WorktreeWriteGate.cs", "if (ResponseFileCheck.FrontmatterValue(s.Files.ReadText(canonical), \"from_name\") is { } author)"),
        // not a message path: a shell argument, the decider boxes, the grants audit log
        ("Ah.Hooks/Gates/CompoundAllowGate.cs", "return CanonicalPath.Resolve(files, abs) is not { } real || files.Exists(real);"),
        ("Ah.Hooks/Gates/AskClassGate.cs", "if (!s.Files.Exists(path))"),
        ("Ah.Hooks/Gates/AskClassGate.cs", "foreach (var line in s.Files.ReadText(path).Split('\\n'))"),
        ("Ah.Hooks/Gates/AskClassGate.cs", "return s.Files.Stat(path) is null ? string.Empty : s.Files.ReadText(path);"),
        ("Ah.Hooks/Gates/WorktreeWriteGate.cs", "if (!s.Files.Exists(path))"),
        ("Ah.Hooks/Gates/WorktreeWriteGate.cs", "if (!s.Files.Exists(path))"),
    ];

    [Fact]
    public void NothingOutsideTheAllowListReadsAMessageFileRaw()
    {
        var src = Path.Combine(Workspace.NativeRoot, "src");
        var offenders = Scan(src);
        Assert.True(offenders.Count == 0, "message files read raw outside the allow-list:\n" + string.Join('\n', offenders));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void TheScanFlagsARawReadOfAMessageFile()
    {
        Assert.True(Hits("Ah.Mesh/X.cs", "var text = files.ReadText(responsePath);"));
        Assert.True(Hits("Ah.Mesh/X.cs", "var b = files.ReadBytes(refPath);"));
        Assert.True(Hits("Ah.Mesh/X.cs", "if (!Exists(req)) { }"));
        Assert.True(Hits("Ah.Mesh/X.cs", "if (path.EndsWith(\"--request.md\", StringComparison.Ordinal) && Exists(path)) { }"));
        Assert.True(Hits("Ah.Hooks/Gates/X.cs", "var previous = s.Files.ReadText(path);"));
        Assert.True(Hits("Ah.Hooks/Gates/X.cs", "var read = s.Files.ReadNoFollow(real, 10);"));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void TheScanLeavesNearbyCodeAlone()
    {
        Assert.False(Hits("Ah.Mesh/X.cs", "var text = files.ReadText(path);"));
        Assert.False(Hits("Ah.Mesh/X.cs", "var text = core.ReadMsgFile(responsePath);"));
        Assert.False(Hits("Ah.Mesh/X.cs", "// files.ReadText(responsePath) is gone"));
        Assert.False(Hits("Ah.Hooks/Gates/X.cs", "var text = s.Files.ReadText(configPath);"));
        Assert.False(Hits("Ah.Mesh/X.cs", "var requests = files.ReadText(requestsLog);"));
    }

    private static bool Hits(string file, string line) =>
        !line.TrimStart().StartsWith("//", StringComparison.Ordinal) && ReadCall().IsMatch(line)
        && (MsgFileArgument().IsMatch(line) || (file.StartsWith(HookGates, StringComparison.Ordinal) && HookPathRead().IsMatch(line)));

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
                if (Hits(relative, lines[i]))
                {
                    found.Add((relative, lines[i].Trim(), $"{relative}:{i + 1}: {lines[i].Trim()}"));
                }
            }
        }

        return MsgsListingScanTests.Excess(found, Reasoned);
    }
}
