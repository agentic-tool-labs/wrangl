using Ah.Cli.Composition;
using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>b459 step 20: the dispatch evaluation's inputs held on their store's version, read again when it moves.</summary>
public sealed class DispatchHoldTests
{
    private static JsonValue Entry(string id, string kind, string reference) =>
        JsonValue.FromObject(new JsonObject { ["id"] = id, ["kind"] = kind, ["ref"] = reference });

    [Fact]
    public void AnUnchangedBoxIsNotReadAgainAndNegativeANewBriefShowsOnTheNextRead()
    {
        var memo = new BriefAddresseeMemo();
        List<JsonValue> entries = [Entry("e1", "brief", "/x/r1--request.md"), Entry("e2", "note", "/x/r2--request.md")];

        var first = memo.Of("/box", "impl@t", entries);
        Assert.Single(first);
        Assert.Equal("impl", first[0].Name);
        Assert.Same(first, memo.Of("/box", "impl@t", entries));
        Assert.Equal(1, memo.Builds);

        entries.Add(Entry("e3", "brief", "/x/r3--request.md"));
        Assert.Equal(2, memo.Of("/box", "impl@t", entries).Count);
        Assert.Equal(2, memo.Builds);
    }

    [Fact]
    public void AFactIsHeldWhileTheMsgsVersionHoldsAndNegativeAWriteDerivesItAgain()
    {
        var memo = new MsgsVersionMemo<bool>();
        var derived = 0;
        bool Landed()
        {
            derived++;
            return derived > 1;
        }

        Assert.False(memo.Of(7, "/r--response.md", Landed));
        Assert.False(memo.Of(7, "/r--response.md", Landed));
        Assert.Equal(1, derived);

        // the response is filled: the msgs version moves and the next tick sees it landed
        Assert.True(memo.Of(8, "/r--response.md", Landed));

        // no version (no store) derives every time
        _ = memo.Of(null, "/r--response.md", Landed);
        Assert.Equal(3, derived);

        // b459 step 25b: a failed read (null) is derived again at the same version; a read that answered false is held
        var tried = new MsgsVersionMemo<bool?>();
        var reads = 0;
        bool? answer = null;
        bool? Try()
        {
            reads++;
            return answer;
        }

        _ = tried.Of(9, "/x", Try, static v => v is not null);
        answer = false;
        Assert.False(tried.Of(9, "/x", Try, static v => v is not null));
        Assert.False(tried.Of(9, "/x", Try, static v => v is not null));
        Assert.Equal(2, reads);
    }
}
