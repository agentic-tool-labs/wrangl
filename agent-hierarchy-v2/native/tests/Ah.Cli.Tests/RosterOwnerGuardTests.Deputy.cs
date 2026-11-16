using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Cli.Tests;

/// <summary><c>duty on|off</c> and the deputy's carve: the chief stays the recorded owner, the deputy on duty runs its own team's owner verbs but the chief's.</summary>
public sealed partial class RosterOwnerGuardTests
{
    private const string DeputyName = "tm-deputy-chief";

    private string DeputyTeamFile => Path.Combine(Hierarchy, "teams", "tm.json");

    private void DeputyTeam(string ownerPid, bool onDuty, string role = "deputy-chief", string extra = "")
    {
        _sandbox.PlantTeam(
            DeputyTeamFile,
            "{\"version\":1,\"team_id\":\"t-tm\",\"transport\":\"terminal\",\"orchestrator\":{\"session_id\":null,\"pid\":" + ownerPid + StartPart(ownerPid)
            + "},\"members\":[{\"name\":\"" + DeputyName + "\",\"role\":\"" + role + "\",\"route\":\"peer\"},{\"name\":\"tm-implementor\",\"role\":\"implementor\",\"route\":\"peer\"}]"
            + (onDuty ? ",\"duty\":{\"deputy\":\"" + DeputyName + "\",\"since\":\"2026-11-08T00:00:00.000Z\",\"cause\":\"duty on\"}" : string.Empty) + extra + "}");
    }

    private void AsDeputy()
    {
        _env[MeshModule.MemberMarkerEnv] = DeputyName;
        _env["WRANGL_TEAM_FILE"] = DeputyTeamFile;
        _nearest = new NearestSession(NearestKind.Member, "tm", DeputyName, Environment.ProcessId);
    }

    private JsonValue StoredDeputyTeam() => JsonText.Parse(StoredTeam(DeputyTeamFile));

    [Fact]
    public void TheChiefPutsTheDeputyOnDutyAndTakesItOffAndTheOrchestratorRecordNeverMoves()
    {
        DeputyTeam(Self, onDuty: false);
        var owner = JsonText.Stringify(StoredDeputyTeam().Member("orchestrator")!.Value, 0);

        var (onCode, onOut, onErr) = Run("duty", "on", DeputyName, "--team", "tm");
        var during = StoredDeputyTeam();
        var (offCode, _, _) = Run("duty", "off", "--team", "tm");
        var after = StoredDeputyTeam();
        var (againCode, again, _) = Run("duty", "off", "--team", "tm");

        Assert.True(onCode == 0, onOut + onErr);
        Assert.Equal(DeputyName, during.Member("duty")!.Value.Member("deputy")!.Value.AsString());
        Assert.Equal(owner, JsonText.Stringify(during.Member("orchestrator")!.Value, 0));
        Assert.Equal(0, offCode);
        Assert.Null(after.Member("duty"));
        Assert.Equal(owner, JsonText.Stringify(after.Member("orchestrator")!.Value, 0));
        Assert.Equal(0, againCode);
        Assert.False(JsonText.Parse(again).Member("changed")!.Value.AsBool());
    }

    [Fact]
    public void NegativeDutyOnAMemberThatIsNotADeputyChiefIsRefusedAndWritesNothing()
    {
        DeputyTeam(Self, onDuty: false);
        var before = StoredTeam(DeputyTeamFile);

        var (code, stdout, _) = Run("duty", "on", "tm-implementor", "--team", "tm");

        Assert.Equal(2, code);
        Assert.Contains("duty-not-deputy", stdout, StringComparison.Ordinal);
        Assert.Equal(before, StoredTeam(DeputyTeamFile));
    }

    [Fact]
    public void NegativeDutyOnALaneTeamIsRefusedAndWritesNothing()
    {
        DeputyTeam(Self, onDuty: false, extra: ",\"parent\":{\"team\":\"mo\",\"stream\":\"ws1\"}");
        var before = StoredTeam(DeputyTeamFile);

        var (code, _, _) = Run("duty", "on", DeputyName, "--team", "tm");

        Assert.Equal(2, code);
        Assert.Equal(before, StoredTeam(DeputyTeamFile));
    }

    /// <summary>The deputy's shell with no member marker: the ancestor search finds its member row, as it does once its registration also holds orchestrator@tm.</summary>
    private void AsUnmarkedDeputy() =>
        _nearest = new NearestSession(NearestKind.Member, "tm", DeputyName, Environment.ProcessId);

    [Theory]
    [InlineData(true, "spawn-one")]
    [InlineData(true, "dismiss")]
    [InlineData(false, "spawn-one")]
    [InlineData(false, "dismiss")]
    public void TheDeputyOnDutyPassesTheGuardForAnOwnTeamVerb(bool marked, string verb)
    {
        DeputyTeam(GonePid.ToString(System.Globalization.CultureInfo.InvariantCulture), onDuty: true);
        if (marked)
        {
            AsDeputy();
        }
        else
        {
            AsUnmarkedDeputy();
        }

        string[] args = verb == "dismiss" ? ["dismiss", "tm-implementor", "--team", "tm"] : [verb, "--team", "tm"];
        var (_, stdout, _) = Run(args);

        Assert.Null(Refusal(stdout).Refused);
    }

    public static TheoryData<bool, string> ChiefsVerbs()
    {
        var data = new TheoryData<bool, string>();
        foreach (var marked in new[] { true, false })
        {
            foreach (var verb in new[] { "quit", "stop", "disband", "untrack", "adopt", "duty", "lane-caps", "create" })
            {
                data.Add(marked, verb);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ChiefsVerbs))]
    public void NegativeTheDeputyOnDutyIsRefusedTheChiefsVerbs(bool marked, string verb)
    {
        DeputyTeam(GonePid.ToString(System.Globalization.CultureInfo.InvariantCulture), onDuty: true);
        if (marked)
        {
            AsDeputy();
        }
        else
        {
            AsUnmarkedDeputy();
        }

        string[] args = verb switch
        {
            "duty" => ["duty", "off", "--team", "tm"],
            "untrack" => ["untrack", "tm-implementor", "--team", "tm"],
            "lane-caps" => ["lane-caps", "--max-lanes", "3", "--team", "tm"],
            _ => [verb, "--team", "tm"],
        };
        var before = StoredTeam(DeputyTeamFile);

        var (code, stdout, _) = Run(args);

        Assert.Equal(2, code);
        Assert.Equal(($"deputy-cannot-{verb}", "member-marker"), Refusal(stdout));
        Assert.Equal(before, StoredTeam(DeputyTeamFile));
    }

    [Fact]
    public void NegativeTheSameDeputyOffDutyIsRefusedAnOwnTeamVerb()
    {
        DeputyTeam(GonePid.ToString(System.Globalization.CultureInfo.InvariantCulture), onDuty: false);
        AsDeputy();

        var (code, stdout, _) = Run("spawn-one", "--team", "tm");

        Assert.Equal(2, code);
        Assert.NotNull(Refusal(stdout).Refused);
    }
}
