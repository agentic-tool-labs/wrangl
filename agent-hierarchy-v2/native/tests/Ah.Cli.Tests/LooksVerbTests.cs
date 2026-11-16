using Ah.Cli.Verbs;
using Ah.Cli.Verbs.AhdCtl;
using Ah.Core.Json;
using Ah.Core.Ui;

namespace Ah.Cli.Tests;

/// <summary><c>ah looks import</c>: the merge the page's route uses, from a file; a corrupt looks file is left alone.</summary>
public sealed class LooksVerbTests : IDisposable
{
    private const string Input = """{"custom":[{"id":"a","name":"Mine","mode":"dark-emissive","profiles":{"shadow":{},"dual":{}}}]}""";

    private readonly string _home = Directory.CreateTempSubdirectory("ah-looks-verb.").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string Looks => Path.Combine(_home, "looks.json");

    private (int Code, string Out, string Err) Import(string text)
    {
        var file = Path.Combine(_home, "in.json");
        File.WriteAllText(file, text);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = LooksVerb.Import(_home, file, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AMergeOfTheCleanedLookAddsNoCopyOfOneAnOlderBuildMerged(bool keptInFile)
    {
        const string Cleaned = """{"custom":[{"id":"a","name":"Mine","mode":"dark-emissive","profile":"shadow","graphShadow":false,"profiles":{"shadow":{},"dual":{}}}]}""";
        var first = LooksFile.Merge(_home, JsonText.Parse(Input));
        Assert.Equal(1, first.Added);
        if (!keptInFile)
        {
            var empty = JsonText.Parse("""{"active":{},"technique":"shadow","custom":[]}""");
            Assert.Equal(LooksOutcome.Ok, LooksFile.Write(_home, first.Rev, empty).Outcome);
        }

        var second = LooksFile.Merge(_home, JsonText.Parse(Cleaned));

        Assert.Equal((0, 1), (second.Added, second.Already));
        Assert.Equal(keptInFile ? 1 : 0, second.Looks!.Get("custom")!.Value.AsArray().Count);
    }

    [Fact]
    public void AMergeKeepsALookThatDiffersByItsProfile()
    {
        _ = LooksFile.Merge(_home, JsonText.Parse(Input));
        var dual = LooksFile.Merge(_home, JsonText.Parse("""{"custom":[{"id":"a","name":"Mine","mode":"dark-emissive","profile":"dual","profiles":{"shadow":{},"dual":{}}}]}"""));

        Assert.Equal(1, dual.Added);
    }

    [Fact]
    public void ImportAddsTheLooksAndAgainAddsNothing()
    {
        var first = Import(Input);
        var second = Import(Input);

        Assert.Equal(0, first.Code);
        Assert.Contains("1 added", first.Out, StringComparison.Ordinal);
        Assert.Contains(Looks, first.Out, StringComparison.Ordinal);
        Assert.Contains("Mine", File.ReadAllText(Looks), StringComparison.Ordinal);
        Assert.Equal(0, second.Code);
        Assert.Contains("0 added", second.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportBringsBackALookThePageMergedAndTheFileNoLongerHolds()
    {
        _ = Ah.Core.Ui.LooksFile.Merge(_home, Ah.Core.Json.JsonText.Parse(Input));
        var rev = Ah.Core.Ui.LooksFile.Read(_home).Rev;
        _ = Ah.Core.Ui.LooksFile.Write(_home, rev, Ah.Core.Json.JsonText.Parse("""{"custom":[]}"""));

        var page = Ah.Core.Ui.LooksFile.Merge(_home, Ah.Core.Json.JsonText.Parse(Input));
        Assert.Equal(0, page.Added);

        var (code, output, _) = Import(Input);

        Assert.Equal(0, code);
        Assert.Contains("1 added", output, StringComparison.Ordinal);
        Assert.Contains("Mine", File.ReadAllText(Looks), StringComparison.Ordinal);
    }

    [Fact]
    public void ImportTakesARawOpticsValueAndLeavesItsSeedOut()
    {
        var (code, output, _) = Import("""
            {"skin":"glass","technique":"dual","active":{"light":"light-inky","dark":"a"},
             "custom":[{"id":"a","name":"Mine","theme":"dark","mode":"dark-emissive","builtin":false,"profiles":{"shadow":{},"dual":{}},"backdrop":{},"ink":{}}]}
            """);

        Assert.Equal(0, code);
        Assert.Contains("1 added", output, StringComparison.Ordinal);
        var text = File.ReadAllText(Looks);
        Assert.Contains("\"dual\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("skin", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACorruptLooksFileIsLeftAlone()
    {
        File.WriteAllText(Looks, "{bad");

        var (code, _, err) = Import(Input);

        Assert.Equal(1, code);
        Assert.Contains("left for you", err, StringComparison.Ordinal);
        Assert.Equal("{bad", File.ReadAllText(Looks));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"custom":"x"}""")]
    [InlineData("[]")]
    public void AnInputThatIsNotALooksObjectChangesNothing(string text)
    {
        var (code, _, _) = Import(text);

        Assert.Equal(1, code);
        Assert.False(File.Exists(Looks));
    }

    [Fact]
    public void AGrantThatMovedSaysSoInOneLineAndOneThatDidNotSaysNothing()
    {
        var moved = UiVerb.MovedNote("""{"port":5001,"moved_from":5000}"""u8, 5001);

        Assert.NotNull(moved);
        Assert.Contains("5000", moved, StringComparison.Ordinal);
        Assert.Contains("5001", moved, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', moved);
        Assert.Null(UiVerb.MovedNote("""{"port":5001}"""u8, 5001));
    }

    [Fact]
    public void AMissingInputIsAnErrorAndNotAnEmptyMerge()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        Assert.Equal(1, LooksVerb.Import(_home, Path.Combine(_home, "absent.json"), stdout, stderr));
        Assert.False(File.Exists(Looks));
    }
}
