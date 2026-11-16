using Ah.Abstractions;
using Ah.Core.Text;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Activity;

/// <summary>
/// Keeps a stream's tab label current from inside each stream member's own session: UserPromptSubmit renders the member as working,
/// Stop as idle and a permission prompt (Notification) as blocked. The render runs through <c>ah roster stream-label</c>, which stays
/// the one place the terminal multiplexer is driven. A session that is not a roster-spawned herdr peer of an open stream, and any
/// subagent, does nothing. It never writes stdout and never blocks; a render that fails is a hook-errors row.
/// </summary>
[HookIdentity(HookIdentityKind.None)]
internal static class StreamLabel
{
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(5);

    public static HookOutcome Run(GateContext c)
    {
        var s = c.Services;
        var pane = Ah.Core.Mux.HerdrFlavour.SessionPaneId(s.Environment.GetVariable);
        var teamFile = s.Environment.GetVariable("WRANGL_TEAM_FILE");
        if (string.IsNullOrEmpty(pane) || string.IsNullOrEmpty(teamFile))
        {
            return HookEmit.Silent;
        }

        var state = c.Input.Text("hook_event_name") switch
        {
            "UserPromptSubmit" => "working",
            "Stop" => "idle",
            "Notification" => "blocked",
            _ => null,
        };
        if (state is null || s.Config.IsSubagent(c.Input))
        {
            return HookEmit.Silent;
        }

        if (s.Roster.ReadTeamFileAt(teamFile) is not { IsMap: true } document)
        {
            throw new InvalidDataException("the team file is absent, not a JSON object, or has no members list");
        }

        var team = document.AsObject();
        if (team.Get("members") is not { IsArray: true } members)
        {
            throw new InvalidDataException("the team file has no members list");
        }

        string? stream = null;
        foreach (var member in members.AsArray())
        {
            if (member is { IsMap: true } && member.AsObject() is var m && m.Get("transport_id") is { IsText: true } id && id.AsString() == pane)
            {
                stream = m.Get("stream") is { IsText: true } named ? named.AsString() : null;
                break;
            }
        }

        if (string.IsNullOrEmpty(stream)
            || team.Get("streams") is not { IsMap: true } streams
            || streams.AsObject().Get(stream) is not { IsMap: true } record
            || record.AsObject().Get("state") is not { IsText: true } open
            || open.AsString() != "open")
        {
            return HookEmit.Silent;
        }

        if (team.Get("expected_root") is not { IsText: true } root)
        {
            throw new InvalidDataException("the team file names no expected root");
        }

        var advisory = s.Advisory ?? throw new InvalidOperationException("the advisory services are not composed");
        var pluginRoot = s.Environment.Plugin?.Root ?? throw new InvalidOperationException("the plugin root is not known");
        var argv = new List<string>
        {
            NodePath.Join(pluginRoot, "bin", "ah"), "roster", "stream-label", stream,
            "--self-pane", pane, "--self-state", state, "--cwd", root.AsString(),
        };
        if (advisory.Mailbox.IsNamedTeamPath(teamFile))
        {
            var name = NodePath.Basename(teamFile);
            argv.Add("--team");
            argv.Add(name.EndsWith(".json", StringComparison.Ordinal) ? name[..^".json".Length] : name);
        }

        var result = advisory.Runner.Run(new ProcessRequest(argv, Timeout: RenderTimeout));
        if (result.ExitCode != 0 || result.TimedOut)
        {
            throw new IOException("Command failed: " + string.Join(' ', argv));
        }

        return HookEmit.Silent;
    }
}
