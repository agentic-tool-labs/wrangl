using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Activity;

/// <summary>
/// PostToolUse resolver for the peer report-back contract: a SendMessage whose target satisfies a pending obligation of this
/// session resolves it, and one that carries a request-file token is recorded as a dispatch of this session. Also acks a mailed
/// entry the message carried. Subagents and other tools are ignored. Never writes stdout.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static class PostToolUsePeerResolve
{
    public static HookOutcome Run(GateContext c)
    {
        var s = c.Services;
        if (c.ToolName() == "SendMessage")
        {
            ConfirmRelay(c);
        }

        if (s.Config.IsSubagent(c.Input) || c.ToolName() != "SendMessage")
        {
            return HookEmit.Silent;
        }

        var advisory = s.Advisory ?? throw new InvalidOperationException("the advisory services are not composed");
        var peers = s.Hierarchy.Peers;
        var core = s.Hierarchy.Core;
        var sessionId = c.Input.Text("session_id") ?? string.Empty;
        var toolInput = c.ToolInput();
        var to = toolInput.Get("to") is { IsText: true } t ? t.AsString() : string.Empty;
        var message = toolInput.Get("message") is { IsText: true } m ? m.AsString() : string.Empty;

        if (message.Length > 0)
        {
            try
            {
                advisory.MailboxHooks.AckFromSendMessage(sessionId, to, message, advisory.Callers.RequestContext(c.Cwd()));
            }
            catch (Exception ex)
            {
                c.LogError(ex);
            }
        }

        if (sessionId.Length == 0 || to.Length == 0)
        {
            return HookEmit.Silent;
        }

        var session = JsonValue.FromString(sessionId);
        var resolvedNow = new JsonArray();
        foreach (var rec in PeerLatestFor.Of(c).PendingFor(session))
        {
            // A relay obligation resolves only by the entry's ack, which the mirror above writes when the marker matches.
            if (rec.Member("relay") is { IsTruthy: true })
            {
                continue;
            }

            if (peers.TargetSatisfiesRecord(to, rec) && ReplySatisfiesMsg(s.Hierarchy, message, rec))
            {
                var resolved = new JsonObject();
                foreach (var key in rec.AsObject().Keys)
                {
                    resolved[key] = rec.AsObject()[key];
                }

                resolved["ts"] = Timestamps.ToIsoString(s.Clock.UtcNow);
                resolved["status"] = "resolved";
                peers.RecordPeerRow(resolved);
                resolvedNow.Add(rec);
            }
        }

        // The harness reports a cut delivery to the sender only later, by the msg_id the send returned; remember which obligations
        // this send resolved so that report can reopen them.
        if (c.Input.Member("tool_response")?.Member("msg_id") is { IsText: true } sentId && sentId.AsString().Length > 0
            && core.ExtractMsgToken(message) is { } sentToken && sentToken.EndsWith("--response.md", StringComparison.Ordinal))
        {
            peers.RecordPeerRow(new JsonObject
            {
                ["type"] = "sent",
                ["session_id"] = sessionId,
                ["msg_id"] = sentId,
                ["to"] = to,
                ["response"] = sentToken,
                ["resolved"] = resolvedNow,
                ["ts"] = Timestamps.ToIsoString(s.Clock.UtcNow),
            });
        }

        // A SendMessage is a peer-route dispatch iff it carries a request-file token: recorded under this session's own id,
        // whether or not the recipient ever receives or acknowledges it.
        var reqPath = core.ExtractMsgToken(message);
        if (reqPath is not null && reqPath.EndsWith("--request.md", StringComparison.Ordinal))
        {
            var meta = core.ParseMsgFilename(reqPath);
            if (meta is not null && meta.Type == "request")
            {
                peers.AppendDispatchRecord(session, meta.Id, meta.To, reqPath, peers.StripRef(to));
            }
        }

        return AutoTrack.AfterSend(c) ?? HookEmit.Silent;
    }

    /// <summary>
    /// The relay claim a mail entry's SendMessage took before it ran is confirmed (the message reached the inbox) or failed (it did
    /// not) by the tool result; a result that says neither moves nothing and the claim lease decides. Subagents share the session's
    /// claim, so they count.
    /// </summary>
    private static void ConfirmRelay(GateContext c)
    {
        if (c.Services.Advisory is not { } advisory)
        {
            return;
        }

        var toolInput = c.ToolInput();
        var message = toolInput.Get("message") is { IsText: true } m ? m.AsString() : string.Empty;
        if (message.Length == 0)
        {
            return;
        }

        try
        {
            var result = RelayResults.Of(c.Input.Member("tool_response"), out var reason);
            var to = toolInput.Get("to") is { IsText: true } t ? t.AsString() : string.Empty;
            var msgId = c.Input.Member("tool_response")?.Member("msg_id") is { IsText: true } id ? id.AsString() : null;
            advisory.MailboxHooks.ConfirmRelay(c.Input.Text("session_id"), to, message, result, reason, advisory.Callers.RequestContext(c.Cwd()), msgId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            c.LogError(ex);
        }
    }

    /// <summary>An obligation with <c>msg</c> set resolves only against a reply carrying the matching response pointer, file present.</summary>
    private static bool ReplySatisfiesMsg(Ah.Hierarchy.HierarchyServices hierarchy, string message, JsonValue rec)
    {
        if (rec.Member("msg") is not { IsTruthy: true } msg)
        {
            return true;
        }

        var meta = msg.IsText ? hierarchy.Core.ParseMsgFilename(msg.AsString()) : null;
        return hierarchy.Messages.HasResponseToken(message, meta is null ? JsonValue.Null : meta.Id);
    }
}
