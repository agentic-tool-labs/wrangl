using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>One list under test: rows newest first, new rows arriving at the head. Each list kind implements it once and runs <see cref="PagerContract"/>.</summary>
internal interface IPagerFixture
{
    /// <summary>Replaces the list with <paramref name="count"/> rows and returns their keys, newest first.</summary>
    IReadOnlyList<string> Seed(int count);

    /// <summary>Adds <paramref name="count"/> rows newer than all the others and returns their keys, newest first.</summary>
    IReadOnlyList<string> AppendNewer(int count);

    Task<ListPage> PageAsync(string? after, string? before, int limit);

    /// <summary>The key of a row, comparable with what <see cref="Seed"/> returned.</summary>
    string KeyOf(Ah.Core.Json.JsonValue row);

    /// <summary>The publish counter of the published state the fixture's pager reads; the one value every page's <c>version</c> must carry.</summary>
    long PublishCounter { get; }
}

/// <summary>The cursor rules every <c>/list</c> list shares: newest first, <c>after</c> older, <c>before</c> newer, and the cursors of an empty page.</summary>
internal static class PagerContract
{
    private static string[] Keys(IPagerFixture f, ListPage page) => [.. page.Rows.Select(f.KeyOf)];

    public static async Task VersionIsThePublishCounterOfTheStateRead(IPagerFixture f)
    {
        _ = f.Seed(5);

        var newest = await f.PageAsync(null, null, 2);
        var older = await f.PageAsync(newest.Next, null, 2);

        Assert.Equal(f.PublishCounter, newest.Version);
        Assert.Equal(f.PublishCounter, older.Version);
    }

    public static async Task BeforePageIsNewestFirstAndAfterPageIsOlder(IPagerFixture f)
    {
        var seeded = f.Seed(10);
        var first = await f.PageAsync(null, null, 3);
        var second = await f.PageAsync(first.Next, null, 3);

        Assert.Equal(seeded.Take(3), Keys(f, first));
        Assert.Equal(seeded.Skip(3).Take(3), Keys(f, second));
        var back = await f.PageAsync(null, second.Prev, 3);
        Assert.Equal(seeded.Take(3), Keys(f, back));
    }

    public static async Task NextWalksToTheOldestRowOnceEachAndEndsWithNull(IPagerFixture f)
    {
        var seeded = f.Seed(7);
        var seen = new List<string>();
        string? after = null;
        var pages = 0;
        do
        {
            var page = await f.PageAsync(after, null, 3);
            seen.AddRange(Keys(f, page));
            after = page.Next;
            pages++;
        }
        while (after is not null && pages < 10);

        Assert.Equal(seeded, seen);
        Assert.Equal(3, pages);
    }

    public static async Task CatchingUpAfterMoreThanALimitOfAppendsGivesEveryNewRowOnce(IPagerFixture f)
    {
        const int Limit = 3;
        _ = f.Seed(10);
        var prev = (await f.PageAsync(null, null, Limit)).Prev;
        var appended = f.AppendNewer(Limit + 5);

        var caught = new List<string>();
        for (var i = 0; i < 10 && prev is not null; i++)
        {
            var page = await f.PageAsync(null, prev, Limit);
            if (page.Rows.Count == 0)
            {
                break;
            }

            Assert.Equal(Keys(f, page).OrderByDescending(static k => k, StringComparer.Ordinal), Keys(f, page));
            caught.AddRange(Keys(f, page));
            prev = page.Prev;
        }

        Assert.Equal(appended.Order(StringComparer.Ordinal), caught.Order(StringComparer.Ordinal));
    }

    public static async Task PrevIsTheHeadCursorAtTheHeadAndBeforeItIsEmptyWithPrevEchoed(IPagerFixture f)
    {
        _ = f.Seed(5);
        var head = await f.PageAsync(null, null, 3);

        Assert.NotNull(head.Prev);
        Assert.Equal(head.HeadCursor, head.Prev);
        var beyond = await f.PageAsync(null, head.HeadCursor, 3);
        Assert.Empty(beyond.Rows);
        Assert.Equal(head.HeadCursor, beyond.Prev);
        Assert.Null(beyond.Next);
    }

    public static async Task AfterTheOldestRowIsEmptyWithNextEchoed(IPagerFixture f)
    {
        _ = f.Seed(4);
        var all = await f.PageAsync(null, null, 4);

        Assert.Null(all.Next);
        var beyond = await f.PageAsync(all.TailCursor, null, 3);
        Assert.Empty(beyond.Rows);
        Assert.Equal(all.TailCursor, beyond.Next);
        Assert.Null(beyond.Prev);
    }

    public static async Task AnEmptyListHasNoCursors(IPagerFixture f)
    {
        _ = f.Seed(0);
        var page = await f.PageAsync(null, null, 3);

        Assert.Empty(page.Rows);
        Assert.Equal((null, null, null, null), (page.Next, page.Prev, page.HeadCursor, page.TailCursor));
    }

    public static async Task HeadAndTailAreTheNewestAndOldestRowsOfThePage(IPagerFixture f)
    {
        _ = f.Seed(6);
        var page = await f.PageAsync(null, null, 3);

        Assert.Equal(page.Prev, page.HeadCursor);
        Assert.Equal(page.Next, page.TailCursor);
        Assert.NotEqual(page.HeadCursor, page.TailCursor);
    }
}
