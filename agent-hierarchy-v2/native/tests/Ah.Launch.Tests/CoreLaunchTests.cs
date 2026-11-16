using Ah.Core.Json;

namespace Ah.Launch.Tests;

/// <summary>A member spawned under herdr core with no app runs as a hidden terminal, and its address becomes the member's transport_id.</summary>
public sealed class CoreLaunchTests
{
    private const string MemberName = "alpha-implementor";

    private static ScenarioRig CoreRig()
    {
        var rig = ScenarioRig.Create(env =>
        {
            env.Remove("HERDR_ENV");
            env.Remove("HERDR_PANE_ID");
            env["HC_TERMINAL_ID"] = "t1";
        });
        rig.Mux.Override = request => request.Argv switch
        {
            ["hc", "ls", ..] => FakeMux.Out("{\"hidden_finished\":0,\"terminals\":[]}"),
            ["hc", "run", ..] => FakeMux.Out("{\"address\":\"t7\",\"agent\":\"claude\",\"state\":\"live\"}"),
            ["hc", ..] => FakeMux.Out("{}"),
            _ => null,
        };
        return rig;
    }

    private static JsonObject Shaped(ScenarioRig rig)
    {
        var outcome = rig.Shapes.SpawnShape(new JsonObject { ["role"] = "implementor", ["name"] = MemberName }, "herdr", rig.Context);
        Assert.Null(outcome.Fail);
        return new JsonObject { ["role"] = "implementor", ["name"] = MemberName, ["spawn"] = outcome.Shape };
    }

    [Fact]
    public void ASpawnUnderCoreWithNoAppStoresTheHiddenTerminalsAddress()
    {
        var rig = CoreRig();

        var result = rig.Launcher.LaunchMemberCore(Shaped(rig), "herdr", rig.Context);

        Assert.True(result.Get("launch_status")?.AsString() == "ready", string.Join(" | ", rig.Mux.Calls) + " :: " + result.Get("error")?.AsString());
        Assert.Equal("t7", result.Get("transport_id")?.AsString());
        var run = Assert.Single(rig.Mux.Calls, c => c.StartsWith("hc run ", StringComparison.Ordinal));
        Assert.Contains("--no-parent", run, StringComparison.Ordinal);
        var nonce = result.Get("spawn_nonce")?.AsString();
        Assert.Matches("^[0-9a-f]{16}$", nonce);
        Assert.Contains("--label ah.spawn=" + nonce, run, StringComparison.Ordinal);
        Assert.DoesNotContain("--pane", run, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Mux.Calls, c => c.StartsWith("/bin/sh", StringComparison.Ordinal));
    }

    [Fact]
    public void ASpawnThatLosesTheNameIsRefusedAndItsTerminalIsClosedByNonceNeverByName()
    {
        var rig = CoreRig();
        var nameLookups = 0;
        rig.Mux.Override = request => request.Argv switch
        {
            ["hc", "ls", _, _, var label] when label.StartsWith("ah.name=", StringComparison.Ordinal) =>
                FakeMux.Out(Ls(nameLookups++ switch { 0 => [], 1 => ["t3", "t7"], _ => ["t3"] })),
            ["hc", "ls", _, _, var label] when label.StartsWith("ah.spawn=", StringComparison.Ordinal) => FakeMux.Out(Ls(["t7"])),
            ["hc", "run", ..] => FakeMux.Out("{\"address\":\"t7\",\"state\":\"live\"}"),
            ["hc", "get", var t, ..] => FakeMux.Out($"{{\"address\":\"{t}\",\"state\":\"live\",\"created_ms\":{(t == "t3" ? 1 : 2)}}}"),
            ["hc", ..] => FakeMux.Out("{}"),
            _ => null,
        };

        var result = rig.Launcher.LaunchMemberCore(Shaped(rig), "herdr", rig.Context);

        Assert.Equal("name-in-use", result.Get("launch_result")?.AsObject().Get("refused")?.AsString());
        Assert.Contains("hc kill t7", rig.Mux.Calls);
        Assert.DoesNotContain(rig.Mux.Calls, c => c.StartsWith("hc kill", StringComparison.Ordinal) && c != "hc kill t7");
        Assert.Single(rig.Mux.Calls, c => c.StartsWith("hc run ", StringComparison.Ordinal));
    }

