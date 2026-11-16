using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hooks.Activity;

namespace Ah.Hooks.Tests;

/// <summary>A background task counts as open only while its end could still arrive.</summary>
public sealed class OpenBackgroundTasksTests
{
    private const double NowMs = 1_800_000_000_000;

    private static JsonValue Row(string kind, string status, int minutesAgo) => JsonValue.FromObject(new JsonObject
    {
        ["kind"] = kind,
        ["status"] = status,
        ["ts"] = Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)NowMs).AddMinutes(-minutesAgo)),
    });

    // fewer-turns S3.1: a started subagent is open background work too.
    [Theory]
    [InlineData("background")]
    [InlineData("subagent")]
    public void AStartedBackgroundTaskOrSubagentIsOpen(string kind) =>
        Assert.Equal(1, ActivityRecorder.OpenBackgroundTasks([Row(kind, "started", 5)], NowMs));

    [Theory]
    [InlineData("background", "started", 61)]
    [InlineData("background", "stopped", 5)]
    [InlineData("subagent", "stopped", 5)]
    [InlineData("other", "started", 5)]
    public void NegativeTooOldOrStoppedOrAnotherKindIsNotOpen(string kind, string status, int minutesAgo) =>
        Assert.Equal(0, ActivityRecorder.OpenBackgroundTasks([Row(kind, status, minutesAgo)], NowMs));
}
