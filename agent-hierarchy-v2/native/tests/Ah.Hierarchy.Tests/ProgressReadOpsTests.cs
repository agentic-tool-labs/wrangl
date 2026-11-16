using System.Text;
using Ah.Core.Json;

namespace Ah.Hierarchy.Tests;

/// <summary>progress.keys and progress.events: the teams of a pool and the rows appended to a team's plan logs since a remembered offset.</summary>
public sealed class ProgressReadOpsTests
{
    private static string Folder(HierarchyReadOpsTests.Rig rig, string team)
    {
        var folder = Path.Combine(rig.Pool, "progress", team);
        _ = Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Line(int n, int pad = 0) =>
        $$"""{"v":1,"ts":"2026-10-18T00:00:{{n % 60:D2}}Z","team":"t","stream":"s","item":"i{{n}}","event":"add","title":"T{{n}}","pad":"{{new string('x', pad)}}","by":"o","session":null}""" + "\n";

    private static JsonArray Streams(HierarchyReadOpsTests.Rig rig, string team, JsonObject? offsets = null)
    {
        var args = new JsonObject { ["dir"] = rig.Pool, ["team"] = team };
        if (offsets is not null)
        {
            args["after_offsets"] = offsets;
        }

        Ah.TestKit.TestImport.Dir(rig.AhHome, rig.Pool);
        return HierarchyReadOpsTests.Parsed(HierarchyReadOpsTests.Read(rig, "progress", "events", args)).Member("streams")!.Value.AsArray();
    }

    private static JsonValue Stream(JsonArray streams, string name) => streams.Single(s => s.Member("stream")!.Value.AsString() == name);

    // per-dir-files-to-db S11c: a row after the old file's take is appended through the domain's write, and offsets are row seqs
    private static void Append(HierarchyReadOpsTests.Rig rig, string stream, string lines)
    {
        var rows = new JsonArray();
        foreach (var line in lines.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            rows.Add(JsonText.Parse(line));
        }

        var appended = HierarchyReadOpsTests.Parsed(rig.Writer.Invoke(
            "progress", "plan-append", JsonText.ToUtf8(new JsonObject { ["dir"] = rig.Pool, ["team"] = "t", ["stream"] = stream, ["rows"] = rows }, 0),
            new Ah.Abstractions.RequestContext(rig.Repo, rig.Pool, string.Empty, string.Empty)));
        Assert.Equal(rows.Count, (int)appended.Member("appended")!.Value.AsNumber());
    }

    [Fact]
    public void KeysListsTheTeamFoldersAndLeavesOutHiddenOnesFilesAndTeamsThatAreOff()
    {
        var rig = HierarchyReadOpsTests.Make();
        Assert.Empty(HierarchyReadOpsTests.Parsed(HierarchyReadOpsTests.Read(rig, "progress", "keys", new JsonObject { ["dir"] = rig.Pool })).Member("teams")!.Value.AsArray());

        _ = Folder(rig, "beta");
        _ = Folder(rig, "alpha");
        _ = Folder(rig, ".hidden");
        File.WriteAllText(Path.Combine(rig.Pool, "progress", "notes.txt"), "x");
        _ = Folder(rig, "silent");
        Ah.TestKit.TestProgressDocs.Off(rig.AhHome, rig.Pool, "silent");

        var teams = HierarchyReadOpsTests.Parsed(HierarchyReadOpsTests.Read(rig, "progress", "keys", new JsonObject { ["dir"] = rig.Pool })).Member("teams")!.Value.AsArray().Select(t => t.AsString());

        Assert.Equal(["alpha", "beta"], teams);
        Assert.False(HierarchyReadOpsTests.Read(rig, "progress", "keys", new JsonObject()).Ok);
    }

    [Fact]
    public void EventsGivesEachStreamsRowsAndALaterReadFromItsOffsetGivesOnlyTheNewRows()
    {
        var rig = HierarchyReadOpsTests.Make();
        var folder = Folder(rig, "t");
        File.WriteAllText(Path.Combine(folder, "a.plan.jsonl"), Line(1) + Line(2));
        File.WriteAllText(Path.Combine(folder, "b.plan.jsonl"), Line(3));
        File.WriteAllText(Path.Combine(folder, "a.md"), "# not a log\n");
        File.WriteAllText(Path.Combine(folder, "a.journal.md"), "journal\n");

        var first = Streams(rig, "t");

        Assert.Equal(["a", "b"], first.Select(s => s.Member("stream")!.Value.AsString()));
        Assert.Equal(2, Stream(first, "a").Member("rows")!.Value.AsArray().Count);
        var offsetA = (long)Stream(first, "a").Member("offset")!.Value.AsNumber();
        Assert.Equal(2, offsetA);
        Assert.Equal(offsetA, (long)Stream(first, "a").Member("length")!.Value.AsNumber());
        Assert.False(Stream(first, "a").Member("truncated")!.Value.AsBool());

        Append(rig, "a", Line(4));
        var second = Streams(rig, "t", new JsonObject { ["a"] = offsetA, ["b"] = 1 });

        var rows = Stream(second, "a").Member("rows")!.Value.AsArray();
        Assert.Equal("i4", Assert.Single(rows).Member("item")!.Value.AsString());
        Assert.Empty(Stream(second, "b").Member("rows")!.Value.AsArray());
        Assert.Equal(3, (long)Stream(second, "a").Member("offset")!.Value.AsNumber());
    }

    [Fact]
    public void TheTakeSkipsABadOrUnfinishedLine()
    {
        var rig = HierarchyReadOpsTests.Make();
        var path = Path.Combine(Folder(rig, "t"), "s.plan.jsonl");
        File.WriteAllText(path, Line(1) + "not json\n" + "42\n" + "[1]\n" + Line(2) + Line(3)[..40]);

        var first = Stream(Streams(rig, "t"), "s");

        Assert.Equal(["i1", "i2"], first.Member("rows")!.Value.AsArray().Select(r => r.Member("item")!.Value.AsString()));
        Assert.Equal(2, (long)first.Member("offset")!.Value.AsNumber());
    }

    [Fact]
    public void AnOffsetPastTheEndStartsAgainFromTheTopAndAnOffsetAtTheEndGivesNothing()
    {
        var rig = HierarchyReadOpsTests.Make();
        var path = Path.Combine(Folder(rig, "t"), "s.plan.jsonl");
        File.WriteAllText(path, Line(1) + Line(2));
        const long end = 2;

        var atEnd = Stream(Streams(rig, "t", new JsonObject { ["s"] = end }), "s");
        Assert.Empty(atEnd.Member("rows")!.Value.AsArray());
        Assert.False(atEnd.Member("reset")!.Value.AsBool());
        Assert.Equal(end, (long)atEnd.Member("offset")!.Value.AsNumber());

        var past = Stream(Streams(rig, "t", new JsonObject { ["s"] = end + 1 }), "s");
        Assert.Equal(2, past.Member("rows")!.Value.AsArray().Count);
        Assert.True(past.Member("reset")!.Value.AsBool());
    }

    [Fact]
    public void AFirstReadOfALogOverOneMegabyteGivesItsTailWholeRowsOnlyAndALogOfExactlyOneMegabyteIsNotCut()
    {
        var rig = HierarchyReadOpsTests.Make();
        var folder = Folder(rig, "t");
        var row = Line(1, 1024 - Line(1).Length);
        var perMegabyte = HierLibCap() / row.Length;
        Assert.Equal(1024, row.Length);

        File.WriteAllText(Path.Combine(folder, "exact.plan.jsonl"), string.Concat(Enumerable.Repeat(row, perMegabyte)));
        var all = new StringBuilder();
        for (var n = 0; n < perMegabyte + 5; n++)
        {
            _ = all.Append(Line(n, 1000 - (n.ToString().Length - 1)));
        }

        File.WriteAllText(Path.Combine(folder, "long.plan.jsonl"), all.ToString());

        var streams = Streams(rig, "t");

        var exact = Stream(streams, "exact");
        Assert.False(exact.Member("truncated")!.Value.AsBool());
        Assert.Equal(perMegabyte, exact.Member("rows")!.Value.AsArray().Count);

        var tail = Stream(streams, "long");
        var rows = tail.Member("rows")!.Value.AsArray();
        Assert.True(tail.Member("truncated")!.Value.AsBool());
        Assert.True(rows.Count > 0 && rows.Count < perMegabyte + 5);
        Assert.Equal($"i{perMegabyte + 4}", rows[^1].Member("item")!.Value.AsString());
        Assert.Equal(perMegabyte + 5, (long)tail.Member("offset")!.Value.AsNumber());
    }

    [Fact]
    public void AnAppendOfMoreThanOneMegabyteToAStreamAlreadyReadStartsItOverFromItsNewestWindowWhileASmallAppendDoesNot()
    {
        var rig = HierarchyReadOpsTests.Make();
        var path = Path.Combine(Folder(rig, "t"), "s.plan.jsonl");
        File.WriteAllText(path, Line(1));
        const long offset = 1;

        Append(rig, "s", Line(2));
        var small = Stream(Streams(rig, "t", new JsonObject { ["s"] = offset }), "s");
        Assert.False(small.Member("reset")!.Value.AsBool());
        Assert.False(small.Member("truncated")!.Value.AsBool());
        Assert.Equal("i2", Assert.Single(small.Member("rows")!.Value.AsArray()).Member("item")!.Value.AsString());

        var big = new StringBuilder();
        for (var n = 10; n < 10 + (HierLibCap() / 1000) + 50; n++)
        {
            _ = big.Append(Line(n, 1000 - n.ToString().Length));
        }

        Append(rig, "s", big.ToString());
        var next =Stream(Streams(rig, "t", new JsonObject { ["s"] = offset }), "s");

        Assert.True(next.Member("reset")!.Value.AsBool());
        Assert.True(next.Member("truncated")!.Value.AsBool());
        Assert.Equal($"i{10 + (HierLibCap() / 1000) + 49}", next.Member("rows")!.Value.AsArray()[^1].Member("item")!.Value.AsString());
        Assert.Equal(next.Member("length")!.Value.AsNumber(), next.Member("offset")!.Value.AsNumber());
    }

    private static int HierLibCap() => 1024 * 1024;

    private static JsonArray Described(HierarchyReadOpsTests.Rig rig, string team)
    {
        Ah.TestKit.TestImport.Dir(rig.AhHome, rig.Pool);
        return HierarchyReadOpsTests.Parsed(HierarchyReadOpsTests.Read(rig, "progress", "streams", new JsonObject { ["dir"] = rig.Pool, ["team"] = team })).Member("streams")!.Value.AsArray();
    }

    [Fact]
    public void DescribeGivesOwnerBranchWorkTreeAndGoalFromTheTeamFileAndTheChecklistHeading()
    {
        var rig = HierarchyReadOpsTests.Make();
        var folder = Folder(rig, "t");
        File.WriteAllText(Path.Combine(folder, "s.plan.jsonl"), Line(1));
        File.WriteAllText(Path.Combine(folder, "s.md"), "# s — Ship the lane\nUpdated: x\n");
        File.WriteAllText(Path.Combine(folder, "bare.plan.jsonl"), Line(2));
        File.WriteAllText(Path.Combine(folder, "bare.md"), "# bare — goal not set\n");
        _ = Directory.CreateDirectory(Path.Combine(rig.Pool, "teams"));
        File.WriteAllText(
            Path.Combine(rig.Pool, "teams", "t.json"),
            """{"streams":{"s":{"branch":"ah/ui-r5b","worktree":"/w/lifecycle-optin"},"quiet":{"branch":"ah/q"}},"members":[{"name":"implementor-28","stream":"s"}]}""");

        var streams = Described(rig, "t");

        Assert.Equal(["bare", "quiet", "s"], streams.Select(s => s.Member("stream")!.Value.AsString()));
        var s = Stream(streams, "s");
        Assert.Equal("implementor-28", s.Member("owner")!.Value.AsString());
        Assert.Equal("ah/ui-r5b", s.Member("branch")!.Value.AsString());
        Assert.Equal("lifecycle-optin", s.Member("work_tree")!.Value.AsString());
        Assert.Equal("Ship the lane", s.Member("goal")!.Value.AsString());
        var bare = Stream(streams, "bare");
        Assert.Equal("—", bare.Member("owner")!.Value.AsString());
        Assert.Equal("—", bare.Member("branch")!.Value.AsString());
        Assert.True(bare.Member("goal")!.Value.IsNull);
        Assert.True(Stream(streams, "quiet").Member("goal")!.Value.IsNull);
    }
}
