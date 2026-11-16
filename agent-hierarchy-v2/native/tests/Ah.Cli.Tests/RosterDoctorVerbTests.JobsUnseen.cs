using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Cli.Tests;

public sealed partial class RosterDoctorVerbTests
{
    private void UnseenJob(string id, string? exit, TimeSpan endedAgo)
    {
        var record = new JsonObject
        {
            ["id"] = id, ["team"] = "tm", ["owner"] = "impl", ["session_id"] = "s", ["kind"] = "bg", ["label"] = "x", ["eta"] = "large",
            ["sent_at"] = Timestamps.ToIsoString(DateTimeOffset.UtcNow - endedAgo - TimeSpan.FromMinutes(5)), ["background"] = true,
            ["ended_at"] = Timestamps.ToIsoString(DateTimeOffset.UtcNow - endedAgo),
        };
        if (exit is not null)
        {
            record["exit"] = exit;
        }

        PlantGraph().Hierarchy.StateCalls.PutJob(Hierarchy, "tm", id, record);
    }

    [Fact]
    public void TheJobsUnseenRowWarnsAboveThreeInTheLastDayAndCountsNeitherOlderOnesNorOtherExits()
    {
        for (var i = 0; i < 3; i++)
        {
            UnseenJob("u" + i, "unseen", TimeSpan.FromHours(1));
        }

        UnseenJob("old", "unseen", TimeSpan.FromHours(25));
        UnseenJob("other", "0", TimeSpan.FromHours(1));
        var atThree = Row(Parse(Roster(null, "doctor").Stdout), "jobs-unseen");

        UnseenJob("u3", "unseen", TimeSpan.FromHours(2));
        var atFour = Row(Parse(Roster(null, "doctor").Stdout), "jobs-unseen");

        Assert.Equal("ok", atThree["status"].AsString());
        Assert.Contains("3 background job(s)", atThree["detail"].AsString(), StringComparison.Ordinal);
        Assert.Equal("warn", atFour["status"].AsString());
    }
}
