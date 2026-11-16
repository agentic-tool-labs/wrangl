using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>
/// A sub-orchestrator is a row of its manager's team and the orchestrator of its own child team: one session, two addresses, two team
/// files. The values here are the shape a live sub-orchestrator registered with.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class SubOrchestratorBareIdentityTests : IDisposable
{
    private const string Session = "98507e21-ab4a-4760-8493-68083a38229e";
    private const string Manager = "ah2-tm3";
    private const string Stream = "lane-db";
    private const string Child = "ah2-tm3-lane-db";
    private const string Member = "ah2-tm3-so-lane-db";

    private readonly Sandbox _sandbox = new();
    private readonly ServiceGraph _graph;
    private readonly string _dir;

    public SubOrchestratorBareIdentityTests()
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        _graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, FakeRunner.KnowingSelf(), new FakeDaemonClient());
        _dir = Path.Combine(_sandbox.Home, "proj", ".claude", "ah2");
        Directory.CreateDirectory(Path.Combine(_dir, "teams"));
        WriteTeams();
        Register(ChildFile);
    }

    public void Dispose() => _sandbox.Dispose();

    private string ManagerFile => Path.Combine(_dir, "teams", Manager + ".json");

    private string ChildFile => Path.Combine(_dir, "teams", Child + ".json");

    private void WriteTeams(string childParent = Manager, string childOrchestrator = Session, string? subOrchestrator = Member, string? rowStream = Stream, string childStream = Stream)
    {
        var row = "{\"name\":\"" + Member + "\",\"role\":\"sub-orchestrator\",\"transport_id\":\"w1K:p6X\"" + (rowStream is null ? string.Empty : ",\"stream\":\"" + rowStream + "\"") + "}";
        var streams = subOrchestrator is null ? "{}" : "{\"" + Stream + "\":{\"state\":\"open\",\"sub_orchestrator\":\"" + subOrchestrator + "\"}}";
        TestPlant.Team(_graph.Roster, _graph.Writer, ManagerFile, "{\"members\":[" + row + "],\"streams\":" + streams + "}");
        TestPlant.Team(_graph.Roster, _graph.Writer, ChildFile, "{\"orchestrator\":{\"session_id\":\"" + childOrchestrator + "\"},\"parent\":{\"team\":\"" + childParent + "\",\"stream\":\"" + childStream + "\"},\"members\":[]}");
    }

    /// <summary>Registers the session the way a live sub-orchestrator does: its lane child's address first, then its row in the manager's team.</summary>
    private void Register(string childFileStated, string childAddress = "orchestrator@" + Child) => Register(
        [new BoxRef(childAddress, _graph.Mailbox.BoxDirFor(childFileStated, "orchestrator")), new BoxRef(Member + "@" + Manager, _graph.Mailbox.BoxDirFor(ManagerFile, Member))],
        childFileStated,
        new Dictionary<string, string> { [childAddress] = childFileStated, [Member + "@" + Manager] = ManagerFile });

    private void Register(BoxRef[] boxes, string? teamFile, IReadOnlyDictionary<string, string>? teamFiles) =>
        Assert.True(_graph.Mailbox.WriteRegistration(new RegistrationWrite(Session, boxes, teamFile, "claude", Environment.ProcessId, null, _sandbox.Home, FakeRunner.SelfStart, TeamFiles: teamFiles)));

    private void RemoveChildTeam()
    {
        var args = new JsonObject { ["path"] = ChildFile, ["dir"] = _dir, ["expect"] = _graph.Roster.ReadTeamWithDigest(ChildFile)["digest"] };
        Assert.True(_graph.Writer.Invoke("team", "clear", JsonText.ToUtf8(args, 0), new RequestContext(_dir, _dir, string.Empty, string.Empty)).Ok);
    }

    private BareHost Host() => new(_graph, new StallHost(_graph, _sandbox.AhHome, () => true, () => null));

    private bool Claimed() => Host().Claimed(Session, 1, null, Manager, Member, _dir);

    [Fact]
    public void TheSessionOfASubOrchestratorIsFoundForItsRowInTheManagersTeamAlthoughItsPrimaryAddressIsTheChildTeams()
    {
        var found = _graph.Launch.Snapshots.SessionIdOf(_dir, Manager, new JsonObject { ["name"] = Member });

        Assert.Equal(Session, found is { IsText: true } id ? id.AsString() : null);
    }

    [Fact]
    public void ThatSessionIsNotFoundForTheSameNameInAnotherTeam()
    {
        var found = _graph.Launch.Snapshots.SessionIdOf(_dir, "another-team", new JsonObject { ["name"] = Member });

        Assert.False(found is { IsText: true });
    }

    [Fact]
    public void ASubOrchestratorsOwnLaneChildAddressIsNotAnotherMembersClaimOnItsSession()
    {
        Assert.False(Claimed());
    }

    [Fact]
    public void WithoutTheHierarchyDirNothingIsExemptedAndTheSessionReadsAsClaimed()
    {
        Assert.True(Host().Claimed(Session, 1, null, Manager, Member));
    }

    [Fact]
    public void AnAddressThatIsNotTheLaneChildTheManagerWouldNameIsAClaim()
    {
        Register(ChildFile, "orchestrator@some-other-team");

        Assert.True(Claimed());
    }

    [Fact]
    public void ARegistrationThatStatesAnotherPathForTheChildTeamFileGainsNoExemption()
    {
        var forged = Path.Combine(_sandbox.Home, "forged.json");
        File.WriteAllText(forged, "{\"orchestrator\":{\"session_id\":\"" + Session + "\"},\"parent\":{\"team\":\"" + Manager + "\",\"stream\":\"" + Stream + "\"}}");
        Register(forged);
        WriteTeams(childOrchestrator: "another-session");

        Assert.True(Claimed());
    }

    [Fact]
    public void ALaneTheManagersFileDoesNotRecordForThisMemberGainsNoExemption()
    {
        WriteTeams(subOrchestrator: null);
        Assert.True(Claimed());

        WriteTeams(subOrchestrator: "some-other-member");
        Assert.True(Claimed());

        WriteTeams(rowStream: null);
        Assert.True(Claimed());
    }

    [Fact]
    public void AChildTeamThatDoesNotNameTheManagerTheStreamOrThisSessionGainsNoExemption()
    {
        WriteTeams(childParent: "some-other-manager");
        Assert.True(Claimed());

        WriteTeams(childStream: "lane-other");
        Assert.True(Claimed());

        WriteTeams(childOrchestrator: "another-session");
        Assert.True(Claimed());

        RemoveChildTeam();
        Assert.True(Claimed());
    }

    [Fact]
    public void ASessionRegisteredForAnotherMemberOfTheTeamIsStillAClaim()
    {
        Register([new BoxRef("ah2-tm3-reviewer@" + Manager, _graph.Mailbox.BoxDirFor(ManagerFile, "ah2-tm3-reviewer"))], null, null);

        Assert.True(Claimed());
    }
}
