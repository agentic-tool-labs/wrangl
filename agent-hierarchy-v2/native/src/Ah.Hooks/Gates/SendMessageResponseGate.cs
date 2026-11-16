using System.Text;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Gates;

/// <summary>
/// A directly-attributed role that owes a response to a message-file request must deliver its answer as a response FILE,
/// named with <c>[hierarchy-msg &lt;path&gt;]</c> as the first line of the SendMessage reporting it. A message without a
/// token is nudged only when it could be the report delivered inline: addressed to a party owed a report, and report-sized.
/// A recorder, never a gate: every send goes through; a miss is recorded in gates.jsonl and returned as context, once per request.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static class SendMessageResponseGate
{
    private const int EchoCap = 2000;

    /// <summary>The gate is advisory: a message store that cannot be read is logged and lets the send through.</summary>
    public static HookOutcome Run(GateContext c)
    {
        try
        {
            return Decide(c);
        }
        catch (StoreUnavailableException error)
        {
            c.LogError(error);
            return HookEmit.Silent;
        }
    }

    private static HookOutcome Decide(GateContext c)
    {
        if (c.ToolName() != "SendMessage")
        {
            return HookEmit.Silent;
        }

        var s = c.Services;
        var config = s.Config;
        if (config.IsSubagent(c.Input))
        {
            return HookEmit.Silent;
        }

        var caller = config.ResolveHierarchyRole(c.Input);
        if (!caller.Direct || caller.Role is not { } callerRole
            || config.ClassProp(callerRole, caller.Resolved, "owesResponse") is not { IsBool: true } owes || !owes.AsBool())
        {
            return HookEmit.Silent;
        }

        var cwd = c.Cwd();
        var sessionKey = c.SessionKey();
        var resolved = config.ResolveConfig(cwd, new ResolveOptions(SessionId: sessionKey != "__nosession__" ? sessionKey : null));
        if (resolved.Get("enabled") is not { IsTruthy: true } || (resolved.Get("msgs") is { IsText: true } msgs && msgs.AsString() == "off"))
        {
            return HookEmit.Silent;
        }

        using var decisions = c.Services.Hierarchy.Core.DecisionReads();
        var qualifying = PeerLatestFor.Of(c).PendingFor(sessionKey)
            .Where(r => r.IsMap && r.Member("msg") is { IsText: true } m && m.AsString().Length > 0)
            .OrderBy(r => TsText(r), StringComparer.Ordinal)
            .ToList();
        if (qualifying.Count == 0)
        {
            return HookEmit.Silent;
        }

        var dir = config.HierarchyDir(cwd);
        var toolInput = c.ToolInput();
        var text = toolInput.Get("message") is { IsText: true } message ? message.AsString() : string.Empty;
        var token = s.Hierarchy.Core.ExtractMsgToken(text, dir);
        var messages = s.Hierarchy.Messages;

        if (token is not null)
        {
            foreach (var rec in qualifying)
            {
                var recMsg = rec.Text("msg")!;
                var meta = s.Hierarchy.Core.ParseMsgFilename(recMsg);
                var check = messages.ValidateResponseToken(text, dir, callerRole, GateInput.JsonOrNull(meta?.Id), recMsg);
                if (check.Member("ok") is not { IsTruthy: true })
                {
                    continue;
                }

                if (check.Text("path") is { Length: > 0 } filePath && check.Member("fm")?.Text("id") is { Length: > 0 } fileId
                    && messages.ReportStatus(filePath, fileId) == "no-report")
                {
                    return Recorded.Note(
                        c,
                        "send-blank-response",
                        $"ah: response {fileId} is still the template — fill it with `ah msg fill --id {fileId} --from {callerRole} --body-file -`, then send again.");
                }

                var echo = EchoBytesAfterPointer(text, meta?.Id);
                return echo <= EchoCap
                    ? HookEmit.Silent
                    : SoftGate.Bounce(c, "inline-report", $"you wrote {echo} B after the pointer line (cap {EchoCap}); the response carries the report.", ReplyWithFile, retryPasses: true);
            }

            // Present but satisfies no open request: re-check the record the token's filename names (else the oldest).
            var tokenMeta = s.Hierarchy.Core.ParseMsgFilename(token);
            var target = tokenMeta is null
                ? qualifying[0]
                : qualifying.FirstOrDefault(r => s.Hierarchy.Core.ParseMsgFilename(r.Text("msg")!)?.Id == tokenMeta.Id, qualifying[0]);
            var targetMsg = target.Text("msg")!;
            var targetMeta = s.Hierarchy.Core.ParseMsgFilename(targetMsg);
            var recheck = messages.ValidateResponseToken(text, dir, callerRole, GateInput.JsonOrNull(targetMeta?.Id), targetMsg);
            var why = recheck.Member("why") is { IsText: true } w ? w.AsString() : string.Empty;
            return SoftGate.Bounce(
                c,
                "inline-report",
                $"the [hierarchy-msg] pointer this SendMessage carries does not satisfy an open request ({why}).",
                $"point it at your own response for request {targetMeta?.Id ?? "<id>"}, or drop the token if this is a check-in.",
                retryPasses: false);
        }

        // A reply auto-tracking files as the response is the report; the file carries it.
        if (AutoTrack.Classify(c, out _) is { RequestPath: not null })
        {
            return HookEmit.Silent;
        }

        var to = toolInput.Get("to") is { IsText: true } t ? t.AsString() : string.Empty;
        var owedHere = qualifying.Where(rec => s.Hierarchy.Peers.TargetSatisfiesRecord(to, rec)).ToList();
        if (owedHere.Count == 0)
        {
            return HookEmit.Silent;
        }

        if (Encoding.UTF8.GetByteCount(JsText.Trim(text)) <= EchoCap)
        {
            return HookEmit.Silent;
        }

        return SoftGate.Bounce(
            c,
            "inline-report",
            "you hold an unanswered message-file request, and this report-sized reply is inline prose.",
            ReplyWithFile + "\n" + OpenRequests(c, owedHere, callerRole),
            retryPasses: true);
    }

    private const string ReplyWithFile = "reply with your response: `[hierarchy-msg <id> response]` + one status bullet.";

    /// <summary><c>String(a.ts)</c>: the record's timestamp as JavaScript would compare it.</summary>
    private static string TsText(JsonValue record) => record.Member("ts") is { } ts
        ? ts.Kind switch
        {
            JsonKind.Text => ts.AsString(),
            JsonKind.Null => "null",
            _ => JsonText.Stringify(ts, 0),
        }
        : "undefined";

    /// <summary>The bytes of what follows the pointer line, counted after it so a long absolute path never trips the cap.</summary>
    private static int EchoBytesAfterPointer(string text, string? id)
    {
        var lines = text.Split('\n');
        var at = Array.FindIndex(lines, l => l.Contains("[hierarchy-msg", StringComparison.Ordinal) && (string.IsNullOrEmpty(id) || l.Contains(id, StringComparison.Ordinal)));
        if (at < 0)
        {
            return 0;
        }

        return Encoding.UTF8.GetByteCount(JsText.Trim(string.Join("\n", lines.Skip(at + 1))));
    }

    private static string OpenRequests(GateContext c, IReadOnlyList<JsonValue> qualifying, string role)
    {
        var s = c.Services;
        var lines = new List<string> { "Open request(s) you have not answered:" };
        foreach (var rec in qualifying)
        {
            var msg = rec.Text("msg")!;
            var id = s.Hierarchy.Core.ParseMsgFilename(msg)?.Id ?? "<id>";
            var request = s.Hierarchy.Core.ReadMsgFile(msg);
            var from = request.Member("fm")?.Member("from") is { IsText: true } f && f.AsString().Length > 0 ? f.AsString() : "orchestrator";
            lines.Add($"- {id} (from {from}): File it with `ah msg fill --id {id} --from {role} --body-file -`, then send [hierarchy-msg {id} response] + the [1] status bullet.");
        }

        return string.Join("\n", lines);
    }
}
