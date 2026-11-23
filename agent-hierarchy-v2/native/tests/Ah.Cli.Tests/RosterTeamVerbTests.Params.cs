using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>Role parameters at spawn and at create (role-params RP-D6, RP-D10, RP-D11): a missing value is reported and never stops a launch.</summary>
public sealed partial class RosterTeamVerbTests
{
    /// <summary>A global config whose architect declares engine-profile (set) and asset-root (not set), with an architect on the roster.</summary>
    private void WriteParamsRoster()
    {
        var file = Path.Combine(_sandbox.Home, ".claude", "ah2", "agent-hierarchy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{"version":1,"enabled":true,"params":{"engine-profile":"/p/engine"},"roles":{"architect":{"params":["engine-profile","asset-root"]}},"roster":{"route":"peer","members":[{"role":"architect","model":"opus"}]}}""");
    }

    /// <summary>A3 (RP-D6): spawn-one of a role missing a value exits 0, its entry carries the values, and its warnings name the missing one only.</summary>
    [Fact]
    public void SpawnOneOfARoleMissingAParamExitsZeroAndWarns()
    {
        WriteParamsRoster();

        var (code, stdout, stderr) = Roster("spawn-one", "architect", "--team", "proj", "--orchestrator-pid", Self, "--dry-run");

        Assert.True(code == 0, stdout + stderr);
        var dry = JsonText.Parse(stdout).AsObject();
        Assert.Equal("/p/engine", dry["params"].AsObject()["engine-profile"].AsString());
        Assert.Equal(JsonValue.Null, dry["params"].AsObject()["asset-root"]);
        var warning = Assert.Single(dry["warnings"].AsArray(), w => w.AsString().Contains("needs parameter", StringComparison.Ordinal)).AsString();
        Assert.Contains("(role architect) needs parameter asset-root, which is not set for this repo; it was started anyway and told so. Set it: ah roster param set asset-root <value>", warning, StringComparison.Ordinal);
        Assert.Contains("ah roster: warning — ", stderr, StringComparison.Ordinal);
    }

    /// <summary>RP-D10/D11: a committed create with --param writes it at repo-user before planning, so the launched member's plan entry carries it and no warning is left.</summary>
    [Fact]
    public void CreateYesWithAParamWritesItAndTheMembersPlanEntryCarriesIt()
    {
        WriteParamsRoster();
        _sandbox.Executable("bin/hc");
        _extraEnv["PATH"] = _sandbox.Bin;
        _runner.Exit = argv => argv.Contains("herdr-app") ? 1 : 0;
        _runner.Output = argv => argv.Contains("get") ? """{"error":{"code":"agent_not_found"}}""" : "{}";

        var (code, stdout, stderr) = Roster("create", "--yes", "--transport", "terminal", "--roster-level", "global", "--orchestrator-pid", Self, "--param", "asset-root=/a");

        Assert.True(code == 0, stdout + stderr);
        var output = JsonText.Parse(stdout);
        var entry = output.Member("plan")!.Value.Member("members")!.Value.AsArray().Single(m => m.Member("role")?.AsString() == "architect");
        Assert.Equal("/a", entry.Member("params")!.Value.Member("asset-root")!.Value.AsString());
        var written = output.Member("commit")!.Value.Member("param_writes")!.Value.AsArray().Single();
        Assert.True(written.Member("written")!.Value.AsBool());
        Assert.Contains("\"asset-root\": \"/a\"", File.ReadAllText(written.Member("path")!.Value.AsString()), StringComparison.Ordinal);
        Assert.DoesNotContain("needs parameter", stderr, StringComparison.Ordinal);
    }

    /// <summary>Negative (Amendment 2 G3): a plan-only create shows the param it would write and writes nothing; the plan lists the unset one as NOT SET; an undeclared name is noted, not refused.</summary>
    [Fact]
    public void APlanOnlyCreateWritesNoParamAndListsTheUnsetOne()
    {
        WriteParamsRoster();

        var (code, stdout, stderr) = Roster("create", "--plan", "--param", "asset-root=/a", "--param", "nobody-needs=x");

        Assert.True(code == 0, stdout + stderr);
        var plan = JsonText.Parse(stdout).AsObject();
        var writes = plan["param_writes"].AsArray();
        Assert.All(writes, w => Assert.False(w.Member("written")!.Value.AsBool()));
        Assert.False(File.Exists(writes[0].Member("path")!.Value.AsString()));
        Assert.Equal("NOT SET", plan["params"].AsArray().Single(p => p.Member("name")!.Value.AsString() == "asset-root").Member("value")!.Value.AsString());
        Assert.Contains("parameter nobody-needs is declared by no role in this roster", stderr, StringComparison.Ordinal);
    }
}
