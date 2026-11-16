#pragma warning disable CA1416 // the daemon's file reader is the Unix one, as in the other daemon tests

using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Cli.Tests;

/// <summary>Which sessions a newly queued QA job rings: the live QA boxes of the job's team, found by the member's role, and no one else.</summary>
public sealed class QaDoorbellTests
{
    private static QaJob Job(string? team = "proj") =>
        new("e1", new string('a', 40), null, "core", "/repo", false, "adhoc", ["r1@proj"], team, 0, "queued", null, null, null, null, null, null);

    private static JsonObject Registration(string session, string team, params string[] addresses)
    {
        var list = new JsonArray();
        foreach (var address in addresses)
        {
            list.Add(address);
        }

        return new JsonObject
        {
            ["session_id"] = session,
            ["team_file"] = $"/p/.claude/ah2/teams/{team}.json",
            ["cwd"] = "/p",
            ["addresses"] = list,
        };
    }

    // The member names a spawn gives: <repo>-<role>[-N], so a QA member's name does not start with "qa".
    private static readonly Dictionary<string, string> Roles = new(StringComparer.Ordinal)
    {
        ["proj-qa"] = "qa",
        ["proj-qa-2"] = "qa",
        ["qa-helper"] = "implementor",
        ["proj-implementor"] = "implementor",
        ["orchestrator"] = "orchestrator",
    };

    private static JsonObject? MemberOf(string teamFile, string name) =>
        Roles.TryGetValue(name, out var role) && !teamFile.EndsWith("/gone.json", StringComparison.Ordinal)
            ? new JsonObject { ["name"] = name, ["role"] = role }
            : null;

    private static List<JsonObject> Ring(QaJob job, HashSet<string> liveSessions, string? refusal = null, List<string>? log = null, params JsonObject[] registrations)
    {
        var sent = new List<JsonObject>();
        DaemonServices.RingLiveQa(
            registrations,
            r => liveSessions.Contains(r["session_id"].AsString()),
            MemberOf,
            request =>
            {
                sent.Add(request);
                return refusal;
            },
            line => log?.Add(line),
            job);
        return sent;
    }

    [Fact]
    public void ACorruptOrMissingTeamFileGivesNoMemberAndIsLoggedWhileAGoodOneIsRead()
    {
        var dir = Directory.CreateTempSubdirectory("ahprobe.").FullName;
        var io = Ah.Platform.PlatformModule.CreateIo(null);
        var env = new Ah.TestKit.FakeEnvironment(
            new Dictionary<string, string> { ["HOME"] = dir + "/home", ["AH_HOME"] = dir + "/ah", ["AH_TEST"] = "1", ["TZ"] = "UTC" },
            new Ah.Abstractions.PluginInfo("/ahroot", "wrangl", "0.0.0", "1"));
        Ah.Roster.IRosterCore roster = null!;
        var config = Ah.Config.ConfigModule.CreateCore(io.Files, env, () => roster, new Ah.Config.ProcessFacts(dir, 4242));
        roster = Ah.Roster.RosterModule.CreateCore(io.Files, env, config, io.Processes);
        var good = Path.Combine(dir, "good.json");
        var corrupt = Path.Combine(dir, "corrupt.json");
        File.WriteAllText(good, """{"members":[{"name":"proj-qa","role":"qa"}]}""");
        File.WriteAllText(corrupt, "{ not json");
        var log = new List<string>();

        Assert.Equal("qa", DaemonServices.TeamMember(roster, good, "proj-qa", log.Add)?.Get("role")?.AsString());
        Assert.Null(DaemonServices.TeamMember(roster, good, "someone-else", log.Add));
        Assert.Empty(log);
        Assert.Null(DaemonServices.TeamMember(roster, corrupt, "proj-qa", log.Add));
        Assert.Null(DaemonServices.TeamMember(roster, Path.Combine(dir, "missing.json"), "proj-qa", log.Add));
        Assert.Equal(2, log.Count);
        Assert.All(log, line => Assert.Contains("could not be read", line, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryLiveQaBoxOfTheJobsTeamIsMailedTheQaQueuedEvent()
    {
        var sent = Ring(
            Job(),
            ["s-qa1", "s-qa2"],
            null,
            null,
            Registration("s-qa1", "proj", "proj-qa@proj"),
            Registration("s-qa2", "proj", "proj-qa-2@proj"));

        Assert.Equal(["proj-qa@proj", "proj-qa-2@proj"], sent.Select(static r => r["to"].AsString()));
        Assert.All(sent, r =>
        {
            Assert.Equal("ahd", r["from"].AsString());
            Assert.Equal("event", r["kind"].AsString());
            Assert.Equal("qa-queued", r["event"].AsString());
            Assert.Equal("/p/.claude/ah2", r["hierarchy_dir"].AsString());
            Assert.Contains("ah qa next", r["text"].AsString(), StringComparison.Ordinal);
        });
        Assert.Equal(["s-qa1", "s-qa2"], sent.Select(static r => r["to_session"].AsString()));
    }

    [Fact]
    public void ANotLiveQaAQaOfAnotherTeamANonQaMemberNamedLikeOneAndAnyOtherBoxAreNotMailed()
    {
        var sent = Ring(
            Job(),
            ["s-qa1", "s-qa3", "s-helper", "s-impl", "s-orch"],
            null,
            null,
            Registration("s-qa1", "proj", "proj-qa@proj"),
            Registration("s-qa2", "proj", "proj-qa-2@proj"),
            Registration("s-qa3", "other", "proj-qa@other"),
            Registration("s-helper", "proj", "qa-helper@proj"),
            Registration("s-impl", "proj", "proj-implementor@proj"),
            Registration("s-orch", "proj", "orchestrator@proj"));

        Assert.Equal(["proj-qa@proj"], sent.Select(static r => r["to"].AsString()));
    }

    [Fact]
    public void ATeamWithNoLiveQaAndAJobWithNoTeamSendNothing()
    {
        Assert.Empty(Ring(Job(), [], null, null, Registration("s-qa1", "proj", "proj-qa@proj")));
        Assert.Empty(Ring(Job(team: null), ["s-qa1"], null, null, Registration("s-qa1", "proj", "proj-qa@proj")));
    }

    [Fact]
    public void ARegistrationWithoutATeamFileOrWithAnUnreadableTeamFileIsSkippedAndARefusedSendIsLogged()
    {
        var log = new List<string>();
        var noTeam = Registration("s-qa1", "proj", "proj-qa@proj");
        _ = noTeam.Remove("team_file");

        Assert.Empty(Ring(Job(), ["s-qa1"], null, log, noTeam));
        Assert.Empty(Ring(Job(), ["s-qa1"], null, log, Registration("s-qa1", "gone", "proj-qa@gone")));
        Assert.Single(Ring(Job(), ["s-qa2"], "no such box", log, Registration("s-qa2", "proj", "proj-qa-2@proj")));
        Assert.Contains("qa-queued mail to proj-qa-2@proj refused: no such box", log);
    }
}
