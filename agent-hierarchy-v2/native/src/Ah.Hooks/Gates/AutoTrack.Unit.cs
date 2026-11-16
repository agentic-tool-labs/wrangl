using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Gates;

/// <summary>The PreToolUse unit: a session's SendMessage goes through auto-track. Subagents, peer-brief sentinels, a disabled hierarchy and <c>msgs: "off"</c> are left alone.</summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static partial class AutoTrack
{
    public static HookOutcome Run(GateContext c)
    {
        var s = c.Services;
        if (c.ToolName() != "SendMessage" || s.Config.IsSubagent(c.Input))
        {
            return HookEmit.Silent;
        }

        var text = c.ToolInput().Get("message") is { IsText: true } m ? m.AsString() : string.Empty;
        if (s.Hierarchy.Peers.ParseSentinel(text) is { IsTruthy: true })
        {
            return HookEmit.Silent;
        }

        var sessionKey = c.SessionKey();
        var resolved = s.Config.ResolveConfig(c.Cwd(), new ResolveOptions(SessionId: sessionKey != "__nosession__" ? sessionKey : null));
        if (resolved.Get("enabled") is not { IsTruthy: true } || (resolved.Get("msgs") is { IsText: true } msgs && msgs.AsString() == "off"))
        {
            return HookEmit.Silent;
        }

        return BareSend(c, resolved);
    }

    /// <summary>A bare send is tracked by the hook; when the tracking could not be completed and the send names a brief or a report, it is recorded and the agent gets the steps to file it.</summary>
    private static HookOutcome BareSend(GateContext c, JsonObject resolved)
    {
        var tracking = PreSend(c, RewriteOn(resolved), out var tracked);
        if (tracked is not null)
        {
            return tracked;
        }

        if (tracking != Tracking.Failed || BareNamedBy(c) is not { } named)
        {
            return HookEmit.Silent;
        }

        var handle = MsgPointer.Handle(named.Path) ?? named.Path;
        var refId = MsgPointer.Id(named.Path) ?? named.Path;
        var text = named.Reason == "bare-brief"
            ? $"ah: this SendMessage carries a brief ({handle}) without its mailbox entry, so ahd cannot time it. File it with: ah msg send {named.Recipient} --ref {refId} --kind brief --cwd {c.Cwd()}"
            : $"ah: this SendMessage carries your report ({handle}) without its mailbox entry, so your Orchestrator's clock cannot see it landed. File it with: ah msg send {named.Recipient} --kind report --ref {refId} --cwd {c.Cwd()}";
        return Recorded.Note(c, named.Reason, text);
    }

    /// <summary>The chain role of the team member a SendMessage is addressed to, or null when the recipient is no member of this session's team.</summary>
    internal static string? TeamMemberRole(GateContext c, JsonObject toolInput, JsonObject resolved)
    {
        var s = c.Services;
        var config = s.Config;
        var cwd = c.Cwd();
        var to = toolInput.Get("to") is { IsText: true } t ? s.Hierarchy.Peers.StripRef(JsText.Trim(t.AsString())) : string.Empty;
        // A session that owns several live teams matches the name against each team's prefix.
        var owned = resolved.Get("ownedTeams") is { IsArray: true } list && list.AsArray().Count > 1 ? list.AsArray().ToList() : null;
        var prefixes = owned is not null
            ? owned.Select(team => config.TeamPrefix(cwd, team.IsText ? team.AsString() : null)).ToList()
            : [config.TeamPrefix(cwd, resolved.Get("team") is { IsText: true } own ? own.AsString() : null)];
        return config.ChainRoles(resolved).FirstOrDefault(r => prefixes.Any(prefix =>
            config.ResolvedPeerTargets(r, resolved.Get("roles")?.Member(r), prefix).Any(name => name.IsText && name.AsString() == to)));
    }

    /// <summary>A bare send shorter than this is taken for an acknowledgement or a chat line ("merged; stand by", "thanks"), not a task.</summary>
    internal const int BareTaskLength = 200;

    /// <summary>
    /// A task-sized SendMessage to a member of the session's own team that carries no <c>[hierarchy-msg ...]</c> pointer gets one context
    /// line asking for the brief to travel as a message file, so ahd's views, liveness and round counts see it. Once per turn (since
    /// the session's latest turn marker); never a denial. Null when no nudge is due.
    /// </summary>
    internal static HookOutcome? BareSendNudge(GateContext c, JsonObject toolInput, string text, JsonObject resolved)
    {
        var s = c.Services;
        var sessionId = c.Input.Text("session_id") ?? string.Empty;
        if (sessionId.Length == 0 || text.Contains("[hierarchy-msg", StringComparison.Ordinal) || text.Trim().Length < BareTaskLength
            || TeamMemberRole(c, toolInput, resolved) is null)
        {
            return null;
        }

        var turnAt = s.Hierarchy.Peers.LatestTurnMarker(sessionId) is { IsMap: true } turn && turn.Text("ts") is { } ts && DateTimeOffset.TryParse(ts, out var parsed) ? parsed : (DateTimeOffset?)null;
        var already = s.Hierarchy.Core.SessionRows(sessionId, "bare-send-nudge").Any(r =>
            r.IsMap && r.Text("type") == "bare-send-nudge" && r.Text("session_id") == sessionId
            && turnAt is not null && r.Text("ts") is { } at && DateTimeOffset.TryParse(at, out var nudged) && nudged >= turnAt);
        if (already)
        {
            return null;
        }

        try
        {
            s.Hierarchy.Peers.RecordPeerRow(new JsonObject
            {
                ["type"] = "bare-send-nudge",
                ["session_id"] = sessionId,
                ["ts"] = Timestamps.ToIsoString(s.Clock.UtcNow),
            });
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            // the nudge is advice: without its row it may repeat, and the send still goes through
            c.LogError(e);
        }

        return HookEmit.AdditionalContext(
            "PostToolUse",
            $"ah: auto-filing failed for this send, so it is untracked. Brief via {s.Config.CliCommand(CliTool.Msg, alwaysQuote: true)} new so the exchange is tracked (ahd, liveness, round counts). This send went through.");
    }
}
