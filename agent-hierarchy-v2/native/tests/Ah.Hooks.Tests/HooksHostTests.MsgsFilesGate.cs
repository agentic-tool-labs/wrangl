using Ah.Core.Json;

namespace Ah.Hooks.Tests;

/// <summary>The <c>msgs-files</c> rule: a file tool on a pool's message path is told the command, because messages live in the store (ah-msgs-db-only §6.8).</summary>
public sealed partial class HooksHostTests
{
    private const string MsgsId = "20261103-005522-12h1";
    private const string MsgsPool = "/w/.claude/ah2/msgs/";

    private static string FileTool(string tool, string path, string agent = "wrangl:implementor") =>
        Event(tool, agent, new JsonObject { ["file_path"] = path, ["content"] = "x" });

    [Theory]
    [InlineData("Read")]
    [InlineData("Write")]
    [InlineData("Edit")]
    [InlineData("MultiEdit")]
    public void MsgsFilesBouncesAFileToolOnAMessagePathEveryTime(string tool)
    {
        var rig = SoftRig();
        var bad = FileTool(tool, MsgsPool + MsgsId + "--impl--s--request.md");
        foreach (var _ in Enumerable.Range(0, 4))
        {
            Assert.True(IsDenied(rig.Run("pretooluse-chain", bad)));
        }

        Assert.Empty(SoftRows(rig, "giveup"));
    }

    [Fact]
    public void MsgsFilesCoversTheArchiveAndANotebookAndNamesTheIdInItsFix()
    {
        var rig = SoftRig();
        var first = rig.Run("pretooluse-chain", FileTool("Read", MsgsPool + "archive/" + MsgsId + "--impl--s--response.md"));
        Assert.True(IsDenied(first));
        Assert.Contains("NOT DONE: messages live in ahd, not in files.", Reason(first), StringComparison.Ordinal);
        Assert.Contains($"read with `ah msg show {MsgsId} [response]`; write with `ah msg fill --id {MsgsId} --from <role> --body-file -`.", Reason(first), StringComparison.Ordinal);

        Assert.True(IsDenied(SoftRig().Run("pretooluse-chain", Event("NotebookEdit", "wrangl:implementor", new JsonObject { ["notebook_path"] = MsgsPool + "x.ipynb" }))));
    }

    [Fact]
    public void MsgsFilesWritesALiteralIdForANameThatIsNoMessage() =>
        Assert.Contains("ah msg show <id> [response]", Reason(SoftRig().Run("pretooluse-chain", FileTool("Read", MsgsPool + "old-export.md"))), StringComparison.Ordinal);

    [Theory]
    [InlineData("/w/src/msgs/Foo.cs")]
    [InlineData("/w/.claude/ah2/notes/x.md")]
    [InlineData("/w/.claude/ah2/msgs/sub/x.md")]
    [InlineData("/w/.claude/ah2/archive/x.md")]
    [InlineData("/w/.claude/ah2x/msgs/x.md")]
    public void NegativeMsgsFilesLeavesAPathOutsideAMessageDirectoryAlone(string path)
    {
        var rig = SoftRig();

        Assert.Equal(string.Empty, Chain(rig, FileTool("Read", path)));
        Assert.Empty(SoftRows(rig));
    }

    [Fact]
    public void NegativeMsgsFilesLeavesOtherToolsAlone()
    {
        var rig = SoftRig();

        Assert.Equal(string.Empty, Chain(rig, Event("Grep", "wrangl:implementor", new JsonObject { ["path"] = MsgsPool + MsgsId + "--impl--s--request.md", ["pattern"] = "x" })));
        Assert.Empty(SoftRows(rig));
    }

    [Fact]
    public void ReviewerWriteHasNoResponseFileCarveOutInTheStore()
    {
        var first = SoftRig().Run("pretooluse-chain", WriteAs("wrangl:reviewer", "/w/review/" + MsgsId + "--reviewer--s--response.md"));

        Assert.True(IsDenied(first));
        Assert.Contains("ah msg fill --id <id> --from reviewer --body-file -", Reason(first), StringComparison.Ordinal);
    }
}
