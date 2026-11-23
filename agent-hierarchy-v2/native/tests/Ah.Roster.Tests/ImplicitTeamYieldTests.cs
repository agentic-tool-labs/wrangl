using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Roster.Tests;

/// <summary>ah2-470 D1: an empty implicit team is not owned while its session owns another team, and is when it is the only one.</summary>
public sealed class ImplicitTeamYieldTests
{
    [Fact]
    public void AnEmptyImplicitTeamYieldsToANamedTeamAndStaysWhenAlone()
    {
        var sandbox = UnitSandbox.Create();
        var dir = Path.Combine(sandbox.Root, ".claude", "ah2");
        Directory.CreateDirectory(Path.Combine(dir, "teams"));
        var env = new FakeEnvironment(new Dictionary<string, string> { ["HOME"] = sandbox.Home });
        var io = PlatformModule.CreateIo(null);
        IRosterCore roster = null!;
        var config = ConfigModule.CreateCore(io.Files, env, () => roster, new ProcessFacts(sandbox.Root, 1));
        var table = PlatformModule.CreateProcessTable(PlatformModule.CreateProcessRunner());
        roster = RosterModule.CreateCore(io.Files, env, config, io.Processes, table);

        var pid = Environment.ProcessId;
        var owner = $"\"orchestrator\":{{\"pid\":{pid},\"pid_start\":\"{table.StartTime(pid)}\"}}";
        File.WriteAllText(Path.Combine(dir, "teams", "snackvote.json"), $"{{\"implicit\":true,\"members\":[],{owner}}}\n");
        var pantry = Path.Combine(dir, "teams", "pantry.json");
        File.WriteAllText(pantry, $"{{\"members\":[{{\"role\":\"implementor\",\"name\":\"p-implementor\"}}],{owner}}}\n");
        var invoker = new TeamInvoker(pid, null);

        Assert.Equal(["pantry"], roster.OwnedTeams(dir, invoker));
        Assert.Equal(["pantry", "snackvote"], roster.AllOwnedTeams(dir, invoker));

        File.Delete(pantry);
        Assert.Equal(["snackvote"], roster.OwnedTeams(dir, invoker));

        // the negatives: a member, a stream, or no implicit flag and the team never yields
        Assert.False(RosterModule.ImplicitTeamYields(JsonText.Parse("{\"implicit\":true,\"members\":[{\"name\":\"m\"}]}")));
        Assert.False(RosterModule.ImplicitTeamYields(JsonText.Parse("{\"implicit\":true,\"members\":[],\"streams\":{\"api\":{}}}")));
        Assert.False(RosterModule.ImplicitTeamYields(JsonText.Parse("{\"members\":[]}")));
    }
}
