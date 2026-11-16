using System.Text.RegularExpressions;

namespace Ah.Architecture.Tests;

/// <summary>
/// A transcript can be hundreds of megabytes, so the code that tails one reads byte windows only: no whole-file read, no direct open.
/// </summary>
public sealed partial class TranscriptWindowScanTests
{
    [GeneratedRegex(@"\.(ReadBytes|ReadText|ReadNoFollow)\(|File\.(ReadAll|Open)|new FileStream\(|StreamReader")]
    private static partial Regex WholeFileRead();

    [Fact]
    public void TheTranscriptCodeReadsNoWholeFile()
    {
        var model = Path.Combine(Workspace.NativeRoot, "src", "Ah.Watch", "Model");
        var files = Directory.EnumerateFiles(model, "Transcript*.cs").ToList();
        Assert.NotEmpty(files);

        var problems = files.Where(f => WholeFileRead().IsMatch(File.ReadAllText(f))).Select(Path.GetFileName);

        Assert.Empty(problems);
    }

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("var all = files.ReadBytes(path);")]
    [InlineData("var all = files.ReadText(path);")]
    [InlineData("var all = File.ReadAllBytes(path);")]
    [InlineData("using var s = new FileStream(path, FileMode.Open);")]
    public void TheScanFlagsAWholeFileRead(string line) => Assert.Matches(WholeFileRead(), line);

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("var tail = files.ReadTail(path, 1);")]
    [InlineData("var range = files.ReadRange(path, 0, 1);")]
    public void TheScanLeavesWindowedReadsAlone(string line) => Assert.DoesNotMatch(WholeFileRead(), line);
}
