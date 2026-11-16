using System.Globalization;
using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>A member idle on open background work reads as working, until that work is older than anything a slot would let run.</summary>
public sealed class ActivityBackgroundWorkTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-04T20:00:00Z", CultureInfo.InvariantCulture);

    private static JsonObject Body(string activity, double? bgOpen, int minutesAgo)
    {
        var since = Now.AddMinutes(-minutesAgo).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var record = new JsonObject { ["activity"] = activity, ["at"] = since, ["state_since"] = since };
        if (bgOpen is { } open)
        {
            record["bg_open"] = open;
        }

        return ActivityLane.BodyOf("/d", "s", JsonValue.FromObject(record), Now.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void AnIdleMemberWithBackgroundWorkOpenReadsWorkingAndTheCountIsServed()
    {
        var body = Body("idle", 1, 5);

        Assert.Equal("working", body["activity"].AsString());
        Assert.Equal(1, body["bg_open"].AsNumber());
    }

    [Theory]
    [InlineData("idle", 0d, 5, "idle")]
    [InlineData("idle", null, 5, "idle")]
    [InlineData("idle", 1d, 61, "idle")]
    [InlineData("blocked", 1d, 5, "blocked")]
    [InlineData("failed", 1d, 5, "failed")]
    public void NegativeNoOpenWorkOrWorkTooOldOrAnotherStateKeepsItsOwnReading(string activity, double? bgOpen, int minutesAgo, string reads)
    {
        Assert.Equal(reads, Body(activity, bgOpen, minutesAgo)["activity"].AsString());
    }
}
