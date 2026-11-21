using System.Text.RegularExpressions;

namespace Ah.Architecture.Tests;

/// <summary>
/// b459 C1 (ADR adr-22, guard G6): the status compute reads the world only through <c>IStatusSource</c> and the <c>now</c> it is
/// given, so that what it observed can be recorded and replayed. A read of the clock, the config, the disk or a process from inside
/// the compute file would be an observation no witness sees, and a restamped document would hide its change for up to the cap.
/// </summary>
public sealed class StatusComputeScanTests
{
    private const string ComputeFile = "src/Ah.Hierarchy/Hier/HierLib.StatusCompute.cs";

    private static readonly string[] Forbidden =
    [
        "NowMs", "_clock", "_files", "_config", "Exists(", "DateTime", "Environment.", "File.", "Directory.", "Process.",
        // HierLib's own readers: static ones the compiler would let a static compute call, and the fields behind the rest
        "_core", "_roster", "_statusEntries", "ReadTextOrNull", "MtimeMs", "Attributed(", "ReadActivity(",
    ];

    /// <summary>A member declared at class level without <c>static</c> or <c>const</c>: it could reach every reader HierLib has.</summary>
    private static readonly Regex InstanceMember = new(@"^    (?:private|internal|public|protected)\b(?!.*\b(?:static|const)\b)", RegexOptions.CultureInvariant);

    /// <summary>
    /// How many times the compute file names <c>now</c> or <c>nowMs</c> outside comments. A new use is a new clock rule or stamp: it must
    /// feed the horizon (or be a stamp <c>HierarchyModule.StatusClockStamps</c> rewrites) and gain a parity fixture in
    /// <c>StatusWitnessTests</c>; then move this number.
    /// </summary>
    // 26, then 30 with pane-subagents D3: the subagent TTL rule in SubagentsOf (its parameter, its comparison, its two callers), feeding the horizon at
    // started + TTL + 1 and witnessed by StatusWitnessTests' subagent rows
    private const int NowUses = 30;

    private static IEnumerable<string> Code(string text) => text.Split('\n').Where(static l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal));

    private static List<string> Problems(string text) =>
        [.. Code(text).SelectMany(static (line, i) => Forbidden.Where(t => line.Contains(t, StringComparison.Ordinal)).Select(t => $"line {i + 1} names {t}: {line.Trim()}")
            .Concat(InstanceMember.IsMatch(line) ? [$"line {i + 1} is not static, so the compiler does not keep it to the seam: {line.Trim()}"] : []))];

    private static int CountNow(string text) => Code(text).Sum(static l => Regex.Count(l, @"\b(?:now|nowMs)\b", RegexOptions.CultureInvariant));

    [Fact]
    public void TheStatusComputeReadsNothingOutsideItsSourceAndEveryUseOfNowIsCounted()
    {
        var text = File.ReadAllText(Path.Combine(Workspace.NativeRoot, ComputeFile));
        var problems = Problems(text);
        Assert.True(problems.Count == 0, ComputeFile + ": the compute must ask IStatusSource, so the witness sees it." + Environment.NewLine + string.Join(Environment.NewLine, problems));
        Assert.True(CountNow(text) == NowUses, $"{ComputeFile} names now/nowMs {CountNow(text)} times, not {NowUses}: a new use must feed the horizon and gain a parity fixture.");
    }

    [Theory]
    [InlineData("var now = NowMs;")]
    [InlineData("var on = _config.ResolveConfig(cwd);")]
    [InlineData("if (Exists(dir))")]
    [InlineData("var at = DateTimeOffset.UtcNow;")]
    [InlineData("var text = File.ReadAllText(path);")]
    [InlineData("var pid = Environment.ProcessId;")]
    [InlineData("var text = ReadTextOrNull(path);")]
    [InlineData("var at = MtimeMs(path);")]
    [InlineData("var life = _core.Liveness?.ForMember(name, team);")]
    [InlineData("var record = _roster.ReadTeamRecord(dir, team);")]
    [InlineData("var who = Attributed(roster, name, team, null, registration);")]
    public void NegativeAPlantedOutsideReadIsFoundAndAPlantedUseOfNowMovesTheCount(string planted)
    {
        var text = File.ReadAllText(Path.Combine(Workspace.NativeRoot, ComputeFile));
        Assert.Single(Problems(text + "\n        " + planted + "\n"));
        Assert.Equal(CountNow(text) + 1, CountNow(text + "\n        if (dueMs <= nowMs)\n"));
        // a comment is not a read
        Assert.Empty(Problems(text + "\n        // was: " + planted + "\n"));
        // an instance member could reach any reader of the class without naming a listed one
        Assert.Single(Problems(text + "\n    private JsonArray Another(IStatusSource src) => [];\n"));
        Assert.Empty(Problems(text + "\n    private static JsonArray Another(IStatusSource src) => [];\n    private const int Cap = 3;\n"));
    }
}
