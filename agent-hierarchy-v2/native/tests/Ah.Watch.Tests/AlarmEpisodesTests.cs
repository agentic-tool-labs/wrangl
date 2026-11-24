namespace Ah.Watch.Tests;

/// <summary>An episode's <c>told_at</c> (ah2-594 P2d): set when it is first told, kept by a refresh, set again for a new episode.</summary>
public sealed class AlarmEpisodesTests
{
    [Fact]
    public void ARefreshKeepsWhenTheEpisodeWasFirstToldAndNegativeANewEpisodeOrALegacyEntryDoesNot()
    {
        string? saved = null;
        var at = DateTimeOffset.Parse("2026-11-24T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var episodes = new AlarmEpisodes(() => saved, text => saved = text, () => at);

        episodes.Tell("k", "sess-1");
        at = at.AddHours(2);
        episodes.Tell("k", "sess-1");
        Assert.Equal("2026-11-24T09:00:00.000Z", episodes.Entry("k")!["told_at"].AsString());
        Assert.Equal("2026-11-24T11:00:00.000Z", episodes.Entry("k")!["at"].AsString());

        episodes.Tell("k", "sess-2");
        Assert.Equal("2026-11-24T11:00:00.000Z", episodes.Entry("k")!["told_at"].AsString());

        saved = "{\"old\":{\"episode\":\"sess-3\",\"at\":\"2026-11-24T08:00:00.000Z\"}}";
        var legacy = new AlarmEpisodes(() => saved, text => saved = text, () => at);
        legacy.Tell("old", "sess-3");
        Assert.Null(legacy.Entry("old")!.Get("told_at"));
    }
}