    /// <summary>A core rig with <c>hc</c> on PATH; t1 is the caller's own terminal (HC_TERMINAL_ID), and the listed terminals carry these names.</summary>
    private static ScenarioRig CoreWithTerminals(Dictionary<string, string> named)
    {
        var rig = CoreRig();
        var hc = rig.Sandbox.Root + "/bin/hc";
        File.WriteAllText(hc, "#!/bin/sh\n");
        File.SetUnixFileMode(hc, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        rig.Mux.Override = request => request.Argv switch
        {
            ["hc", "ls", ..] => FakeMux.Out(Ls([.. named.Keys])),
            ["hc", "get", var t, ..] when named.TryGetValue(t, out var name) =>
                FakeMux.Out($"{{\"address\":\"{t}\",\"state\":\"live\",\"labels\":{{\"ah.name\":\"{name}\"}}}}"),
            ["hc", ..] => FakeMux.Out("{}"),
            _ => null,
        };
        return rig;
    }

    [Fact]
    public void UnderCoreTheTeardownArmNeverTakesTheCallersOwnTerminal()
    {
        var rig = CoreWithTerminals(new() { ["t1"] = "alpha-implementor", ["t2"] = "alpha-reviewer" });

        var arm = TeardownPasteTests.TeardownOver(rig).HerdrArmMatches(rig.Sandbox.Root + "/ah", new TeamScope(null, NoTeam: true), rig.Context);

        Assert.True(arm.Ok, arm.Reason);
        var panes = arm.Matched.Select(m => m.Member("pane_id")?.AsString() ?? m.Member("transport_id")?.AsString()).ToList();
        Assert.DoesNotContain("t1", panes);
        Assert.Contains("t2", panes);
    }

    [Fact]
    public void AHerdrMemberWithOnlyASpawnNonceIsClosableAndOneWithNeitherIsNot()
    {
        var teardown = TeardownPasteTests.TeardownOver(CoreRig());
        var nonceOnly = new JsonObject { ["name"] = "alpha-implementor", ["route"] = "peer", ["spawn_nonce"] = "0123456789abcdef" };
        var neither = new JsonObject { ["name"] = "alpha-reviewer", ["route"] = "peer" };

        Assert.Equal(["alpha-implementor"], teardown.ClosableMembers([nonceOnly, neither]).Select(m => m.Member("name")!.Value.AsString()));
    }

    [Fact]
    public void ACloseByANonceNoTerminalCarriesPassesNothingToHerdr()
    {
        var rig = CoreRig();
        var member = new JsonObject { ["name"] = "alpha-implementor", ["route"] = "peer", ["spawn_nonce"] = "0123456789abcdef" };

        var result = TeardownPasteTests.TeardownOver(rig).CloseAndVerify([member], "herdr", [], rig.Context);

        Assert.DoesNotContain(rig.Mux.Calls, c => c.StartsWith("hc kill", StringComparison.Ordinal));
        Assert.Contains("nothing was closed", result.Results[0].Get("note")?.AsString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ANonceLookupThatIsUnreadableOrFindsSeveralClosesNothingAndKeepsTheRowOpen(bool unreadable)
    {
        var rig = CoreRig();
        rig.Mux.Override = request => request.Argv switch
        {
            ["hc", "ls", ..] => unreadable ? FakeMux.Exit(1, stderr: "boom") : FakeMux.Out(Ls(["t7", "t8"])),
            ["hc", ..] => FakeMux.Out("{}"),
            _ => null,
        };
        var member = new JsonObject { ["name"] = "alpha-implementor", ["route"] = "peer", ["spawn_nonce"] = "0123456789abcdef" };

        var result = TeardownPasteTests.TeardownOver(rig).CloseAndVerify([member], "herdr", [], rig.Context);

        Assert.False(result.Results[0].Get("closed")!.Value.IsTruthy);
        Assert.Contains("nothing was closed", result.Results[0].Get("error")?.AsString(), StringComparison.Ordinal);
        Assert.Contains("alpha-implementor", result.StillLive);
        Assert.DoesNotContain(rig.Mux.Calls, c => c.StartsWith("hc kill", StringComparison.Ordinal));
    }

    [Fact]
    public void ANonceLookupThatFindsOneTerminalWritesItsAddressOnTheRowBeforeTheClose()
    {
        var rig = CoreWithTerminals(new() { ["t7"] = "alpha-implementor" });
        var member = new JsonObject { ["name"] = "alpha-implementor", ["route"] = "peer", ["spawn_nonce"] = "0123456789abcdef" };

        _ = TeardownPasteTests.TeardownOver(rig).CloseAndVerify([member], "herdr", [], rig.Context);

        Assert.Equal("t7", member.Get("transport_id")?.AsString());
        Assert.Contains("hc kill t7", rig.Mux.Calls);
    }

    private static string Ls(string[] addresses) =>
        "{\"hidden_finished\":0,\"terminals\":[" + string.Join(",", addresses.Select(a => $"{{\"address\":\"{a}\",\"state\":\"live\"}}")) + "]}";

    [Fact]
    public void ASpawnUnderTuiStillGoesThroughTheShellAndStoresNoAddress()
    {
        var rig = ScenarioRig.Create();
        var member = Shaped(rig);

        _ = rig.Launcher.LaunchMemberCore(member, "herdr", rig.Context);

        Assert.Contains(rig.Mux.Calls, c => c.StartsWith("/bin/sh -c herdr agent start", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Mux.Calls, c => c.StartsWith("hc ", StringComparison.Ordinal));
        Assert.True(member.Get("transport_id") is null or { IsNull: true });
    }
}
