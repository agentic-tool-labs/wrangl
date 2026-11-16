using Ah.Core.Json;

namespace Ah.Hierarchy.Tests;

/// <summary>The History's one filter and its keyset pages (spec qa-history-replies §4.6), as the CLI and the daemon both call it.</summary>
public sealed class QaHistoryFilterTests
{
    private static JsonValue Ask(string id, double at, string? team, bool answered)
    {
        var row = HierarchyModule.QaAskRow(at, "s1", team, JsonText.Parse("""[{"question":"Pick a color","header":"Color","options":[{"label":"Teal"},{"label":"Rust"}]}]"""),
            JsonText.Parse(answered ? """{"answers":{"Pick a color":"Other: magenta"}}""" : "{}"));
        row["id"] = id;
        return row;
    }

    private static readonly JsonValue[] Rows =
    [
        HierarchyModule.QaPromptRow("p1", 1000, "s1", "t1", "Why is the Build red?"),
        HierarchyModule.QaDirectRow(1100, "s1", "t1", "Alpha answer", "p1"),
        HierarchyModule.QaReplyRow(1200, "s1", "t1", "work charlie work", "p1", typed: true, cut: false, final: "Bravo done", openingId: "o1"),
        HierarchyModule.QaPromptRow("p2", 2000, "s1", "t2", "deploy 50% of the site"),
        HierarchyModule.QaPromptRow("p3", 3000, "s1", null, "what about zebras"),
        HierarchyModule.QaReplyRow(3500, "s1", null, "Delta later", "p3", typed: false, cut: false),
        HierarchyModule.QaPromptRow("p4", 4000, "s1", "t1", "tell me about the moon"),
        HierarchyModule.QaDirectRow(4100, "s1", "t1", "moon direct", "p4"),
        Ask("a1", 5000, "t1", answered: true),
        Ask("a2", 6000, "t2", answered: false),

        // a prompt the user did not type, and the reply filed under it: hidden in every mode
        HierarchyModule.QaPromptRow("h1", 7000, "s1", "t1", "<agent-message from=\"x\">hiddenword</agent-message>"),
        HierarchyModule.QaReplyRow(7100, "s1", "t1", "hiddenword in a reply", "h1", typed: true, cut: false),

        // a reply no prompt in the log names: unprompted, shown only with All
        HierarchyModule.QaReplyRow(8000, "s2", "t1", "unprompted echo", null, typed: false, cut: false),
    ];

    private static string[] Ids(QaHistoryQuery q) =>
        [.. HierarchyModule.QaHistoryPage(HierarchyModule.QaThreads(Rows), q).Threads.Select(static t => (t.Prompt ?? t.Ask ?? t.Replies[0]).Member("id")!.Value.AsString())];

    [Fact]
    public void EachFilterAndTheirCombinationKeepExactlyTheThreadsItNames()
    {
        var u1 = HierarchyModule.QaThreads(Rows).Single(static t => t.Prompt is null && t.Ask is null && t.Replies[0].Member("session_id")!.Value.AsString() == "s2")
            .Replies[0].Member("id")!.Value.AsString();
        (string Case, QaHistoryQuery Query, string[] Expected)[] table =
        [
            ("no filter", new(), ["a2", "a1", "p4", "p3", "p2", "p1"]),
            ("empty search is no search", new() { Search = string.Empty }, ["a2", "a1", "p4", "p3", "p2", "p1"]),
            ("search in the question, any case", new() { Search = "BUILD" }, ["p1"]),
            ("search in the direct reply", new() { Search = "alpha" }, ["p1"]),
            ("search in the final", new() { Search = "bRaVo" }, ["p1"]),
            ("search in the whole turn", new() { Search = "charlie" }, ["p1"]),
            ("search in a later reply", new() { Search = "delta" }, ["p3"]),
            ("search in an ask's questions", new() { Search = "rust" }, ["a2", "a1"]),
            ("search in an ask's answers", new() { Search = "MAGENTA" }, ["a1"]),
            ("search is plain text: % matches itself", new() { Search = "50%" }, ["p2"]),
            ("negative: search is never a pattern", new() { Search = ".*" }, []),
            ("negative: a JSON key is not searched", new() { Search = "prompt_id" }, []),
            ("negative: a hidden prompt's reply is never a hit, even with All", new() { Search = "hiddenword", All = true }, []),
            ("team t1, and the null-team thread", new() { Team = "t1" }, ["a1", "p4", "p3", "p1"]),
            ("team t2, and the null-team thread", new() { Team = "t2" }, ["a2", "p3", "p2"]),
            ("negative: a team nobody has keeps only the null-team thread", new() { Team = "t9" }, ["p3"]),
            ("since, at or after", new() { Since = 4000 }, ["a2", "a1", "p4"]),
            ("answered: a reply row, or an ask with answers", new() { Answered = true }, ["a1", "p4", "p3", "p1"]),
            ("unanswered: a prompt with no reply row", new() { Answered = false }, ["p2"]),
            ("questions", new() { Questions = true }, ["p4", "p3", "p1"]),
            ("combined: team, answered, since, search", new() { Team = "t1", Answered = true, Since = 2000, Search = "MOON" }, ["p4"]),
            ("negative combined: team t2 has no alpha", new() { Team = "t2", Search = "alpha" }, []),
            ("all adds the unprompted thread, never the hidden one", new() { All = true }, [u1, "a2", "a1", "p4", "p3", "p2", "p1"]),
            ("id is one thread, unprompted too", new() { Id = u1 }, [u1]),
        ];

        Assert.All(table, row => Assert.True(row.Expected.SequenceEqual(Ids(row.Query)), $"{row.Case}: [{string.Join(", ", Ids(row.Query))}]"));
    }

