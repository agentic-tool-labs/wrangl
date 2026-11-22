using System.Text;
using Ah.Core.Json;

namespace Ah.Hooks.Tests;

public sealed partial class HooksHostTests
{
    // ---------------------------------------------------------------- the dispatch row a mailed or delivered brief records

    private const string BriefId = "20261016-120000-ab12";

    private static string BriefDir(Rig rig) => rig.Sandbox.Root + "/wt/.claude/ah2/msgs";

    /// <summary>A brief file under the rig's sandbox: <paramref name="kind"/> is <c>request</c> or <c>response</c>.</summary>
    private static string Brief(Rig rig, string kind = "request", bool exists = true)
    {
        var dir = BriefDir(rig);
        _ = Directory.CreateDirectory(dir);
        var path = $"{dir}/{BriefId}--implementor--t1--{kind}.md";
        if (exists)
        {
            File.WriteAllText(path, $"---\nid: {BriefId}\ntype: {kind}\n---\n");
        }

        return path;
    }

    private static List<JsonValue> DispatchRowsWritten(Rig rig)
    {
        var rows = new List<JsonValue>();
        foreach (var (call, args) in rig.Writer.Calls.Zip(rig.Writer.Arguments))
        {
            if (call != "peer-records.append")
            {
                continue;
            }

            var rec = JsonText.Parse(Encoding.UTF8.GetBytes(args)).Member("rec")!.Value;
            rows.AddRange(rec.IsArray ? rec.AsArray() : [rec]);
        }

        return [.. rows.Where(r => r.Member("type") is { IsText: true } t && t.AsString() == "dispatch")];
    }

    [Fact]
    public void AMailedBriefRecordsADispatchRowWithItsPathAndAddress()
    {
        var root = MakePluginRoot();
        var rig = new Rig(null, root);
        var path = Brief(rig);

        var outcome = rig.Run("pretooluse-ah-cli", BashAs(string.Empty, $"node {root}/hooks/msg.mjs send m@team --ref {path}"));

        Assert.Equal("allow", DecisionOf(outcome));
        var row = Assert.Single(DispatchRowsWritten(rig));
        Assert.Equal(BriefId, row.Member("request_id")!.Value.AsString());
        Assert.Equal(path, row.Member("path")!.Value.AsString());
        Assert.Equal("m@team", row.Member("to_addr")!.Value.AsString());
        Assert.Equal("implementor", row.Member("to")!.Value.AsString());
        Assert.Equal("sess-1", row.Member("session_id")!.Value.AsString());
    }

    /// <summary>ah2-296: a bare id resolves as the send resolves it; negative: an id no request has records no row and is still allowed.</summary>
    [Theory]
    [InlineData(BriefId, true)]
    [InlineData("20261016-120000-zz99", false)]
    public void ABareIdRefRecordsTheRequestsRowAndNegativeAnUnknownIdRecordsNone(string reference, bool recorded)
    {
        var root = MakePluginRoot();
        var rig = new Rig(null, root);
        var path = Brief(rig);

        var outcome = rig.Run("pretooluse-ah-cli", BashAs(string.Empty, $"node {root}/hooks/msg.mjs send m@team --ref {reference} --cwd {rig.Sandbox.Root}/wt"));

        Assert.Equal("allow", DecisionOf(outcome));
        Assert.Equal(recorded ? [path] : [], DispatchRowsWritten(rig).Select(static r => r.Member("path")!.Value.AsString()).ToArray());
    }

