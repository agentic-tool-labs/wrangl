using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Hooks.Tests;

/// <summary>Helpers kept from the gate tests deleted in ah-simplify S2, for the tests that still use them.</summary>
public sealed partial class HooksHostTests : IDisposable
{
    // Static because the rigs that make plugin roots are static; the tests of one class run one at a time, so Dispose takes what its test made.
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> PluginRoots = new();

    /// <summary>Removes the plugin roots made since the last test was disposed.</summary>
    public void Dispose()
    {
        while (PluginRoots.TryDequeue(out var root))
        {
            Ah.TestProcess.SandboxDirectory.Remove(root);
        }
    }

    /// <summary>A plugin root on disk, for the two command forms; removed when the test that made it is disposed.</summary>
    internal static string MakePluginRoot()
    {
        var root = Path.Combine("/private/tmp", "ahprobe.plugin." + Guid.NewGuid().ToString("N")[..8], "ah");
        PluginRoots.Enqueue(Path.GetDirectoryName(root)!);
        Directory.CreateDirectory(Path.Combine(root, "hooks"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        Directory.CreateDirectory(Path.Combine(root, ".claude-plugin"));
        foreach (var script in new[] { "roster.mjs", "msg.mjs", "ahd-ctl.mjs" })
        {
            File.WriteAllText(Path.Combine(root, "hooks", script), "x");
        }

        File.WriteAllText(Path.Combine(root, "bin", "ah"), "x");
        File.WriteAllText(Path.Combine(root, "bin", "ahd"), "x");
        File.WriteAllText(Path.Combine(root, ".claude-plugin", "plugin.json"), "{\"name\":\"agent-hierarchy\",\"version\":\"1\"}");
        return root;
    }

    private static string Event(string tool, string agentType, JsonObject toolInput, string? permissionMode = null)
    {
        var e = new JsonObject
        {
            ["hook_event_name"] = "PreToolUse",
            ["session_id"] = "sess-1",
            ["tool_name"] = tool,
            ["tool_input"] = toolInput,
        };
        if (agentType.Length > 0)
        {
            e["agent_type"] = agentType;
        }

        if (permissionMode is not null)
        {
            e["permission_mode"] = permissionMode;
        }

        return JsonText.Stringify(e, 0);
    }

    private static string BashAs(string agentType, string command) =>
        Event("Bash", agentType, new JsonObject { ["command"] = command });

    private static string BashAsSubagent(string agentType, string command)
    {
        var e = JsonText.Parse(BashAs(agentType, command)).AsObject();
        e["agent_id"] = "a-1";
        return JsonText.Stringify(e, 0);
    }

    private static bool IsDenied(HookOutcome o) =>
        !o.Stdout.IsEmpty && Json(o).Member("hookSpecificOutput")!.Value.Member("permissionDecision")!.Value.AsString() == "deny";

    private static string? DecisionOf(HookOutcome o) =>
        o.Stdout.IsEmpty ? null : Json(o).Member("hookSpecificOutput")!.Value.Member("permissionDecision")!.Value.AsString();

    private static string AllowHookAnswer(Rig rig, string command) =>
        DecisionOf(rig.Run("pretooluse-ah-cli", Event("Bash", "wrangl:orchestrator", new JsonObject { ["command"] = command }))) ?? "silent";
}
