using Ah.Core.Json;
using Ah.Roster;

namespace Ah.Hooks.Tests;

/// <summary>A session that orchestrates a team under a hold: the live registration the roster looks a session's teams up by, the team file and the hold file.</summary>
internal static class HoldFixture
{
    /// <summary>Every session id has a live registration at the harness pid, so the roster can name the teams the session orchestrates.</summary>
    public static SessionProofs Proofs { get; } = new(_ => null, _ => (HarnessSessions.Pid, HarnessSessions.Start));

    public static HooksHostTests.Rig Rig(Dictionary<string, string>? environment = null) =>
        new(environment, processTable: new HarnessSessions().Table, proofs: Proofs);

    public static void Orchestrates(HooksHostTests.Rig rig, string session, string team)
    {
        var dir = rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj");
        var path = rig.Services.Roster.TeamPath(dir, team);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var record = new JsonObject
        {
            ["version"] = 1,
            ["team_id"] = "t-" + team,
            ["orchestrator"] = new JsonObject { ["session_id"] = session },
            ["members"] = new JsonArray(),
        };
        File.WriteAllBytes(path, JsonText.ToUtf8(record, 0));
    }

    public static void Hold(HooksHostTests.Rig rig, string team, string kind = "quit")
    {
        var dir = rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj");
        var path = rig.Services.Roster.HoldPath(dir, team);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"v\":1,\"kind\":\"" + kind + "\",\"phase\":\"held\",\"since\":\"2026-10-24T00:00:00Z\"}");
    }
}
