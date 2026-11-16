using System.Text.RegularExpressions;

namespace Ah.Architecture.Tests;

/// <summary>
/// A rule that derives a value lives in the daemon's composer, and both front ends render the served field. Each row here is a rule that has moved:
/// no function of that name may come back in the web UI or the TUI. Each slice of the parity work adds the names it moves.
/// </summary>
public sealed class NoClientDerivationTests
{
    private static readonly string[] Moved =
    [
        // S0: team and stream status, from plans-model.js.
        "teamStatus",
        "statusOf",

        // S1: a member's context, liveness, process line and state, and the open briefs' flags, from select.js.
        // stateOf and needsCount move later (S6, S5); windowOf and stateOf name other functions in rows.js, looks.js and plans-seen.js.
        "contextOf",
        "pidLive",
        "processLine",
        "stateFrom",
        "inFlightOf",
        "unattendedOf",

        // S2: board rows and the backlog, from plans-model.js. S4: graph.js's columnOf, the same name.
        "columnOf",
        "checklistOf",
        "queueOf",
        "backlogOf",
        "waveOf",

        // S3b: a transcript's rows and the lines an edit changes, from transcript-rows.js and conversation.js.
        "rowsOf",
        "isCut",
        "diffLines",

        // S6: a timeline's spans and lanes, from timeline.js. Its marksOf is scanned below: sitrep and plans each have their own of that name.
        "spansOf",
        "buildLanes",
    ];

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void TheTimelineDefinesNoMarksOfAndNegativeTheSitrepsIsItsOwn()
    {
        var files = ClientFiles().ToList();
        Assert.False(Defines(File.ReadAllText(files.Single(static f => Path.GetFileName(f) == "timeline.js")), "marksOf"));
        Assert.True(Defines(File.ReadAllText(files.Single(static f => Path.GetFileName(f) == "sitrep-seen.js")), "marksOf"));
    }

    /// <summary>A definition of <paramref name="name"/> in JavaScript or C#; a read of a field or a parameter of that name is no definition.</summary>
    private static bool Defines(string code, string name) =>
        Regex.IsMatch(code, $@"\bfunction\s+{name}\b|\b(?:const|let|var)\s+{name}\s*=|\b(?:static|internal|private|public)\b[^;\n=]*\s{name}\s*\(");

    private static IEnumerable<string> ClientFiles()
    {
        var web = Path.Combine(Workspace.NativeRoot, "..", "web", "ui");
        return Directory.EnumerateFiles(web, "*.js", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(Workspace.NativeRoot, "src", "Ah.Tui"), "*.cs", SearchOption.AllDirectories)
                .Where(static f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void NoFrontEndDefinesARuleThatHasMovedToTheDaemon()
    {
        var files = ClientFiles().ToList();
        Assert.NotEmpty(files);
        var found = files.SelectMany(f => Moved.Where(name => Defines(File.ReadAllText(f), name)).Select(name => $"{name} in {f}")).ToList();
        Assert.True(found.Count == 0, string.Join('\n', found));
    }

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("export function columnOf(item) { return 1; }")]
    [InlineData("const columnOf = (item) => 1;")]
    [InlineData("internal static string columnOf(JsonValue item) => string.Empty;")]
    public void TheMutantThatDefinesTheRuleAgainFails(string code) => Assert.True(Defines(code, "columnOf"));

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("const c = item.columnOf;")]
    [InlineData("export function streamOf(raw, columnOf = 1) { return raw; }")]
    [InlineData("var x = row.columnOf(y);")]
    public void NegativeReadingAFieldOfThatNameIsNoDefinition(string code) => Assert.False(Defines(code, "columnOf"));
}
