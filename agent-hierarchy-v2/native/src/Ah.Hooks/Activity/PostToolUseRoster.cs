using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hooks.Gates;
using Ah.Hooks.Lifecycle;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Activity;

/// <summary>
/// PostToolUse roster writer (orchestrator side). A <c>ListAgents</c> response line under <c>Peer sessions</c> whose name is a
/// configured peer target, or carries a role token, is recorded as <c>seen</c>; a <c>SendMessage</c> carrying the brief
/// sentinel records <c>briefed</c>. Subagents write nothing. Never writes stdout.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static partial class PostToolUseRoster
{
    private const string Ws = JsText.SpaceClass;

    [GeneratedRegex("^[" + Ws + "]*([^\\n\\r\\u2028\\u2029]+?) \\[([0-9a-f]+)\\][" + Ws + "]+·[" + Ws + "]+([A-Za-z0-9_]+)[" + Ws + "]+·[" + Ws + "]+(idle|busy|shell)[" + Ws + "]+·", RegexOptions.CultureInvariant)]
    private static partial Regex LineRegex();

    public static HookOutcome Run(GateContext c)
    {
        var s = c.Services;
        if (s.Config.IsSubagent(c.Input))
        {
            return HookEmit.Silent;
        }

        var cwd = c.Cwd();
        var resolved = s.Config.ResolveConfig(cwd);
        var team = resolved.Get("team") is { IsText: true } t ? t.AsString() : null;
        var repoBasename = s.Config.TeamPrefix(cwd, team);
        var dir = s.Config.HierarchyDir(cwd);
        var toolInput = c.ToolInput();
        var hook = new StateCallOptions(Hook: true);

        if (c.ToolName() == "ListAgents")
        {
            var lines = ResponseText(c.Input.Member("tool_response")).Split('\n');
            var headerAt = Array.FindIndex(lines, l => ContainsAsciiFolded(l, "peer sessions"));
            List<StopOrchestratorLiveness.Outstanding>? open = null;
            foreach (var line in headerAt >= 0 ? lines.Skip(headerAt + 1) : lines)
            {
                var m = LineRegex().Match(line);
                if (!m.Success)
                {
                    continue;
                }

                var name = JsText.Trim(m.Groups[1].Value);
                if (m.Groups[4].Value is "busy" or "shell")
                {
                    open ??= OpenDispatches(c, cwd);
                    RecordChecked(c, open, name);
                }

                var role = s.Hierarchy.Roster.RoleForAnyPeerName(dir, name, resolved, repoBasename);
                if (role is null)
                {
                    continue;
                }

                var seen = SeenRow(name, m.Groups[2].Value, role, m.Groups[4].Value, TeamTag(c, dir, name));
                s.Hierarchy.StateCalls.AppendRosterRecord(dir, seen, hook);
            }
        }
        else if (c.ToolName() == "SendMessage")
        {
            var message = toolInput.Get("message") is { IsText: true } mt ? mt.AsString() : string.Empty;
            var sentinel = s.Hierarchy.Peers.ParseSentinel(message);
            var to = toolInput.Get("to") is { IsText: true } tt ? s.Hierarchy.Peers.StripRef(JsText.Trim(tt.AsString())) : string.Empty;
            if (sentinel.IsTruthy && to.Length > 0)
            {
                var role = s.Hierarchy.Roster.RoleForAnyPeerName(dir, to, resolved, repoBasename);
                var task = sentinel.Member("task") is { IsTruthy: true } task0 ? task0 : JsonValue.Null;
                s.Hierarchy.StateCalls.AppendRosterRecord(
                    dir,
                    new JsonObject
                    {
                        ["status"] = "briefed",
                        ["name"] = to,
                        ["role"] = role is null ? JsonValue.Null : role,
                        ["task"] = task,
                        ["team"] = TeamTag(c, dir, to),
                    },
                    hook);
            }
        }

        return HookEmit.Silent;
    }

    /// <summary>
    /// The roster row of a listed peer. A peer running a background shell is not idle, so its row says busy as well as shell: the
    /// readers that only know busy agree with the one that knows the difference.
    /// </summary>
    internal static JsonObject SeenRow(string name, string reference, string role, string tag, JsonValue team)
    {
        var seen = new JsonObject
        {
            ["status"] = "seen",
            ["name"] = name,
            ["ref"] = reference,
            ["role"] = role,
            ["busy"] = tag is "busy" or "shell",
            ["team"] = team,
        };
        if (tag == "shell")
        {
            seen["shell"] = true;
        }

        return seen;
    }

    private static List<StopOrchestratorLiveness.Outstanding> OpenDispatches(GateContext c, string cwd)
    {
        var s = c.Services;
        var sessionId = c.Input.Text("session_id") ?? string.Empty;
        if (sessionId.Length == 0)
        {
            return [];
        }

        var resolved = s.Config.ResolveConfig(cwd, new ResolveOptions(SessionId: sessionId));
        return StopOrchestratorLiveness.OpenOf(s, cwd, resolved, sessionId, s.Clock.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// A peer shown busy, or running a shell command, is a check-in for each open dispatch of this session addressed to it, due or not
    /// (b541): the listed name is the name the watch lists the dispatch under (<c>to_name</c> first), its <c>to_addr</c> local part, or
    /// its recipient name when it has no address. An idle peer records nothing.
    /// </summary>
    private static void RecordChecked(GateContext c, List<StopOrchestratorLiveness.Outstanding> open, string name)
    {
        var sessionId = c.Input.Text("session_id") ?? string.Empty;
        foreach (var o in open)
        {
            var addressee = o.ToAddr is { Length: > 0 } addr ? addr.Split('@')[0] : o.To;
            if (addressee != name && o.ToName != name)
            {
                continue;
            }

            c.Services.Hierarchy.Roster.AppendGateRouted(
                o.Dir,
                new JsonObject { ["type"] = "liveness-checked", ["session_id"] = sessionId, ["request_id"] = o.Id, ["peer"] = name, ["busy"] = true });
        }
    }

    /// <summary>The team tag of a roster record: a fresh name-membership lookup, since this hook has no session id to resolve the team from.</summary>
    private static JsonValue TeamTag(GateContext c, string dir, string name)
    {
        var membership = c.Services.Roster.ResolveMemberTeam(dir, name);
        return membership.Found && membership.Team is { } team ? team : JsonValue.Null;
    }

    private static bool ContainsAsciiFolded(string line, string lowerNeedle)
    {
        for (var i = 0; i + lowerNeedle.Length <= line.Length; i++)
        {
            var hit = true;
            for (var j = 0; j < lowerNeedle.Length && hit; j++)
            {
                var ch = line[i + j];
                hit = (ch is >= 'A' and <= 'Z' ? (char)(ch + 32) : ch) == lowerNeedle[j];
            }

            if (hit)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The text of a tool response as the hook reads it: a string, <c>output</c>, <c>text</c>, the <c>text</c> parts of <c>content</c>, or its JSON.</summary>
    internal static string ResponseText(JsonValue? response)
    {
        if (response is not { } resp || resp.IsNull)
        {
            return string.Empty;
        }

        if (resp.IsText)
        {
            return resp.AsString();
        }

        if (!resp.IsMap && !resp.IsArray)
        {
            return string.Empty;
        }

        if (resp.IsMap)
        {
            if (resp.Member("output") is { IsText: true } output)
            {
                return output.AsString();
            }

            if (resp.Member("text") is { IsText: true } text)
            {
                return text.AsString();
            }

            if (resp.Member("content") is { IsArray: true } content)
            {
                return string.Join('\n', content.AsArray().Select(PartText));
            }

            return JsonText.Stringify(resp, 0);
        }

        return string.Join('\n', resp.AsArray().Select(p => p.IsText ? p.AsString() : PartText(p)));
    }

    private static string PartText(JsonValue part) => part.Member("text") is { IsText: true } t ? t.AsString() : string.Empty;
}
