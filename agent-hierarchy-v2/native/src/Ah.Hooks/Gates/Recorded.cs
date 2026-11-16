using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Gates;

/// <summary>
/// What a dispatch or conversation recorder says about a call it would once have held. The call always proceeds.
/// </summary>
internal static class Recorded
{
    /// <summary>One gates row (<paramref name="type"/>, the session, the tool) for status and the UI, and the advice as context for the agent.</summary>
    public static HookOutcome Note(GateContext c, string type, string advice)
    {
        try
        {
            c.Services.Hierarchy.Roster.AppendGateRouted(
                c.Services.Config.HierarchyDir(c.Cwd()),
                new JsonObject { ["type"] = type, ["session_id"] = c.SessionKey(), ["tool"] = c.ToolName() ?? string.Empty });
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            c.LogError(e);
        }

        return HookEmit.Combined(null, null, advice, null);
    }
}
