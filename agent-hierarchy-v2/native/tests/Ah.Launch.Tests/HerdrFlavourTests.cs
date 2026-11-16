using Ah.Core.Json;
using Ah.Core.Mux;
using Ah.TestKit;

namespace Ah.Launch.Tests;

/// <summary>herdr desktop P1: the flavour from the env and the team record, the guard, and the layout rule without positions.</summary>
public sealed class HerdrFlavourTests
{
    private sealed class Runner(bool tmuxUp) : IMuxRunner
    {
        public MuxResult Run(string binary, IReadOnlyList<string> args, double timeoutMs, int outputCapBytes = 1 << 20, string? workingDirectory = null, IReadOnlyDictionary<string, string?>? environment = null) =>
            new(tmuxUp, string.Empty, string.Empty, tmuxUp ? 0 : 1, false, tmuxUp ? null : "no server");
    }

    private static MuxTransports Transports(bool tmuxUp, params (string Name, string Value)[] vars) =>
        new(new FakeEnvironment(vars.ToDictionary(v => v.Name, v => v.Value)), new Runner(tmuxUp));

    [Theory]
    [InlineData("1", "t7", "herdr", HerdrFlavour.Tui)] // a TUI run inside a core terminal
    [InlineData("0", "t7", "herdr", HerdrFlavour.Core)]
    [InlineData("", "t7", "herdr", HerdrFlavour.Core)]
    [InlineData(null, "t7", "herdr", HerdrFlavour.Core)]
    [InlineData(null, "", "tmux", null)] // an empty HC_TERMINAL_ID is not core
    [InlineData(null, null, "tmux", null)]
    public void DetectionTable(string? herdrEnv, string? terminalId, string transport, string? flavour)
    {
        var vars = new List<(string, string)>();
        if (herdrEnv is not null)
        {
            vars.Add(("HERDR_ENV", herdrEnv));
        }

        if (terminalId is not null)
        {
            vars.Add(("HC_TERMINAL_ID", terminalId));
        }

        var t = Transports(true, [.. vars]);
        Assert.Equal(transport, t.Detect());
        Assert.Equal(flavour, t.Flavour());
    }

    [Fact]
    public void NeitherSetAndNoTmuxIsTerminal() => Assert.Equal("terminal", Transports(false).Detect());

    [Fact]
    public void TheStoredFlavourWinsAndAMissingFieldIsTui()
    {
        Assert.Equal(HerdrFlavour.Core, HerdrFlavour.OfTeam(new JsonObject { ["transport"] = "herdr", ["herdr_flavour"] = "core" }));
        Assert.Equal(HerdrFlavour.Tui, HerdrFlavour.OfTeam(new JsonObject { ["transport"] = "herdr" }));
        Assert.Equal(HerdrFlavour.Tui, HerdrFlavour.OfTeam(new JsonObject { ["herdr_flavour"] = "Core" }));
        Assert.Equal(HerdrFlavour.Tui, HerdrFlavour.OfTeam(null));
    }

    [Fact]
    public void TheGuardCoversTheCoreBinaries()
    {
        var guard = new MuxGuard(new FakeEnvironment(new Dictionary<string, string>()), null!, null!, 1);
        Assert.Contains("hc", guard.GuardedBinaries);
        Assert.Contains("herdr-app", guard.GuardedBinaries);
        Assert.DoesNotContain("hcx", guard.GuardedBinaries);
    }

    private static JsonArray OnePane(double width, double height) =>
        [new JsonObject { ["pane_id"] = "t1", ["rect"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = width, ["height"] = height } }];

    [Fact]
    public void WithoutPositionsTheAspectRuleDecides()
    {
        // 100x40 in a 100x40 tab, grid: the band rule says down (40*2*2 > 40*3); the aspect rule says right (100 > 80).
        var rules = new LayoutRules();
        Assert.Equal(new SplitDecision("t1", "right"), rules.NextSplit("grid", 3, "t1", [], OnePane(100, 40), positions: false));
        Assert.Equal(new SplitDecision("t1", "down"), rules.NextSplit("grid", 3, "t1", [], OnePane(100, 40)));
        Assert.Equal(new SplitDecision("t1", "down"), rules.NextSplit("grid", 3, "t1", [], OnePane(100, 40), positions: true));
    }

    [Fact]
    public void WithoutPositionsColumnsStillGoRight() =>
        Assert.Equal("right", new LayoutRules().NextSplit("auto", 2, "t1", [], OnePane(40, 100), positions: false).Direction);
}
