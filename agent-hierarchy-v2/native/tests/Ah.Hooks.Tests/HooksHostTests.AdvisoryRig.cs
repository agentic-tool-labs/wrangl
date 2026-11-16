using Ah.Mesh;

namespace Ah.Hooks.Tests;

public sealed partial class HooksHostTests
{
    private const string StuckSession = "sess-so";

    private const string StuckDeniedCommand = "ah roster transfer lane-ui-a --team mo-ws1";

    /// <summary>A rig with the lane teams on disk and the peer-record state written for real, so the rows a hook leaves can be read back.</summary>
    private static Rig StuckRig(Dictionary<string, string>? env = null)
    {
        var root = MakePluginRoot();
        var variables = new Dictionary<string, string> { [MeshModule.MemberMarkerEnv] = "mo-so-ws1" };
        foreach (var (key, value) in env ?? [])
        {
            variables[key] = value;
        }

        var rig = new Rig(variables, root);
        ArrangeSubOrchestrator(rig);
        rig.Writer.Executing = rig.Services.Hierarchy.StateDomains.Where(d => d.Name == "peer-records").ToList();
        return rig;
    }
}
