using Ah.Core.Json;

namespace Ah.Watch.Tests;

/// <summary>D9 on the topology Flow reads: the same rule as the tree's, so only a leader holding its open lane's child team is served a path.</summary>
public sealed partial class TreeLanesTests
{
    [Theory]
    [InlineData("open", "so", true)]
    [InlineData("open", "other", false)]
    [InlineData("done", "so", false)]
    public void ALeaderHoldingItsOpenLaneIsServedALivePathAndNegativeNotAClosedLaneOrAnotherHolder(string state, string session, bool served)
    {
        var reader = new TreeReader();
        reader.Teams[MgrPath] = MgrDoc(Streams(("s1", state)), "{\"name\":\"so-s1\",\"role\":\"sub-orchestrator\",\"session_id\":\"" + session + "\"},{\"name\":\"dev\",\"role\":\"implementor\",\"session_id\":\"so\"}");
        reader.Teams[ChildPath] = ChildDoc("mgr", "s1");
        reader.KnownDirs.Add(Canon);
        var (model, _) = MakeLanes(reader);
        using var owner = model;

        var nodes = JsonText.Parse(Body(model, $"topology:{D}/mgr")).Member("nodes")!.Value;
        var paths = nodes.Member("live_paths");
        Assert.Equal(served, paths is not null);
        if (served)
        {
            Assert.Equal($"/t/{D}/mgr-s1/m/%40orchestrator", paths!.Value.Member($"member:{D}/mgr/so-s1")!.Value.AsString());
            Assert.Null(paths.Value.Member($"member:{D}/mgr/dev"));
        }
    }
}
