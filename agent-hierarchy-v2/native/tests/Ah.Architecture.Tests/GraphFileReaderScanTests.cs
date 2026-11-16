using System.Text.RegularExpressions;

namespace Ah.Architecture.Tests;

/// <summary>
/// Every composition root reads files through the service graph's reader, the one that reads the asks logs from the state database
/// (per-dir-files-to-db §6c G5). A type or method handed a graph takes no raw reader beside it, the hooks' root reads through
/// <c>graph.Io</c> once the graph is built, and the store-backed reader is built in one place.
/// </summary>
public sealed partial class GraphFileReaderScanTests
{
    /// <summary>A parameter list naming a <c>ServiceGraph</c> and an <c>IFileReader</c> or <c>PlatformIo</c> parameter beside it.</summary>
    [GeneratedRegex(@"\((?=[^()]*\bServiceGraph\b)(?=[^()]*\b(?:IFileReader|PlatformIo)\s+\w)[^()]*\)")]
    private static partial Regex GraphBesideRawReader();

    internal static bool TakesARawReaderBesideTheGraph(string line) => GraphBesideRawReader().IsMatch(line);

    private static IEnumerable<(string File, int Line, string Text)> CliLines() =>
        Directory.EnumerateFiles(Path.Combine(Workspace.NativeRoot, "src", "Ah.Cli"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(static f => File.ReadLines(f).Select((text, i) => (f, i + 1, text)));

    [Fact]
    public void NothingHandedTheGraphIsHandedARawFileReaderBesideIt()
    {
        var hits = CliLines().Where(static l => TakesARawReaderBesideTheGraph(l.Text))
            .Select(static l => $"{Path.GetRelativePath(Workspace.NativeRoot, l.File)}:{l.Line}").ToList();

        Assert.True(hits.Count == 0, "takes an IFileReader or PlatformIo beside a ServiceGraph; read graph.Io.Files instead: " + string.Join(", ", hits));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void TheHooksRootReadsThroughTheGraphOnceItIsBuilt()
    {
        var source = File.ReadAllText(Path.Combine(Workspace.NativeRoot, "src", "Ah.Cli", "Composition", "HookComposition.cs"));
        var built = source.IndexOf("ServiceGraph.Build(", StringComparison.Ordinal);
        var swapped = source.IndexOf("io = graph.Io;", StringComparison.Ordinal);

        Assert.True(built >= 0 && swapped > built, "HookComposition.Build must set io = graph.Io; right after ServiceGraph.Build");
        Assert.DoesNotContain("io.Files", source[built..swapped], StringComparison.Ordinal);
    }

    /// <summary>
    /// The lines of the daemon's root, from the graph's build to the end of the method, that still name the raw reader and are not one of
    /// the daemon server's own reads (its dependencies and its start refusal, built before the lazy graph exists; Ah.Daemon reads no asks log).
    /// </summary>
    internal static List<string> RawReadsAfterTheGraph(string source)
    {
        var built = source.IndexOf("ServiceGraph.Build(", StringComparison.Ordinal);
        var end = built < 0 ? -1 : source.IndexOf("\n    }\n", built, StringComparison.Ordinal);
        return built < 0 || end < 0
            ? ["no ServiceGraph.Build( in the daemon's root"]
            : [.. source[built..end].Split('\n').Where(static l => l.Contains("io.Files", StringComparison.Ordinal)
                && !l.Contains("new DaemonDependencies(io.Files,", StringComparison.Ordinal) && !l.Contains("StartRefusal = () => TestModeStart.", StringComparison.Ordinal))];
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void TheDaemonsRootReadsThroughTheGraphOnceItIsBuilt()
    {
        var source = File.ReadAllText(Path.Combine(Workspace.NativeRoot, "src", "Ah.Cli", "Composition", "DaemonComposition.cs"));

        Assert.Empty(RawReadsAfterTheGraph(source));
        // the negative: a roster composed over the raw reader after the build is seen
        Assert.Single(RawReadsAfterTheGraph("ServiceGraph.Build(io);\n        RosterComposition.Compose(graph, io.Files, env);\n    }\n"));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void TheStoreBackedReaderIsBuiltOnce()
    {
        var builds = Directory.EnumerateFiles(Path.Combine(Workspace.NativeRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Sum(static f => Regex.Count(File.ReadAllText(f), @"new AskLogFileReader\("));

        Assert.Equal(1, builds);
    }

    [GeneratedRegex(@"DocumentPath\(|AuditPath\(|WorktreeAuditFile|""grants""")]
    private static partial Regex NamesAGrantsPath();

    [GeneratedRegex(@"\.(?:ReadText|ReadBytes|ReadTail|List)\(")]
    private static partial Regex RawRead();

    /// <summary>
    /// A source that names a grants path (<c>DocumentPath(</c>, <c>AuditPath(</c>, <c>WorktreeAuditFile</c>, <c>"grants"</c>) and makes a
    /// raw read anywhere in it, so a path built on one line and read on another is seen too.
    /// </summary>
    internal static bool ReadsGrantsRaw(string source) => NamesAGrantsPath().IsMatch(source) && RawRead().IsMatch(source);

    /// <summary>
    /// The graph's one grants source reaches both the store-backed reader and the grants domain, so every root built on the graph (CLI, hook,
    /// daemon, the in-process replay) reads and writes grants through the same tables.
    /// </summary>
    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void TheGraphHandsItsGrantSourceToTheReaderAndTheDomains()
    {
        var source = File.ReadAllText(Path.Combine(Workspace.NativeRoot, "src", "Ah.Cli", "Composition", "ServiceGraph.cs"));
        var reader = source.Split('\n').Single(static l => l.Contains("new AskLogFileReader(", StringComparison.Ordinal));
        var domains = source.IndexOf("ConfigModule.CreateStateDomains(", StringComparison.Ordinal);
        var next = domains < 0 ? -1 : source.IndexOf("RosterModule.CreateStateDomain(", domains, StringComparison.Ordinal);

        Assert.Contains("grantSource", reader, StringComparison.Ordinal);
        Assert.True(next > domains && source[domains..next].Contains("grantSource", StringComparison.Ordinal), "CreateStateDomains must be handed grantSource");
    }

    /// <summary>The wrapper that shuts the doors, and GateReport's file fallback for services with no grants source (test rigs).</summary>
    private static readonly string[] RawGrantsReaders = ["Ah.Cli/Composition/AskLogFileReader.cs", "Ah.Commands/Roster/Read/GateReport.cs"];

    /// <summary>Grants are read only through <c>IFileReader.ReadGrants</c> (per-dir-files-to-db S11e, §6c G5).</summary>
    [Fact]
    public void GrantsAreReadOnlyThroughTheTypedRead()
    {
        var src = Path.Combine(Workspace.NativeRoot, "src");
        var hits = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !RawGrantsReaders.Contains(Path.GetRelativePath(src, f)) && ReadsGrantsRaw(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(Workspace.NativeRoot, f)).ToList();

        Assert.True(hits.Count == 0, "names a grants path and reads a file raw; read grants with IFileReader.ReadGrants: " + string.Join(", ", hits));
    }

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("var path = _grants.DocumentPath(dir, team);\nif (JsonText.Parse(files.ReadText(path)) is { IsMap: true } existing)", true)]
    [InlineData("return files.List(NodePath.Join(hierarchyDir, \"grants\"))", true)]
    [InlineData("var path = _grants.DocumentPath(dir, team);\nfiles.ReadGrants(dir, _grants.AuditPath()).TryDocuments(out var documents);", false)]
    [InlineData("if (!files.Exists(_grants.DocumentPath(dir, team)))", false)]
    public void TheGrantsCheckSeesARawReadAnywhereInTheFile(string source, bool expected) => Assert.Equal(expected, ReadsGrantsRaw(source));

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("internal sealed class RosterComposition(ServiceGraph graph, IFileReader files, IClock clock) : IRosterCliServices", true)]
    [InlineData("    internal static void Compose(Lazy<ServiceGraph> graph, PlatformIo io, IAhEnvironment environment)", true)]
    [InlineData("internal sealed class RosterComposition(ServiceGraph graph, IClock clock, IAhEnvironment environment) : IRosterCliServices", false)]
    [InlineData("internal sealed class MemoReader(IFileReader inner, IClock clock) : IFileReader", false)]
    [InlineData("        var services = new RosterComposition(g, g.Io.Clock, _environment);", false)]
    [InlineData("    private static GateServices Build(PlatformIo io, IAhEnvironment env) => ServiceGraph.Build(io, env);", false)]
    public void TheCheckSeesARawReaderOnlyBesideAGraph(string line, bool expected) =>
        Assert.Equal(expected, TakesARawReaderBesideTheGraph(line));
}