    /// <summary>N2: a bare id resolves with the verb's <c>--pool</c> and <c>--kind</c>; negative: a report's bare id names the response and records no row.</summary>
    [Fact]
    public void ABareIdRefResolvesWithThePoolAndAReportsIdRecordsNoRow()
    {
        var root = MakePluginRoot();
        var rig = new Rig(null, root);
        string Plant(string pool)
        {
            var dir = $"{rig.Sandbox.Root}/{pool}/.claude/ah2/msgs";
            _ = Directory.CreateDirectory(dir);
            var path = $"{dir}/{BriefId}--implementor--t1--request.md";
            File.WriteAllText(path, $"---\nid: {BriefId}\ntype: request\n---\n");
            return path;
        }

        _ = Plant("pA");
        var inB = Plant("pB");

        _ = rig.Run("pretooluse-ah-cli", BashAs(string.Empty, $"node {root}/hooks/msg.mjs send m@team --ref {BriefId} --pool {rig.Sandbox.Root}/pB/.claude/ah2 --cwd {rig.Sandbox.Root}/wt"));

        Assert.Equal([inB], DispatchRowsWritten(rig).Select(static r => r.Member("path")!.Value.AsString()).ToArray());

        var other = new Rig(null, root);
        _ = Brief(other);
        _ = Brief(other, "response");

        _ = other.Run("pretooluse-ah-cli", BashAs(string.Empty, $"node {root}/hooks/msg.mjs send orchestrator@team --kind report --ref {BriefId} --cwd {other.Sandbox.Root}/wt"));

        Assert.Empty(DispatchRowsWritten(other));
    }

    [Fact]
    public void ARelativeRefIsMadeAbsoluteAgainstTheCommandsCwd()
    {
        var root = MakePluginRoot();
        var rig = new Rig(null, root);
        var path = Brief(rig);

        _ = rig.Run("pretooluse-ah-cli", BashAs(string.Empty, $"node {root}/hooks/msg.mjs send m@team --ref {Path.GetFileName(path)} --cwd {BriefDir(rig)}"));

        Assert.Equal(path, Assert.Single(DispatchRowsWritten(rig)).Member("path")!.Value.AsString());
    }

    [Fact]
    public void AReportSendRecordsNoRowWhateverItsKind()
    {
        var root = MakePluginRoot();
        var rig = new Rig(null, root);
        var response = Brief(rig, "response");

        _ = rig.Run("pretooluse-ah-cli", BashAs(string.Empty, $"node {root}/hooks/msg.mjs send orchestrator@team --kind report --ref {response}"));

        Assert.Empty(DispatchRowsWritten(rig));
    }

    [Fact]
    public void ASendFromASubagentRecordsNoRow()
    {
        var root = MakePluginRoot();
        var rig = new Rig(null, root);
        var path = Brief(rig);

        _ = rig.Run("pretooluse-ah-cli", BashAsSubagent("wrangl:implementor", $"node {root}/hooks/msg.mjs send m@team --ref {path}"));

        Assert.Empty(DispatchRowsWritten(rig));
    }

    [Fact]
    public void ASendNamingNoRefOrAMissingFileRecordsNoRowAndIsStillAllowed()
    {
        var root = MakePluginRoot();
        var rig = new Rig(null, root);
        var missing = Brief(rig, exists: false);

        var withoutRef = rig.Run("pretooluse-ah-cli", BashAs(string.Empty, $"node {root}/hooks/msg.mjs send m@team --text hi"));
        var withMissing = rig.Run("pretooluse-ah-cli", BashAs(string.Empty, $"node {root}/hooks/msg.mjs send m@team --ref {missing}"));

        Assert.Equal("allow", DecisionOf(withoutRef));
        Assert.Equal("allow", DecisionOf(withMissing));
        Assert.Empty(DispatchRowsWritten(rig));
    }

    [Fact]
    public void ADeliverRecordsTheRequestsPathAndNoAddress()
    {
        var root = MakePluginRoot();
        var rig = new Rig(null, root);
        var path = Brief(rig);

        _ = rig.Run("pretooluse-ah-cli", BashAs(string.Empty, $"node {root}/hooks/roster.mjs deliver m --req {path}"));

        var row = Assert.Single(DispatchRowsWritten(rig));
        Assert.Equal(path, row.Member("path")!.Value.AsString());
        Assert.Null(row.Member("to_addr"));
    }

    [Fact]
    public void AWaitOnlyDeliverRecordsNoRow()
    {
        var root = MakePluginRoot();
        var rig = new Rig(null, root);
        var path = Brief(rig);

        _ = rig.Run("pretooluse-ah-cli", BashAs(string.Empty, $"node {root}/hooks/roster.mjs deliver m --req {path} --wait-only"));

        Assert.Empty(DispatchRowsWritten(rig));
    }
}
