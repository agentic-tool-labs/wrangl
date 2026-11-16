using Ah.Core.Json;

namespace Ah.Launch.Tests;

/// <summary>A launch that reports success but whose member never registers is reported unconfirmed; one that registers is left alone.</summary>
public sealed class UnconfirmedLaunchTests
{
    private static JsonObject Launch(Func<string, string, bool> registered, string transport = "tmux", string? screen = null)
    {
        var rig = ScenarioRig.Create();
        if (screen is not null)
        {
            rig.Mux.Screens["alpha-implementor"] = screen;
        }

        var member = new JsonObject { ["role"] = "implementor", ["name"] = "alpha-implementor" };
        var outcome = rig.Shapes.SpawnShape(member, transport, rig.Context);
        Assert.Null(outcome.Fail);
        member["spawn"] = outcome.Shape;
        var launcher = rig.Launcher with { Registered = registered };
        var batch = launcher.LayoutAndLaunch(rig.Cwd, [member], transport, "split", rig.Cwd, "spawn-one", rig.Context);
        Assert.Null(batch.Fail);
        return batch.Members[0];
    }

    [Fact]
    public void AMemberThatNeverRegistersIsReportedUnconfirmedWithItsReason()
    {
        var launched = Launch((_, _) => false);

        Assert.Equal("unconfirmed", launched.Get("launch_status")!.Value.AsString());
        Assert.Contains("did not register as alpha-implementor@", launched.Get("unconfirmed_reason")!.Value.AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberStoppedAtClaudesTrustScreenIsReportedAsNeedingFolderTrust()
    {
        var launched = Launch((_, _) => false, "herdr", " Quick safety check: Is this a project you created or one you trust?\n ❯ No, exit\n   Yes, I trust this folder\n");

        Assert.Equal("unconfirmed", launched.Get("launch_status")!.Value.AsString());
        Assert.StartsWith("needs folder trust in ", launched.Get("unconfirmed_reason")!.Value.AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberOnAnotherScreenKeepsTheGeneralReason()
    {
        var launched = Launch((_, _) => false, "herdr", " Choose the text style that looks best with your terminal\n");

        Assert.Contains("did not register as", launched.Get("unconfirmed_reason")!.Value.AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberThatRegistersWithinTheWaitKeepsItsStatus()
    {
        var polls = 0;
        var launched = Launch((_, _) => ++polls >= 3);

        Assert.Equal("dispatched", launched.Get("launch_status")!.Value.AsString());
        Assert.Null(launched.Get("unconfirmed_reason"));
    }
}
