using Ah.Mesh;

namespace Ah.Hooks.Tests;

/// <summary>The command that opened #347, with no title or body naming a verb, is not an owner-verb call.</summary>
public sealed partial class HooksHostTests
{
    [Theory]
    [InlineData("{Root}/bin/ah msg new --type request --from sub-orchestrator --to sub-orchestrator --to-name ah2-tm3-so-lane-db --slug crash-resume-ui-surface --cwd /r --title plain")]
    [InlineData("ah msg new --type request --from sub-orchestrator --to sub-orchestrator --to-name ah2-tm3-so-lane-db --slug crash-resume-ui-surface --cwd /r --title plain")]
    public void AMarkedSessionRunningAMsgNewWithNoVerbTextIsNotRefusedByTheOwnerVerbLayer(string template)
    {
        var root = MakePluginRoot();
        var rig = new Rig(new Dictionary<string, string> { [MeshModule.MemberMarkerEnv] = "t-sub-orchestrator-1" }, root);

        var outcome = rig.Run("pretooluse-ah-cli", BashAs(string.Empty, template.Replace("{Root}", root, StringComparison.Ordinal)));

        Assert.False(IsDenied(outcome) && Reason(outcome) == MeshModule.OwnerVerbRefusal, template);
    }
}
