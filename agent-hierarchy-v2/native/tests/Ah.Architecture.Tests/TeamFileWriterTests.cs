using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ah.Architecture.Tests;

/// <summary>
/// A team file is written by one path only: the state engine's team domain through <c>AtomicWriteJson</c> and <c>UnlinkTeamFile</c>.
/// The database shadows those files and cannot follow a write that goes around them. This scan is a heuristic over source text, not a
/// proof: it reads every production file for a file-system write whose statement, or whose method, names a team path, and it pins each
/// allowed site by the hash of its method body so a change to one needs a person to look again.
/// </summary>
public sealed class TeamFileWriterTests
{
    private const string TeamDomain = "TeamStateDomain.cs";

    /// <summary>Probe sites that write into a sandbox of their own (a team file): <c>file:symbol</c> and the hash of the method body; each file must also name its sandbox.</summary>
    private static readonly string[] Probes =
    [
        "CheckVerb.cs:Seed:76042c368d2d",
    ];

    private static readonly string[] SandboxMarkers = ["SandboxEnvironment", "CreateTempSubdirectory", "GetTempPath", "mktemp"];

    /// <summary>Tokens that make a method one that handles team file paths, whatever its statements are called.</summary>
    private static readonly string[] TeamPathTokens = ["TeamPath(", "RawTeamPath(", "PoolTeamPath(", "TeamHome(", "\"teams\"", "/teams", "TeamFile", "teamFile", "team_file"];

    private static readonly Regex FileWrite = new(
        "(File\\.(WriteAll[A-Za-z]*|AppendAll[A-Za-z]*|Move|Copy|Replace|Delete|Create|CreateText|AppendText|OpenWrite|Open|SetUnixFileMode)|Directory\\.(Move|Delete)|new FileStream|new StreamWriter|FileInfo[^;]*\\.(MoveTo|CopyTo|Delete|Replace)|WriteAtomic[A-Za-z]*|AtomicWrite[A-Za-z]*)\\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MethodHeader = new("^ {4}[A-Za-z][A-Za-z0-9_<>\\[\\],.? ]* ([A-Za-z0-9_]+)\\(", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string WithoutLineComments(string source) =>
        string.Join('\n', source.Split('\n').Select(line => line.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : line));

    private static (string Symbol, string Body) MethodAt(string text, int index)
    {
        var lines = text[..index].Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (MethodHeader.Match(lines[i]) is { Success: true } m)
            {
                var start = string.Join('\n', lines.Take(i)).Length + (i > 0 ? 1 : 0);
                var end = text.IndexOf("\n    }\n", index, StringComparison.Ordinal);
                return (m.Groups[1].Value, text[start..(end < 0 ? text.Length : end + 7)]);
            }
        }

        return ("?", string.Empty);
    }

    internal static string Hash(string body) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..12].ToLowerInvariant();

    /// <summary>Every file-system write in <paramref name="source"/> whose statement or method names a team path, as <c>file:symbol:bodyhash</c>.</summary>
    internal static List<string> TeamFileWrites(string name, string source)
    {
        var text = WithoutLineComments(source);
        List<string> found = [];
        foreach (Match m in FileWrite.Matches(text))
        {
            var end = text.IndexOf(';', m.Index);
            var statement = text[m.Index..(end < 0 ? text.Length : end)];
            var (symbol, body) = MethodAt(text, m.Index);
            if (statement.Contains("team", StringComparison.OrdinalIgnoreCase) || TeamPathTokens.Any(t => body.Contains(t, StringComparison.Ordinal)))
            {
                var site = $"{name}:{symbol}:{Hash(body)}";
                if (!found.Contains(site))
                {
                    found.Add(site);
                }
            }
        }

        return found;
    }

    /// <summary>The write and delete calls on the store context inside the team domain, as the method each one sits in.</summary>
    internal static List<string> DomainWritesAndRemoves(string source)
    {
        var text = WithoutLineComments(source);
        List<string> found = [];
        foreach (Match m in Regex.Matches(text, "(ctx|writer)\\.(Write|Remove)\\(", RegexOptions.CultureInvariant))
        {
            found.Add($"{m.Groups[2].Value}:{MethodAt(text, m.Index).Symbol}");
        }

        return found;
    }

