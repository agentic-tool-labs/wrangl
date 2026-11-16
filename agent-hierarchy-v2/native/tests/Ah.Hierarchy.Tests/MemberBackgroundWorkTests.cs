using System.Globalization;
using Ah.Core.Json;

namespace Ah.Hierarchy.Tests;

/// <summary>The status document reads a member idle on open background work as working, as the views do.</summary>
public sealed class MemberBackgroundWorkTests
{
    private static string Described(string recordFields, int minutesAgo)
    {
        var rig = HierarchyReadOpsTests.Make();
        var at = DateTimeOffset.Parse("2026-06-01T12:00:00Z", CultureInfo.InvariantCulture).AddMinutes(-minutesAgo).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        HierarchyReadOpsTests.PutActivity(rig, "s1", $"{{\"activity\":\"idle\",\"at\":\"{at}\",\"state_since\":\"{at}\"{recordFields}}}");
        var team = JsonText.Parse("{\"team_id\":\"t\",\"members\":[{\"role\":\"implementor\",\"name\":\"cd\"}]}");
        var roster = JsonText.Parse($"[{{\"type\":\"peer\",\"status\":\"up\",\"name\":\"cd\",\"role\":\"implementor\",\"session_id\":\"s1\",\"pid\":{Environment.ProcessId},\"ts\":\"{at}\"}}]").AsArray();
        return rig.Services.Status.DescribeMembers(rig.Pool, team, roster, "t")[0].Member("activity")!.Value.AsString();
    }

    [Fact]
    public void AnIdleMemberWithBackgroundWorkOpenReadsWorking() => Assert.Equal("working", Described(",\"bg_open\":1", 5));

    [Theory]
    [InlineData(",\"bg_open\":0", 5)]
    [InlineData("", 5)]
    [InlineData(",\"bg_open\":1", 61)]
    public void NegativeNoOpenWorkOrWorkTooOldStaysIdle(string fields, int minutesAgo) => Assert.Equal("idle", Described(fields, minutesAgo));

    [Theory]
    [InlineData(0, null, "stalled-after-compact")]
    [InlineData(1, null, "waiting")]
    [InlineData(0, "shell", "waiting")]
    public void OnlyAMemberWithNoBackgroundWorkIsStalledAfterACompaction(int bgOpen, string? tag, string verdict) =>
        Assert.Equal(verdict, HierarchyModule.MemberVerdict(new MemberEvidence
        {
            Live = true,
            Activity = "idle",
            DispatchOpen = true,
            WorkingAtMs = 1_000,
            CompactedAtMs = 2_000,
            StateSinceMs = 2_500,
            NowMs = 100_000_000,
            BgOpen = bgOpen,
            Tag = tag,
        }));
}
