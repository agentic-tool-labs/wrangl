using Ah.Core.Json;
using Ah.Roster;

namespace Ah.Watch.Tests;

/// <summary>S3: the views name the deputy on duty and since when, from the record; a stale or absent duty shows nothing.</summary>
public sealed class DeputyViewTests
{
    private static JsonValue Team(string deputy) => JsonText.Parse(
        "{\"members\":[{\"name\":\"tm-dep\"}],\"duty\":{\"deputy\":\"" + deputy + "\",\"since\":\"2026-11-08T20:00:00.000Z\",\"cause\":\"user\"}}");

    [Fact]
    public void TheDutyBlockNamesTheDeputySinceAndCauseAndNothingForAStaleOrMissingDuty()
    {
        var duty = RosterModule.DutyOf(Team("tm-dep"));

        Assert.NotNull(duty);
        Assert.Equal(("tm-dep", "2026-11-08T20:00:00.000Z", "user"), (duty["deputy"].AsString(), duty["since"].AsString(), duty["cause"].AsString()));
        Assert.Null(RosterModule.DutyOf(Team("tm-gone")));
        Assert.Null(RosterModule.DutyOf(JsonText.Parse("{\"members\":[{\"name\":\"tm-dep\"}]}")));
    }
}