    [Fact]
    public void PagesAreFullWhileMatchesRemainNeverRepeatAndACursorIsAPlaceNotAThread()
    {
        // 250 prompts, two to each time so the head id breaks the tie; every even one says "even"
        var rows = Enumerable.Range(0, 250).Select(static i => (JsonValue)HierarchyModule.QaPromptRow($"p{i:000}", 10_000 + (i / 2), "s1", "t1", i % 2 == 0 ? $"even {i}" : $"odd {i}")).ToList();
        var even = Enumerable.Range(0, 250).Where(static i => i % 2 == 0).Reverse().Select(static i => $"p{i:000}").ToList();
        static string Head(QaThread t) => t.Prompt!.Value.Member("id")!.Value.AsString();
        QaPage Read(QaHistoryQuery q) => HierarchyModule.QaHistoryPage(HierarchyModule.QaThreads(rows), q);

        var seen = new List<string>();
        var sizes = new List<int>();
        QaCursor? cursor = null;
        do
        {
            var page = Read(new QaHistoryQuery { Search = "even", Limit = 10, Before = cursor });
            sizes.Add(page.Threads.Count);
            seen.AddRange(page.Threads.Select(Head));
            cursor = page.Next is { } next ? HierarchyModule.QaCursorOf(next.ToString()) : null; // through its text, as a client holds it
        }
        while (cursor is not null);

        Assert.Equal([.. Enumerable.Repeat(10, 12), 5], sizes);
        Assert.Equal(even, seen);

        // page 2 is the same after a new matching thread lands at the head
        var first = Read(new QaHistoryQuery { Search = "even", Limit = 10 });
        var second = Read(new QaHistoryQuery { Search = "even", Limit = 10, Before = first.Next }).Threads.Select(Head).ToList();
        rows.Add(HierarchyModule.QaPromptRow("p999", 99_999, "s1", "t1", "even newer"));
        Assert.Equal(second, Read(new QaHistoryQuery { Search = "even", Limit = 10, Before = first.Next }).Threads.Select(Head).ToList());
        Assert.Equal("p999", Head(Read(new QaHistoryQuery { Search = "even", Limit = 10 }).Threads[0]));

        // negative: a cursor taken without the filter, used with it, keeps only matches past that place
        var unfiltered = Read(new QaHistoryQuery { Limit = 4 }).Next!; // p247 (odd), after p999, p249 and p248; p246 shares its time
        Assert.Equal("p247", unfiltered.Id);
        Assert.Equal(["p246", "p244"], Read(new QaHistoryQuery { Search = "even", Limit = 2, Before = unfiltered }).Threads.Select(Head));

        // a limit is capped at 100, so 126 matches leave a next; a page that takes the last match exactly has none
        Assert.Equal(100, Read(new QaHistoryQuery { Search = "even", Limit = 500 }).Threads.Count);
        Assert.NotNull(Read(new QaHistoryQuery { Search = "even", Limit = 126 }).Next);
        Assert.Null(Read(new QaHistoryQuery { Search = "even newer", Limit = 1 }).Next);
    }

    [Fact]
    public void AListReadCutsEachReplyTo600BytesAndMarksItButNotAShorterOne()
    {
        var rows = new JsonValue[]
        {
            HierarchyModule.QaPromptRow("p1", 1000, "s1", "t1", "long?"),
            HierarchyModule.QaDirectRow(1100, "s1", "t1", new string('a', 600), "p1"),
            HierarchyModule.QaReplyRow(1200, "s1", "t1", new string('b', 599) + "é", "p1", typed: true, cut: false, final: "short", openingId: "o1"),
        };
        var page = HierarchyModule.QaHistoryPage(HierarchyModule.QaThreads(rows), new QaHistoryQuery());

        var listed = HierarchyModule.QaHistoryJson(page, "/d", heads: true).Get("threads")!.Value.AsArray()[0].Member("replies")!.Value.AsArray();
        var whole = HierarchyModule.QaHistoryJson(page, "/d", heads: false).Get("threads")!.Value.AsArray()[0].Member("replies")!.Value.AsArray();

        // negative: exactly 600 bytes is not cut
        Assert.Equal((600, false), (listed[0].Member("text")!.Value.AsString().Length, listed[0].Member("cut") is not null));

        // 601 bytes is cut before the split character, and the stored row is left whole
        Assert.Equal((new string('b', 599), true, "short"), (listed[1].Member("text")!.Value.AsString(), listed[1].Member("cut")!.Value.AsBool(), listed[1].Member("final")!.Value.AsString()));
        Assert.Equal(new string('b', 599) + "é", whole[1].Member("text")!.Value.AsString());
        Assert.Null(whole[1].Member("cut"));
    }
}
