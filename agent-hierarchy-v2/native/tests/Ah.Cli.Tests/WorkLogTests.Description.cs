using System.Text;
using Ah.Cli.Verbs;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.StateDb;

namespace Ah.Cli.Tests;

/// <summary>Work item descriptions: one <c>description</c> log entry per edit, the newest with a body being the description, and a nudge that never stops anything.</summary>
public sealed partial class WorkLogTests
{
    private const string Nudge = "has no description; add one: ah work record ";

    private (int Code, string Out, string Err) WorkIn(string stdin, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = WorkVerb.Run(_context with { ReadStdin = () => Encoding.UTF8.GetBytes(stdin) }, args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private (int Code, string Out, string Err) Backlog(params string[] args) => Run(BacklogVerb.Run, args);

    private string NewItem(string title = "x")
    {
        var r = WorkIn("About it", "add", title, "--body-file", "-");
        Assert.True(r.Code == 0, r.Err);
        return r.Out.Trim();
    }

    private string? DescriptionJson(string id, params string[] more)
    {
        var r = Work(["show", id, "--json", .. more]);
        Assert.True(r.Code == 0, r.Err);
        return JsonText.Parse(r.Out).Member("description") is { IsText: true } text ? text.AsString() : null;
    }

    private long Items() => Scalar("SELECT COUNT(*) FROM work_items");

    [Fact]
    public void AddWithNoBodyCreatesTheItemAndNudgesOnStderrOnly()
    {
        var r = Work("add", "x");

        Assert.Equal(0, r.Code);
        Assert.Equal("mcr-1\n", r.Out);
        Assert.Equal($"ah: mcr-1 has no description; add one: ah work record mcr-1 --type description --title Description --body-file - (see the write-ticket skill)\n", r.Err);
        Assert.Null(DescriptionJson("mcr-1"));
    }

    [Fact]
    public void AddWithABodyFromStdinOrAFileOrTheFlagHasADescriptionAndNoNudge()
    {
        var path = Path.Combine(_sandbox.Root, "ticket.md");
        File.WriteAllText(path, "From the file");
        foreach (var (stdin, args, expected) in new (string, string[], string)[]
        {
            ("From stdin\nline two\n", ["--body-file", "-"], "From stdin\nline two\n"),
            (string.Empty, ["--body-file", path], "From the file"),
            (string.Empty, ["--body", "From the flag"], "From the flag"),
        })
        {
            var r = WorkIn(stdin, ["add", "x", .. args]);

            Assert.Equal(0, r.Code);
            Assert.Empty(r.Err);
            Assert.Equal(expected, DescriptionJson(r.Out.Trim()));
        }
    }

    [Fact]
    public void AnUnreadableBodyFileCreatesNothing()
    {
        var r = Work("add", "x", "--body-file", Path.Combine(_sandbox.Root, "absent.md"));

        Assert.Equal(2, r.Code);
        Assert.Empty(r.Out);
        Assert.Contains("--body-file", r.Err, StringComparison.Ordinal);
        Assert.Equal(0, Items());
    }

    [Fact]
    public void ABodyOverTheCapCreatesNothingAndOneAtTheCapFits()
    {
        var over = WorkIn(new string('x', (8 * 1024) + 1), "add", "x", "--body-file", "-");

        Assert.Equal(2, over.Code);
        Assert.Empty(over.Out);
        Assert.Contains("attach a file instead", over.Err, StringComparison.Ordinal);
        Assert.Equal(0, Items());

        var at = WorkIn(new string('x', 8 * 1024), "add", "x", "--body-file", "-");
        Assert.Equal(0, at.Code);
        Assert.Equal(new string('x', 8 * 1024), DescriptionJson(at.Out.Trim()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n\t\n")]
    [InlineData("\u0001\n")] // nothing but what cleaning removes
    public void AnEmptyBodyCreatesTheItemWithNoDescriptionAndNudges(string body)
    {
        var r = WorkIn(body, "add", "x", "--body-file", "-");

        Assert.Equal(0, r.Code);
        Assert.Equal("mcr-1\n", r.Out);
        Assert.Contains(Nudge, r.Err, StringComparison.Ordinal);
        Assert.Null(DescriptionJson("mcr-1"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM work_log WHERE type = 'description'"));
    }

    [Fact]
    public void AFailedEntryWriteStillPrintsTheIdAndExitsNonZero()
    {
        Assert.True(StateDbModule.OpenDirect(_sandbox.AhHome).Write(tx =>
        {
            tx.Execute("CREATE TRIGGER no_description BEFORE INSERT ON work_log WHEN NEW.type = 'description' BEGIN SELECT RAISE(ABORT, 'no description today'); END");
            return 0;
        }).Ok);

        var r = WorkIn("About it", "add", "x", "--body-file", "-");

        Assert.NotEqual(0, r.Code);
        Assert.Equal("mcr-1\n", r.Out);
        Assert.Contains("no description today", r.Err, StringComparison.Ordinal);
        Assert.DoesNotContain(Nudge, r.Err, StringComparison.Ordinal);
        Assert.Equal(1, Items());
    }

    [Fact]
    public void RecordWritesADescriptionOnABacklogRowATeamItemAndABacklogId()
    {
        Add("team-item");
        var backlog = Backlog("add", "From the backlog").Out.Trim();
        Assert.Equal("b1", backlog);

        foreach (var (id, text) in new[] { ("team-item", "On the team item"), ("_run/team-item", "By its alias"), (backlog, "By b-number") })
        {
            var r = WorkIn(text, "record", id, "--type", "description", "--title", "Description", "--body-file", "-");
            Assert.True(r.Code == 0, r.Err);
            Assert.Equal(text, DescriptionJson(id));
        }
    }

    [Fact]
    public void TheNewestWinsAndTheOlderStaysInTheTimeline()
    {
        var id = NewItem();
        Assert.Equal(0, WorkIn("Second", "record", id, "--type", "description", "--title", "Reworded", "--body-file", "-").Code);

        Assert.Equal("Second", DescriptionJson(id));
        var log = Work("log", id, "--all", "--body").Out;
        Assert.Contains("About it", log, StringComparison.Ordinal);
        Assert.Contains("Reworded", log, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntryWithNoBodyOrOnlyBlanksLeavesTheLastRealDescriptionStanding()
    {
        var id = NewItem();
        Recorded(id, "description", "Touched");
        Assert.Equal(0, WorkIn(" \n", "record", id, "--type", "description", "--title", "Blanked", "--body-file", "-").Code);

        Assert.Equal("About it", DescriptionJson(id));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM work_log WHERE type = 'description' AND body IS NULL"));
    }

    [Fact]
    public void ADescriptionOlderThanTheTimelineWindowStillReads()
    {
        var id = NewItem();
        for (var i = 0; i < 205; i++)
        {
            Recorded(id, "note", "n" + i);
        }

        Assert.Equal("About it", DescriptionJson(id));
        Assert.Contains("About it", Work("show", id).Out, StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherEntryTypeOrAnotherItemIsNotADescription()
    {
        var id = NewItem("has");
        Add("other");
        Recorded("other", "note", "A note", "--body", new string('n', 2000));
        Assert.Equal(0, Backlog("set", Backlog("add", "row").Out.Trim(), "--note", "short remark").Code);

        Assert.Null(DescriptionJson("other"));
        Assert.Null(DescriptionJson("b2"));
        Assert.DoesNotContain("description:", Work("show", "b2").Out, StringComparison.Ordinal);
        Assert.Equal("About it", DescriptionJson(id));
    }

    [Fact]
    public void ShowPrintsItInFullAfterEveryFieldOutsideTheBudget()
    {
        var text = string.Concat(Enumerable.Repeat("0123456789\n", 744));
        var id = WorkIn(text, "add", "x", "--body-file", "-").Out.Trim();
        // an attachment ahead of it, and a show part cut by its own budget, must not eat it
        Assert.Equal(0, Work("attach", id, "https://example.com/" + new string('a', 400)).Code);

        var shown = Work("show", id).Out;

        Assert.EndsWith("description:\n" + text, shown, StringComparison.Ordinal);
        Assert.True(shown.IndexOf("attachments:", StringComparison.Ordinal) < shown.IndexOf("description:", StringComparison.Ordinal));
        Assert.Equal(text, DescriptionJson(id));
    }

    [Fact]
    public void ShowWithNoDescriptionIsUnchanged()
    {
        Add("x");
        var before = Work("show", "x");
        Recorded("x", "note", "not a description", "--body", "body");

        Assert.Equal(before.Out, Work("show", "x").Out);
        Assert.DoesNotContain("description", before.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("description:", Work("show", "x", "--log").Out, StringComparison.Ordinal);
        Assert.Null(DescriptionJson("x"));
        Assert.Null(DescriptionJson("x", "--log"));
    }

    [Fact]
    public void ShowLogGivesAtMostAKibibyteEndingWithThePointerAndJsonGivesAllOfIt()
    {
        var text = new string('d', 4000);
        var id = WorkIn(text, "add", "x", "--body-file", "-").Out.Trim();

        var shown = Work("show", id, "--log").Out;

        var from = shown.IndexOf("description:\n", StringComparison.Ordinal);
        var to = shown.IndexOf("log:\n", StringComparison.Ordinal);
        Assert.InRange(from, 0, to);
        var excerpt = shown[from..to];
        Assert.True(Encoding.UTF8.GetByteCount(excerpt) <= 1024, excerpt.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.EndsWith($"(all of it: ah work show {id})\n", excerpt, StringComparison.Ordinal);
        Assert.Contains(new string('d', 900), excerpt, StringComparison.Ordinal);
        Assert.DoesNotContain(text, shown, StringComparison.Ordinal);
        Assert.Equal(text, DescriptionJson(id, "--log"));
    }

    [Fact]
    public void ShowLogKeepsTheTimelineAndItsCapWhenTheTimelineIsLong()
    {
        var id = NewItem();
        for (var i = 0; i < 20; i++)
        {
            Recorded(id, "note", new string('t', 119) + (i % 10));
        }

        Assert.Equal(0, WorkIn(new string('d', 4000), "record", id, "--type", "handoff", "--title", "h", "--body-file", "-").Code);
        Assert.Equal(0, WorkIn(new string('e', 4000), "record", id, "--type", "description", "--title", "Long", "--body-file", "-").Code);

        var shown = Work("show", id, "--log").Out;

        Assert.True(Encoding.UTF8.GetByteCount(shown) <= 8 * 1024, shown.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("log:\n", shown, StringComparison.Ordinal);
        Assert.Contains("--- newest handoff body ---", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOverCapDescriptionByRecordIsRefused()
    {
        var id = NewItem();

        var r = WorkIn(new string('x', (8 * 1024) + 1), "record", id, "--type", "description", "--title", "Long", "--body-file", "-");

        Assert.Equal(2, r.Code);
        Assert.Contains("attach a file instead", r.Err, StringComparison.Ordinal);
        Assert.Equal("About it", DescriptionJson(id));
    }

    [Fact]
    public void ThePlacementGoingAwayLeavesItsDescriptionReadable()
    {
        Plan("placed", new PlanInput("placed", "add", "Do placed") { At = "2026-11-03T09:00:00Z" });
        Assert.Equal(0, WorkIn("Outlives its plan", "record", "placed", "--type", "description", "--title", "Description", "--body-file", "-").Code);
        Ah.TestKit.TestProgressLogs.Drop(_sandbox.AhHome, _dir, Team, "_run.plan.jsonl");
        Plan("placed", new PlanInput("placed", "add", "Do placed") { At = "2026-11-03T10:00:00Z" });
        Assert.Equal(0, Work("list").Code);

        var shown = Work("show", "mcr-1").Out;

        Assert.Contains("state: gone", shown, StringComparison.Ordinal);
        Assert.EndsWith("description:\nOutlives its plan\n", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void BacklogAddNudgesOnStderrWithStdoutAndExitUnchanged()
    {
        var r = Backlog("add", "row");

        Assert.Equal(0, r.Code);
        Assert.Equal("b1\n", r.Out);
        Assert.Contains("ah: b1 " + Nudge + "b1 ", r.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void BacklogAddOfAnExtItAlreadyHoldsAddsNothingAndDoesNotNudge()
    {
        Assert.Contains(Nudge, Backlog("add", "row", "--ext", "T-1").Err, StringComparison.Ordinal);

        var again = Backlog("add", "row again", "--ext", "T-1");

        Assert.Equal(0, again.Code);
        Assert.Equal("b1\n", again.Out);
        Assert.Empty(again.Err);
    }

    [Fact]
    public void BacklogImportDoesNotNudge()
    {
        var file = Path.Combine(_sandbox.Root, "groom-1.json");
        File.WriteAllText(file, """[{"id":"N1","title":"one"}]""");

        var r = Backlog("import", file);

        Assert.Equal(0, r.Code);
        Assert.DoesNotContain(Nudge, r.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void MsgNewDoesNotNudgeOnAnAutoRowOrANamedRowAndLeavesNoDescription()
    {
        FakeRunner.RegisterSelf(_graph.Mailbox, "o", Path.Combine(_dir, "teams", Team + ".json"), "orchestrator@tm");
        string[] Request(string id, params string[] more) =>
            ["new", "--type", "request", "--to", "implementor", "--to-name", "impl-1", "--from", "orchestrator", "--slug", "s", "--title", "Fix the widget title", "--id", id, "--session", "o", "--body-file", BodyFile(), .. more];

        var auto = Msg(Request("20261103-120000-aaaa"));
        Assert.True(auto.Code == 0, auto.Err);
        Assert.DoesNotContain(Nudge, auto.Err, StringComparison.Ordinal);
        Assert.Equal(0, Work("list").Code);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM work_items WHERE item LIKE 'auto-%'"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM work_log WHERE type = 'description'"));

        Add("named");
        var named = Msg(Request("20261103-120000-aaab", "--item", "named"));
        Assert.True(named.Code == 0, named.Err);
        Assert.DoesNotContain(Nudge, named.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEntryIsListedLikeAnyOtherInLogAndMine()
    {
        Add("held");
        Dispatch("held", "impl-1", "20261103-120000-aaaa");
        Assert.Equal(0, WorkIn("About it", "record", "held", "--type", "description", "--title", "Reworded", "--body-file", "-").Code);

        Assert.Contains("Reworded", Work("log", "held").Out, StringComparison.Ordinal);
        Assert.Contains("Reworded", Work("mine").Out, StringComparison.Ordinal);
    }
}
