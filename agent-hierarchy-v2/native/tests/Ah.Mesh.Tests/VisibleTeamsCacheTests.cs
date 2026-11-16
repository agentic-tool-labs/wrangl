using Ah.Abstractions;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>Mailbox.VisibleTeams reads a team record again only when its stamp moved, and never caches a file written in the last 2 s.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class VisibleTeamsCacheTests
{
    private static string Team(int pid) => $"{{\"members\":[{{\"name\":\"b\",\"role\":\"reviewer\",\"kind\":\"claude\"}}],\"orchestrator\":{{\"pid\":{pid}}}}}\n";

    private static string Pid(VisibleTeam team) => Ah.Core.Json.JsonText.Stringify(team.Data.AsObject().Get("orchestrator")!.Value.AsObject().Get("pid")!.Value, 0);

    [Fact]
    public void AnOldUnchangedTeamIsNotReadAgainAndAChangedOrFreshOneIs()
    {
        var sandbox = UnitSandbox.Create();
        var hierarchy = sandbox.Root + "/h";
        Directory.CreateDirectory(hierarchy + "/teams");
        var teamFile = hierarchy + "/teams/tm.json";
        var old = DateTime.UtcNow.AddSeconds(-10);
        File.WriteAllText(teamFile, Team(12345));
        File.SetLastWriteTimeUtc(teamFile, old);
        var vars = new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = sandbox.Root + "/ah" };
        var mailbox = MeshFixture.Create(vars, DateTimeOffset.Parse("2026-01-01T12:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture), sandbox.Root).Mailbox;
        var context = new RequestContext(sandbox.Root, hierarchy, string.Empty, string.Empty);

        var first = mailbox.VisibleTeams(context).Single();
        Assert.Same(first.Data.AsObject(), mailbox.VisibleTeams(context).Single().Data.AsObject());

        // negative: a rewritten team file (a new size, so a new stamp) is read again
        File.WriteAllText(teamFile, Team(123456));
        File.SetLastWriteTimeUtc(teamFile, old.AddSeconds(1));
        Assert.Equal("123456", Pid(mailbox.VisibleTeams(context).Single()));

        // negative: on a 1 s mtime filesystem a same-size write in the same second keeps size and mtime; a fresh mtime is not cached
        var now = DateTime.UtcNow;
        File.WriteAllText(teamFile, Team(100001));
        File.SetLastWriteTimeUtc(teamFile, now);
        Assert.Equal("100001", Pid(mailbox.VisibleTeams(context).Single()));
        File.WriteAllText(teamFile, Team(100002));
        File.SetLastWriteTimeUtc(teamFile, now);
        Assert.Equal("100002", Pid(mailbox.VisibleTeams(context).Single()));
    }
}
