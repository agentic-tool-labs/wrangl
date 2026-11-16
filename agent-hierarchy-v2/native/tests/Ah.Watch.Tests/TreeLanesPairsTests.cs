using System.Globalization;

namespace Ah.Watch.Tests;

/// <summary>The team exchange lists count the exchanges each unordered pair of resolved members shares, list the creation times of those of the last hour, and count an exchange with an unresolved end apart.</summary>
public sealed partial class TreeLanesTests
{
    private static readonly long FixtureNowMs = DateTimeOffset.Parse("2026-06-01T13:00:00Z", CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    private static string Ago(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(FixtureNowMs - ms).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string PairMsg(string to, string from, string? toName, string? fromName, string? created, bool open = true, string? responded = null, bool withdrawn = false) =>
        "{\"slug\":\"s\",\"to\":\"" + to + "\",\"from\":\"" + from + "\",\"to_name\":" + Quoted(toName) + ",\"from_name\":" + Quoted(fromName)
        + ",\"team\":\"default\",\"created\":" + Quoted(created) + ",\"has_request\":true,\"has_response\":" + (open ? "false" : "true")
        + ",\"response_created\":" + Quoted(responded) + ",\"withdrawn\":" + (withdrawn ? "true" : "false") + "}";

    private static string At(long agoMs) => (FixtureNowMs - agoMs).ToString(CultureInfo.InvariantCulture);

    private static string Pairs(string body) => body[body.IndexOf("\"pairs\":", StringComparison.Ordinal)..];

    private const long Minute = 60_000;

    private const long Hour = 60 * Minute;

    [Fact]
    public void ThePairsCountOpenAndClosedExchangesPerUnorderedPairInNameOrderAndTheFeedListHoldsTheSame()
    {
        var reader = Truth();
        reader.Exchanges["a"] = PairMsg("developer", "orchestrator", "dev", null, Ago(5 * Hour));
        reader.Exchanges["b"] = PairMsg("orchestrator", "developer", null, "dev", Ago(5 * Hour), open: false);
        reader.Exchanges["c"] = PairMsg("tester", "developer", "qa", "dev", Ago(5 * Hour));
        reader.Exchanges["d"] = PairMsg("tester", "orchestrator", "qa", null, Ago(5 * Hour), open: false);
        var (model, _) = Seeded(reader);
        using var owner = model;

        const string Expected = "\"pairs\":[{\"a\":\"@orchestrator\",\"b\":\"dev\",\"n\":2,\"recent_1h\":[],\"last_response\":null},{\"a\":\"@orchestrator\",\"b\":\"qa\",\"n\":1,\"recent_1h\":[],\"last_response\":null},{\"a\":\"dev\",\"b\":\"qa\",\"n\":1,\"recent_1h\":[],\"last_response\":null}],\"unresolved\":{\"n\":0}}";
        Assert.Equal(Expected, Pairs(Body(model, ExchangesListId)));
        Assert.Equal(Expected, Pairs(Body(model, FeedListId)));
    }

    [Fact]
    public void LastResponseIsTheLatestAnswerOfAPairAndIgnoresWithdrawalsOpenAndUnresolvedExchanges()
    {
        var reader = Truth();
        reader.Exchanges["a"] = PairMsg("developer", "orchestrator", "dev", null, Ago(5 * Hour), open: false, responded: Ago(4 * Hour));
        reader.Exchanges["b"] = PairMsg("developer", "orchestrator", "dev", null, Ago(3 * Hour), open: false, responded: Ago(2 * Hour));
        reader.Exchanges["c"] = PairMsg("developer", "orchestrator", "dev", null, Ago(2 * Hour), open: false, responded: Ago(Hour), withdrawn: true);
        reader.Exchanges["d"] = PairMsg("developer", "orchestrator", "dev", null, Ago(Minute));
        reader.Exchanges["e"] = PairMsg("tester", "orchestrator", "qa", null, Ago(2 * Hour), open: false, responded: Ago(Hour), withdrawn: true);
        reader.Exchanges["f"] = PairMsg("developer", "orchestrator", "gone", null, Ago(2 * Hour), open: false, responded: Ago(Minute));
        reader.Exchanges["g"] = PairMsg("tester", "developer", "qa", "dev", Ago(2 * Hour), open: false, responded: "not a time");
        var (model, _) = Seeded(reader);
        using var owner = model;

        var pairs = Pairs(Body(model, ExchangesListId));

        Assert.Contains("\"b\":\"dev\",\"n\":4,\"recent_1h\":[" + At(Minute) + "],\"last_response\":" + At(2 * Hour) + "}", pairs, StringComparison.Ordinal);
        Assert.Contains("\"b\":\"qa\",\"n\":1,\"recent_1h\":[],\"last_response\":null}", pairs, StringComparison.Ordinal);
        Assert.Contains("\"a\":\"dev\",\"b\":\"qa\",\"n\":1,\"recent_1h\":[],\"last_response\":null}", pairs, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExchangeWithAnEndThatResolvesToNobodyIsCountedInUnresolvedOnlyWhateverTheReason()
    {
        var reader = Truth();
        reader.Teams[TeamPath] = "{\"team_id\":\"t1\",\"created\":\"2026-06-01T10:00:00Z\",\"orchestrator\":{\"session_id\":\"so\",\"pid\":1,\"pid_start\":\"o1\"},\"members\":["
            + "{\"name\":\"d1\",\"role\":\"developer\",\"transport_id\":\"p1\",\"stream\":null},{\"name\":\"d2\",\"role\":\"developer\",\"transport_id\":\"p2\",\"stream\":null},"
            + "{\"name\":\"qa\",\"role\":\"tester\",\"transport_id\":\"p3\",\"stream\":null}]}";
        reader.Exchanges["a"] = PairMsg("developer", "orchestrator", "gone", null, Ago(Minute));
        reader.Exchanges["b"] = PairMsg("developer", "orchestrator", null, null, Ago(2 * Hour));
        reader.Exchanges["c"] = PairMsg("tester", "orchestrator", "qa", null, Ago(Minute));
        var (model, _) = Seeded(reader);
        using var owner = model;

        Assert.Equal(
            "\"pairs\":[{\"a\":\"@orchestrator\",\"b\":\"qa\",\"n\":1,\"recent_1h\":[" + At(Minute) + "],\"last_response\":null}],\"unresolved\":{\"n\":2}}",
            Pairs(Body(model, ExchangesListId)));
    }

    [Fact]
    public void TheHourIncludesAnExchangeCreatedExactlyAnHourAgoAndExcludesOneASecondOlderOrWithNoCreatedTime()
    {
        var reader = Truth();
        reader.Exchanges["a"] = PairMsg("developer", "orchestrator", "dev", null, Ago(Hour));
        reader.Exchanges["b"] = PairMsg("developer", "orchestrator", "dev", null, Ago(Hour + 1000));
        reader.Exchanges["c"] = PairMsg("developer", "orchestrator", "dev", null, Ago(Hour - 1000));
        reader.Exchanges["d"] = PairMsg("developer", "orchestrator", "dev", null, null);
        reader.Exchanges["e"] = PairMsg("developer", "orchestrator", "dev", null, "not a time");
        var (model, _) = Seeded(reader);
        using var owner = model;

        Assert.Equal(
            "\"pairs\":[{\"a\":\"@orchestrator\",\"b\":\"dev\",\"n\":5,\"recent_1h\":[" + At(Hour - 1000) + "," + At(Hour) + "],\"last_response\":null}],\"unresolved\":{\"n\":0}}",
            Pairs(Body(model, ExchangesListId)));
    }

    [Fact]
    public void ANewExchangeRecomposesThePairsAndEqualsARebuild()
    {
        var reader = Truth();
        reader.Exchanges["a"] = PairMsg("developer", "orchestrator", "dev", null, Ago(2 * Hour));
        var (model, clock) = Seeded(reader);
        using var owner = model;
        Assert.Contains("\"n\":1,", Pairs(Body(model, ExchangesListId)), StringComparison.Ordinal);

        Send(model, clock, reader, "b", PairMsg("tester", "developer", "qa", "dev", Ago(Minute)));

        Assert.Equal(
            "\"pairs\":[{\"a\":\"@orchestrator\",\"b\":\"dev\",\"n\":1,\"recent_1h\":[],\"last_response\":null},{\"a\":\"dev\",\"b\":\"qa\",\"n\":1,\"recent_1h\":[" + At(Minute) + "],\"last_response\":null}],\"unresolved\":{\"n\":0}}",
            Pairs(Body(model, ExchangesListId)));
        AssertEqualsRebuild(model, reader);
    }

    [Fact]
    public void ThePairKeepsTheNewestHundredTimesOfTheHourAndNoMore()
    {
        var reader = Truth();
        for (var i = 0; i < 105; i++)
        {
            reader.Exchanges["x" + i.ToString(CultureInfo.InvariantCulture)] = PairMsg("developer", "orchestrator", "dev", null, Ago((i + 1) * 10_000L));
        }

        var (model, _) = Seeded(reader);
        using var owner = model;

        var expected = string.Join(",", Enumerable.Range(0, 100).Select(i => At((i + 1) * 10_000L)));
        Assert.Equal(
            "\"pairs\":[{\"a\":\"@orchestrator\",\"b\":\"dev\",\"n\":105,\"recent_1h\":[" + expected + "],\"last_response\":null}],\"unresolved\":{\"n\":0}}",
            Pairs(Body(model, ExchangesListId)));
    }

    [Fact]
    public void AHundredTimesAreAllKeptAndAnOlderExchangeCountsInNOnly()
    {
        var reader = Truth();
        for (var i = 0; i < 100; i++)
        {
            reader.Exchanges["x" + i.ToString(CultureInfo.InvariantCulture)] = PairMsg("developer", "orchestrator", "dev", null, Ago((i + 1) * 10_000L));
        }

        reader.Exchanges["old"] = PairMsg("developer", "orchestrator", "dev", null, Ago(2 * Hour));
        var (model, _) = Seeded(reader);
        using var owner = model;

        var expected = string.Join(",", Enumerable.Range(0, 100).Select(i => At((i + 1) * 10_000L)));
        Assert.Equal(
            "\"pairs\":[{\"a\":\"@orchestrator\",\"b\":\"dev\",\"n\":101,\"recent_1h\":[" + expected + "],\"last_response\":null}],\"unresolved\":{\"n\":0}}",
            Pairs(Body(model, ExchangesListId)));
    }
}
