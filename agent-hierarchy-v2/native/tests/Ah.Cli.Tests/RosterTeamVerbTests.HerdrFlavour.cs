using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>herdr desktop: a herdr team created from a core terminal stores <c>herdr_flavour: "core"</c>; any other team stores nothing.</summary>
public sealed partial class RosterTeamVerbTests
{
    private JsonValue? CommittedFlavour(string transport)
    {
        var (code, stdout, stderr) = Roster(
            "create", "--commit", "--verified",
            "[{\"role\":\"implementor\",\"name\":\"proj-implementor\",\"route\":\"peer\",\"expected_root\":\"" + Project + "\"}]",
            "--transport", transport, "--roster-level", "repo", "--orchestrator-pid", Self);
        Assert.True(code == 0, stdout + stderr);
        return JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("herdr_flavour");
    }

    [Fact]
    public void AHerdrTeamCreatedUnderCoreStoresTheFlavour()
    {
        _extraEnv["HC_TERMINAL_ID"] = "t7";

        Assert.Equal("core", CommittedFlavour("herdr")?.AsString());
    }

    [Fact]
    public void AHerdrTeamCreatedUnderTheTuiStoresNoFlavour()
    {
        _extraEnv["HERDR_ENV"] = "1";
        _extraEnv["HC_TERMINAL_ID"] = "t7";

        Assert.Null(CommittedFlavour("herdr"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateSpawnUnderCoreOutputsTheNonceItLaunchedWith(bool core)
    {
        WriteGlobalRosterMembers("""[{"role":"implementor","model":"opus"}]""");
        _sandbox.Executable("bin/hc");
        _extraEnv["PATH"] = _sandbox.Bin;
        if (core)
        {
            _extraEnv["HC_TERMINAL_ID"] = "t1";
        }

        // no herdr-app: the members run as hidden terminals
        _runner.Exit = argv => argv.Contains("herdr-app") ? 1 : 0;
        _runner.Output = argv =>
            argv.Contains("ls") ? "{\"hidden_finished\":0,\"terminals\":[]}"
            : argv.Contains("run") ? "{\"address\":\"t7\",\"state\":\"live\"}"
            : argv.Contains("get") ? """{"error":{"code":"agent_not_found"}}"""
            : "{}";

        var (code, stdout, stderr) = Roster("create", "--spawn", "--transport", core ? "herdr" : "terminal", "--roster-level", "global", "--orchestrator-pid", Self);

        var calls = string.Join(" | ", _runner.Calls.Select(c => string.Join(' ', c)));
        Assert.True(code == 0, $"exit {code}: {stdout}{stderr} :: {calls}");
        var member = JsonText.Parse(stdout).Member("members")?.AsArray().FirstOrDefault(m => m.Member("role")?.AsString() == "implementor");
        Assert.True(member is not null, stdout);
        if (core)
        {
            var nonce = member!.Value.Member("spawn_nonce")?.AsString();
            Assert.Matches("^[0-9a-f]{16}$", nonce);
            Assert.Contains("ah.spawn=" + nonce, calls, StringComparison.Ordinal);
        }
        else
        {
            Assert.True(member!.Value.Member("spawn_nonce") is null, stdout);
        }
    }

    [Fact]
    public void ANonHerdrTeamStoresNoFlavourEvenInACoreTerminal()
    {
        _extraEnv["HC_TERMINAL_ID"] = "t7";

        Assert.Null(CommittedFlavour("terminal"));
    }
}
