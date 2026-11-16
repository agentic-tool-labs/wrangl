using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Activity;

/// <summary>
/// SubagentStop nudge for the message-file protocol: a chain-role subagent briefed with <c>[hierarchy-msg &lt;request path&gt;]</c>
/// owes a response file, and its final text must open with <c>[hierarchy-msg &lt;response path&gt;]</c>. When it does not, or
/// echoes more than the cap after the pointer, the stop is blocked once per subagent; the second stop always passes. Any failure
/// allows.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static class SubagentStopMsgNudge
{
    private const int EchoCap = 2000;

    public static HookOutcome Run(GateContext c)
    {
        var s = c.Services;
        var agentId = c.Input.Text("agent_id") ?? string.Empty;
        var cwd = c.Cwd();
        if (agentId.Length == 0
            || c.Input.Text("session_id") is { Length: > 0 } sessionId && Ah.Roster.RosterModule.ChainDownActive(s.Hierarchy.Core.PeerRecordsShared(), sessionId))
        {
            return HookEmit.Silent;
        }

        var lookup = s.Config.LookupRole(c.Input.Member("agent_type"), cwd);
        var role = lookup.Get("role") is { IsText: true } r && r.AsString().Length > 0 ? r.AsString() : null;
        var registry = lookup.Get("resolved") is { IsMap: true } reg ? reg.AsObject() : null;
        if (role is null || s.Config.ClassProp(role, registry, "chain") is not { IsBool: true } chain || !chain.AsBool())
        {
            return HookEmit.Silent;
        }

        var resolved = s.Config.ResolveConfig(cwd);
        if (resolved.Get("enabled") is not { IsTruthy: true } || resolved.Get("msgs") is { IsText: true } msgs && msgs.AsString() == "off")
        {
            return HookEmit.Silent;
        }

        var dir = s.Config.HierarchyDir(cwd);
        var agentTranscript = c.Input.Text("agent_transcript_path") ?? string.Empty;
        if (agentTranscript.Length == 0 && c.Input.Text("transcript_path") is { } transcriptPath && c.Input.Text("session_id") is { } sessionIdText)
        {
            agentTranscript = NodePath.Join(NodePath.Dirname(transcriptPath), sessionIdText, "subagents", $"agent-{agentId}.jsonl");
        }

        var (firstUser, lastAssistant) = ScanTranscript(s, agentTranscript);
        var requestPath = s.Hierarchy.Core.ExtractMsgToken(firstUser);
        var meta = requestPath is not null ? s.Hierarchy.Core.ParseMsgFilename(requestPath) : null;
        if (meta is null || meta.Type != "request")
        {
            return HookEmit.Silent;
        }

        var last = c.Input.Text("last_assistant_message") ?? lastAssistant;
        var sessionValue = c.Input.Member("session_id") is { IsTruthy: true } sv ? sv : JsonValue.Null;
        var gates = s.Hierarchy.Roster;
        if (s.Hierarchy.Messages.HasResponseToken(last, meta.Id))
        {
            var echo = EchoBytesAfterPointer(last, meta.Id);
            if (echo <= EchoCap)
            {
                return HookEmit.Silent;
            }

            // Bounded the same way the missing-response nudge is: one block per agent. Its own record type, so an over-long
            // report does not spend the missing-response slot.
            if (gates.HasGate(dir, r2 => IsGate(r2, "echo-cap", agentId)))
            {
                return HookEmit.Silent;
            }

            s.Hierarchy.StateCalls.AppendGate(
                dir,
                new JsonObject { ["type"] = "echo-cap", ["agent_id"] = agentId, ["session_id"] = sessionValue, ["id"] = meta.Id, ["bytes"] = echo });
            return HookEmit.Block(Directive(s).StopFinalTooLong(meta.Id, echo, EchoCap));
        }

        // A response file that already holds the report makes a missing token in the final text cosmetic.
        var planned = s.Hierarchy.Messages.ResponsePlan(requestPath!);
        if (planned.IsTruthy && planned.Member("path") is { IsText: true } plannedPath && plannedPath.AsString().Length > 0
            && s.Hierarchy.Core.MsgExists(plannedPath.AsString()) && s.Hierarchy.Messages.ResponseAuthored(plannedPath.AsString()))
        {
            return HookEmit.Silent;
        }

        // The one-shot-per-agent bound stays; the second stop is a silent give-up and is recorded as unmet.
        if (gates.HasGate(dir, r2 => IsGate(r2, "nudge", agentId)))
        {
            s.Hierarchy.StateCalls.AppendGate(dir, new JsonObject { ["type"] = "nudge-unmet", ["agent_id"] = agentId, ["id"] = meta.Id });
            return HookEmit.Silent;
        }

        s.Hierarchy.StateCalls.AppendGate(
            dir,
            new JsonObject { ["type"] = "nudge", ["agent_id"] = agentId, ["session_id"] = sessionValue, ["id"] = meta.Id });
        var options = new JsonObject { ["id"] = meta.Id };
        if (role is not null)
        {
            options["role"] = role;
        }

        options["req"] = requestPath!;
        return HookEmit.Block(Directive(s).StopWriteResponse(meta.Id, role) + "\n- or write it as a file with " + s.Config.ResponseCommand(options));
    }

    private static Ah.Config.IDirectiveText Directive(GateServices s) =>
        (s.Advisory ?? throw new InvalidOperationException("the advisory services are not composed")).Directive;

    private static bool IsGate(JsonValue record, string type, string agentId) =>
        record.Member("type") is { IsText: true } t && t.AsString() == type
        && record.Member("agent_id") is { IsText: true } a && a.AsString() == agentId;

    private static int EchoBytesAfterPointer(string text, string id)
    {
        var lines = text.Split('\n');
        var i = Array.FindIndex(lines, l => l.Contains("[hierarchy-msg", StringComparison.Ordinal) && l.Contains(id, StringComparison.Ordinal));
        if (i < 0)
        {
            return 0;
        }

        return Encoding.UTF8.GetByteCount(JsText.Trim(string.Join('\n', lines.Skip(i + 1))));
    }

    private static string TextOf(JsonValue? message)
    {
        if (message is not { } m)
        {
            return string.Empty;
        }

        if (m.IsText)
        {
            return m.AsString();
        }

        if (m.IsArray)
        {
            return string.Join('\n', m.AsArray().Select(p => p.Member("text") is { IsText: true } t ? t.AsString() : string.Empty));
        }

        return string.Empty;
    }

    /// <summary>The first user text and the last non-blank assistant text of a JSONL transcript; either may be empty.</summary>
    private static (string FirstUser, string LastAssistant) ScanTranscript(GateServices s, string path)
    {
        var firstUser = string.Empty;
        var lastAssistant = string.Empty;
        if (path.Length == 0 || !s.Files.Exists(path))
        {
            return (firstUser, lastAssistant);
        }

        string raw;
        try
        {
            raw = s.Files.ReadText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (firstUser, lastAssistant);
        }

        foreach (var line in raw.Split('\n'))
        {
            if (JsText.Trim(line).Length == 0)
            {
                continue;
            }

            JsonValue rec;
            try
            {
                rec = JsonText.Parse(line);
            }
            catch (JsonParseException)
            {
                continue;
            }

            if (rec.Member("message") is not { } msg || (!msg.IsMap && !msg.IsArray) || !msg.IsTruthy)
            {
                continue;
            }

            var type = rec.Member("type") is { IsTruthy: true } tt ? tt : msg.Member("role");
            var role = type is { IsText: true } rt ? rt.AsString() : null;
            if (role == "user" && firstUser.Length == 0)
            {
                firstUser = TextOf(msg.Member("content"));
            }

            if (role == "assistant")
            {
                var text = TextOf(msg.Member("content"));
                if (JsText.Trim(text).Length > 0)
                {
                    lastAssistant = text;
                }
            }
        }

        return (firstUser, lastAssistant);
    }
}