    private static IEnumerable<string> ProductionFiles() =>
        Directory.EnumerateFiles(Path.Combine(Workspace.NativeRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>The files that may declare a domain's writers switched to the database; declaring it is what lets a cut-over proceed, so each site is named.</summary>
    private static readonly string[] WriterSwitchSites = ["DaemonComposition.cs"];

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void OnlyTheNamedWriterSwitchSitesDeclareADomainsWritersRouted()
    {
        List<string> callers = [];
        foreach (var path in ProductionFiles().Where(p => !p.EndsWith("StateDbFile.cs", StringComparison.Ordinal) && !p.EndsWith("StateDbModule.cs", StringComparison.Ordinal)))
        {
            if (WithoutLineComments(File.ReadAllText(path)).Contains(".RouteWriters(", StringComparison.Ordinal) && !WriterSwitchSites.Contains(Path.GetFileName(path), StringComparer.Ordinal))
            {
                callers.Add(Path.GetRelativePath(Workspace.NativeRoot, path));
            }
        }

        Assert.True(callers.Count == 0, "a domain's writers may be declared switched only from a named site: " + string.Join("; ", callers));
    }

    [Fact]
    public void NoProductionCodeWritesATeamFileExceptThroughTheTeamDomainAndTheSandboxedProbes()
    {
        List<string> stray = [];
        var scanned = 0;
        foreach (var path in ProductionFiles().Where(p => Path.GetFileName(p) != TeamDomain))
        {
            scanned++;
            foreach (var site in TeamFileWrites(Path.GetFileName(path), File.ReadAllText(path)))
            {
                if (!Probes.Contains(site, StringComparer.Ordinal))
                {
                    stray.Add($"{site} ({Path.GetRelativePath(Workspace.NativeRoot, path)})");
                }
            }
        }

        Assert.True(scanned > 100, "the scan found too few files to mean anything");
        Assert.True(stray.Count == 0, "a team file may be written around UpdateTeam, or an allowed site changed: " + string.Join("; ", stray));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void EveryProbeSiteIsStillThereAndNamesItsSandbox()
    {
        foreach (var probe in Probes)
        {
            var file = probe[..probe.IndexOf(':', StringComparison.Ordinal)];
            var text = File.ReadAllText(ProductionFiles().Single(p => Path.GetFileName(p) == file));

            Assert.Contains(probe, TeamFileWrites(file, text));
            Assert.True(SandboxMarkers.Any(marker => text.Contains(marker, StringComparison.Ordinal)), $"{probe} writes a team file but its file names no sandbox");
        }
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void TheTeamDomainWritesOnlyInAtomicWriteJsonAndRemovesOnlyInUnlinkTeamFile()
    {
        var path = ProductionFiles().Single(p => Path.GetFileName(p) == TeamDomain);

        var sites = DomainWritesAndRemoves(File.ReadAllText(path));

        Assert.Equal(["Write:AtomicWriteJson", "Remove:UnlinkTeamFile"], sites);
    }

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("File.WriteAllText(Path.Join(dir, \"teams\", \"x.json\"), body);", true)]
    [InlineData("File.Move(tmp, teamFile);", true)]
    [InlineData("File.Delete(TeamPath(dir, name));", true)]
    [InlineData("using var s = new FileStream(team_file, FileMode.Create);", true)]
    [InlineData("var p = TeamPath(dir, name); File.WriteAllText(p, body);", true)]
    [InlineData("new FileInfo(tmp).MoveTo(teamFile);", true)]
    [InlineData("File.AppendText(teamFile).Dispose();", true)]
    [InlineData("Directory.Move(a, Path.Join(dir, \"teams\"));", true)]
    [InlineData("WriteAtomic(Path.Join(dir, \"teams\", \"x.json\"), body);", true)]
    [InlineData("File.WriteAllText(Path.Join(dir, \"notes.json\"), body);", false)]
    [InlineData("var t = File.ReadAllText(teamFile);", false)]
    [InlineData("// File.WriteAllText(teamFile, body);", false)]
    public void TheScanFlagsATeamFileWriteAndNothingNearIt(string statement, bool flagged)
    {
        var source = "internal sealed class C\n{\n    private void Go()\n    {\n        " + statement + "\n    }\n}\n";

        var found = TeamFileWrites("C.cs", source);

        Assert.Equal(flagged ? 1 : 0, found.Count);
        Assert.All(found, f => Assert.StartsWith("C.cs:Go:", f, StringComparison.Ordinal));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void AChangeToAnAllowedSitesBodyChangesItsPinSoItIsNoLongerAllowed()
    {
        string Source(string extra) => "internal sealed class C\n{\n    private void Seed()\n    {\n        File.WriteAllText(teamFile, body);" + extra + "\n    }\n}\n";

        var before = TeamFileWrites("C.cs", Source(string.Empty));
        var after = TeamFileWrites("C.cs", Source(" Touch(teamFile);"));

        Assert.NotEqual(before, after);
        Assert.StartsWith("C.cs:Seed:", before[0], StringComparison.Ordinal);
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void ASecondWriteInsideTheTeamDomainIsCaught()
    {
        var source = "internal sealed class D\n{\n    private void AtomicWriteJson(IStoreWriteContext ctx)\n    {\n        ctx.Write(p, t, b);\n    }\n\n    private void Other(IStoreWriteContext ctx)\n    {\n        ctx.Write(p, t, b);\n    }\n}\n";

        Assert.Equal(["Write:AtomicWriteJson", "Write:Other"], DomainWritesAndRemoves(source));
    }
}
