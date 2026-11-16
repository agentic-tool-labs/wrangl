using System.Globalization;
using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;
using Ah.Mesh;
using Ah.Roster;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// PermissionDenied: Claude Code's auto-mode classifier refused a tool call. For a member or sub-orchestrator this raises one
/// <c>classifier-denied</c> event to its team's Orchestrator box; for the top Orchestrator's own session it only records the denial.
/// Every denial is recorded as a <c>watch-event</c> peer row, which is also the state the alarm keeps: a <c>tool_use_id</c> already on
/// record is never raised again, and a member's denials for one reason key are mailed once per <see cref="ReasonWindow"/>, the rest held
/// and counted into the next mail. Every denial is also written to hook-errors. Never blocks; any failure is silent.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static partial class PermissionDeniedAlarm
{
    internal const string Kind = "CLASSIFIER-DENIED";

    internal const string EventName = "step-needs-user";

    internal const string UnknownLabel = "unknown";

    /// <summary>A member's denials for one reason key within this long of the last one mailed for that key are held for the next mail.</summary>
    internal static readonly TimeSpan ReasonWindow = TimeSpan.FromMinutes(15);

    /// <summary>Without a <c>tool_use_id</c>, the same command in the same session within this long is one denial.</summary>
    internal static readonly TimeSpan SameCommandWindow = TimeSpan.FromSeconds(5);

    /// <summary>The key of a denial whose reason is empty.</summary>
    internal const string NoReasonKey = "(none given)";

    internal const int ReasonChars = 1000;
    internal const int ToolInputChars = 2000;
    private const int CommandChars = 400;
    private const int KeyChars = 200;
    private const int KeyInTextChars = 120;
    private const int CommandsCap = 5;

    internal const string NoVerdictEventName = "step-unfinished";

    internal const string VerdictDenied = "denied";

    internal const string VerdictNone = "no-verdict";

    /// <summary>The text the harness gives a refusal that ended before the classifier reached a verdict.</summary>
    internal const string NoVerdictPhrase = "the response ended before its verdict arrived";

    /// <summary>This many no-verdicts of one session within <see cref="NoVerdictWindow"/> are mailed once.</summary>
    internal const int NoVerdictThreshold = 3;

    internal static readonly TimeSpan NoVerdictWindow = TimeSpan.FromMinutes(30);

    /// <summary>One denial as the hook payload gives it.</summary>
    internal sealed record Denial(string SessionId, string? ToolUseId, string Command, string Reason, string Cwd, string Transcript, string ToolName = "");

    /// <summary>What to do with a denial: the event to mail (when <see cref="Mail"/>) under <paramref name="Event"/>, and the peer row that records it.</summary>
    internal sealed record Plan(bool Mail, string Text, JsonObject Data, JsonObject Row, string Event = EventName);

    /// <summary>The opening of the refusal the harness gives when the auto-mode classifier itself failed to answer (a failure of the check, not a judgment).</summary>
    internal const string ClassifierGaveNoVerdictOpener = "The server-side auto mode classifier gave no verdict";

    /// <summary>The opening of the older wording of the same refusal.</summary>
    internal const string EndedBeforeVerdictOpener = "Permission for this action was denied: " + NoVerdictPhrase;

    /// <summary>The whole reason the PermissionDenied payload carries when the classifier could not answer (the model sees the longer sentence instead).</summary>
    internal const string ClassifierUnavailableReason = "Classifier unavailable";

    /// <summary>
    /// Whether the reason is a classifier that gave no answer: the harness's no-verdict sentence, or the payload's whole reason being
    /// <see cref="ClassifierUnavailableReason"/>. The sentence counts only at the start of the reason, and the short reason only when it is
    /// the whole reason: a real denial's reason can quote a command that contains these words, and must stay a denial.
    /// </summary>
    internal static bool IsNoVerdict(string reason)
    {
        var text = reason.TrimStart();
        return text.StartsWith(ClassifierGaveNoVerdictOpener, StringComparison.OrdinalIgnoreCase)
            || text.StartsWith(EndedBeforeVerdictOpener, StringComparison.Ordinal)
            || text.TrimEnd().Equals(ClassifierUnavailableReason, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsNoVerdictRow(JsonValue r) => r.Member("verdict") is { IsText: true } v && v.AsString() == VerdictNone;

    /// <summary>
    /// The bracketed category of a denial's reason text: the one after <c>Reason: </c> anywhere in it, or the one its trimmed text starts with
    /// (<c>[Data Exfiltration]</c>); <see cref="UnknownLabel"/> when there is none.
    /// </summary>
    internal static string LabelOf(string reason)
    {
        var match = ReasonRegex().Match(reason);
        return match.Success ? match.Groups[1].Value : UnknownLabel;
    }

    /// <summary>The key a denial is deduplicated under: its label, else its reason with whitespace collapsed and capped, else <see cref="NoReasonKey"/>.</summary>
    internal static string ReasonKeyOf(string reason)
    {
        var label = LabelOf(reason);
        if (label != UnknownLabel)
        {
            return label;
        }

        var collapsed = WhitespaceRegex().Replace(reason, " ").Trim();
        return collapsed.Length == 0 ? NoReasonKey : Cap(collapsed, KeyChars);
    }

    [GeneratedRegex(@"(?:Reason: |^\s*)\[([^\]\r\n]+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex ReasonRegex();

    [GeneratedRegex("[ \\t\\r\\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// The plan for one denial given the peer rows already on record, or null when it is already on record. It is mailed when the
    /// session is a member or sub-orchestrator (<paramref name="member"/> and <paramref name="team"/> given) with no mail for the same
    /// reason key in the last <see cref="ReasonWindow"/>, from any of its sessions; the denials recorded for that key since its last
    /// mail are merged into the count and commands.
    /// </summary>
    internal static Plan? Decide(IEnumerable<JsonValue> rows, Denial d, string? member, string? team, DateTimeOffset now, (string Name, string Fix)? cause = null, string? role = null, string? ruleLine = null)
    {
        var all = rows.Where(r => IsDenialOf(r, d.SessionId)).ToList();
        var own = all.Where(static r => !IsNoVerdictRow(r)).ToList();
        var command = Cap(d.Command, CommandChars);
        if (d.ToolUseId is not null
                ? all.Any(r => r.Member("tool_use_id") is { IsText: true } t && t.AsString() == d.ToolUseId)
                : all.Any(r => r.Member("command") is { IsText: true } c && c.AsString() == command
                    && r.Member("ts") is { IsText: true } ts && Timestamps.TryParseMs(ts.AsString(), out var ms)
                    && Math.Abs(now.ToUnixTimeMilliseconds() - ms) < SameCommandWindow.TotalMilliseconds))
        {
            return null;
        }

        var noVerdict = IsNoVerdict(d.Reason);
        var label = LabelOf(d.Reason);
        var reasonKey = ReasonKeyOf(d.Reason);
        var who = member ?? "orchestrator";
        var scope = (member is null ? own : rows.Where(r => IsMemberDenial(r, member, team)).ToList())
            .Where(r => r.Member("reason_key") is { IsText: true } k && k.AsString() == reasonKey).ToList();
        var lastMailed = scope.FindLastIndex(static r => r.Member("mailed") is { IsBool: true } m && m.AsBool());
        var held = scope.Skip(lastMailed + 1).ToList();
        var recent = lastMailed >= 0 && scope[lastMailed].Member("ts") is { IsText: true } lt && Timestamps.TryParseMs(lt.AsString(), out var lastMs)
            && now.ToUnixTimeMilliseconds() - lastMs < ReasonWindow.TotalMilliseconds;
        var commands = held.Select(static r => r.Member("command") is { IsText: true } t ? t.AsString() : string.Empty)
            .Append(command)
            .Where(static t => t.Length > 0)
            .ToList();
        var count = held.Count + 1;
        var at = Timestamps.ToIsoString(now);
        var more = count > 1 ? $" (+{(count - 1).ToString(CultureInfo.InvariantCulture)} more)" : string.Empty;
        var tail = "." + CauseClause(member, team, d.Cwd);
        var text = $"{who} paused on {(d.ToolName.Length == 0 ? "(unknown tool)" : d.ToolName)}: {(label == UnknownLabel ? Cap(reasonKey, KeyInTextChars) : $"[{label}]")} — a tool call needs the user{more}{tail}";
        var mail = member is not null && team is not null && !recent;
        var eventName = EventName;
        var verdict = VerdictDenied;
        if (noVerdict)
        {
            verdict = VerdictNone;
            var windowMs = NoVerdictWindow.TotalMilliseconds;
            var inWindow = all.Where(r => IsNoVerdictRow(r) && r.Member("ts") is { IsText: true } wts && Timestamps.TryParseMs(wts.AsString(), out var wms) && now.ToUnixTimeMilliseconds() - wms < windowMs).ToList();
            count = inWindow.Count + 1;
            mail = member is not null && team is not null && count >= NoVerdictThreshold
                && !inWindow.Any(static r => r.Member("mailed") is { IsBool: true } m && m.AsBool());
            eventName = NoVerdictEventName;
            // No Details line: the diagnosis skips no-verdicts, so it would describe some older denial.
            text = $"{who}: {count.ToString(CultureInfo.InvariantCulture)} responses ended early in {NoVerdictWindow.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} min (harness); not an ah bug.";
        }
        var data = new JsonObject
        {
            ["member"] = who,
            ["team"] = team is null ? JsonValue.Null : JsonValue.FromString(team),
            ["session_id"] = d.SessionId,
            ["tool_use_id"] = d.ToolUseId is null ? JsonValue.Null : JsonValue.FromString(d.ToolUseId),
            ["command"] = command,
            ["label"] = label,
            ["reason_key"] = reasonKey,
            ["tool_name"] = d.ToolName,
            ["reason_text"] = Cap(d.Reason, ReasonChars),
            ["cwd"] = d.Cwd,
            ["at"] = at,
            ["transcript"] = d.Transcript,
            ["count"] = (double)count,
        };
        data["verdict"] = verdict;
        data["role"] = role ?? (member is null ? "orchestrator" : "unknown");
        if (cause is { } recorded)
        {
            data["cause"] = recorded.Name;
        }

        if (count > 1 && !noVerdict)
        {
            var list = new JsonArray();
            foreach (var each in commands.TakeLast(CommandsCap))
            {
                list.Add(JsonValue.FromString(each));
            }

            data["commands"] = list;
        }

        var row = new JsonObject
        {
            ["type"] = "watch-event",
            ["session_id"] = d.SessionId,
            ["request_id"] = JsonValue.Null,
            ["kind"] = Kind,
            ["text"] = text,
            ["ts"] = at,
            ["mailed"] = false,
        };
        foreach (var key in data.Keys)
        {
            if (key is not ("count" or "commands"))
            {
                row[key] = data[key];
            }
        }

        return new Plan(mail, text, data, row, eventName);
    }

    /// <summary>
    /// The line that points at the diagnosis, for a member's denial: nothing for the top Orchestrator's own. The cause stays in the row and
    /// in the diagnosis, not in the mail.
    /// </summary>
    private static string CauseClause(string? member, string? team, string cwd) =>
        member is null ? string.Empty : $" {UserDirectedHint} Details: {DiagnoseCommand(member, team, cwd)}";

    /// <summary>The diagnosis of a member, naming its team and tree so that it reads the right team from any checkout the lead runs it in.</summary>
    private static string DiagnoseCommand(string member, string? team, string cwd) =>
        $"ah roster permissions diagnose {member}"
        + (team is null ? string.Empty : $" --team {team}")
        + (cwd.Length == 0 ? string.Empty : $" --cwd {JsonText.Quote(cwd)}");

    /// <summary>Said to the lead in every member alarm: what to do when the blocked step is a change the user asked for, and what to do with the rest.</summary>
    internal const string UserDirectedHint = "If the user asked for this change, make that exact edit yourself and log it as a decision on the user's behalf (skills/agent-team/reference/escalation.md); anything the user did not ask for stays theirs: in a pipeline run park the question (skills/autonomous-pipeline/reference/decisions.md) and keep the other work going.";

    public static HookOutcome Run(GateContext c)
    {
        try
        {
            return Raise(c);
        }
        catch (Exception e) when (e is not (OperationCanceledException or StoreUnavailableException))
        {
            return HookEmit.Silent;
        }
    }

    private static HookOutcome Raise(GateContext c)
    {
        var s = c.Services;
        var sessionId = c.Input.Text("session_id") ?? string.Empty;
        if (c.Input.Text("hook_event_name") != "PermissionDenied")
        {
            return HookEmit.Silent;
        }

        LogDenial(c, sessionId);
        if (sessionId.Length == 0 || s.Config.IsSubagent(c.Input))
        {
            return HookEmit.Silent;
        }

        var advisory = s.Advisory ?? throw new InvalidOperationException("the advisory services are not composed");
        var cwd = c.Cwd();
        var dir = s.Config.HierarchyDir(cwd);
        var toolName = c.Input.Text("tool_name") ?? string.Empty;
        var denial = new Denial(
            sessionId,
            c.Input.Text("tool_use_id") is { Length: > 0 } id ? id : null,
            CommandOf(c.Input, toolName),
            c.Input.Text("reason") ?? string.Empty,
            cwd,
            c.Input.Text("transcript_path") ?? string.Empty,
            toolName);
        var address = MemberAddress(advisory, sessionId);
        var now = s.Clock.UtcNow;
        var cause = address is null || IsNoVerdict(denial.Reason) ? null : CauseOf(s, dir, cwd, address.Name, denial.Command);
        var role = address is null ? null : RoleOf(s, dir, address);
        var ruleLine = address is null || IsNoVerdict(denial.Reason) ? null : RuleLineOf(s, dir, cwd, address, role, denial.Command);
        var watchEvents = address is null
            ? s.Hierarchy.Core.SessionRows(denial.SessionId, "watch-event")
            : s.Hierarchy.Core.PeerRecordsWhere(new PeerQuery(Types: new HashSet<string>(StringComparer.Ordinal) { "watch-event" }));
        if (Decide(watchEvents, denial, address?.Name, address?.Team, now, cause, role, ruleLine) is not { } plan)
        {
            return HookEmit.Silent;
        }

        if (plan.Mail)
        {
            plan.Row["mailed"] = Send(s, advisory, dir, cwd, address!.Team, plan, now);
        }

        s.Hierarchy.Peers.RecordPeerRow(JsonValue.FromObject(plan.Row));
        return HookEmit.Silent;
    }

    /// <summary>The shell command of a denied call; for another tool its name followed by the first of <c>file_path</c>, <c>path</c>, <c>url</c> or <c>pattern</c> it was given.</summary>
    internal static string CommandOf(JsonValue input, string toolName)
    {
        var toolInput = input.Member("tool_input");
        if (toolInput?.Member("command") is { IsText: true } shell && shell.AsString().Length > 0)
        {
            return shell.AsString();
        }

        var name = toolName.Length == 0 ? "(unknown tool)" : toolName;
        foreach (var key in new[] { "file_path", "path", "url", "pattern" })
        {
            if (toolInput?.Member(key) is { IsText: true } target && target.AsString().Length > 0)
            {
                return $"{name} {target.AsString()}";
            }
        }

        return name;
    }

    /// <summary>Writes the denial to hook-errors; every payload, duplicates and subagents included. A failed write is silent.</summary>
    private static void LogDenial(GateContext c, string sessionId)
    {
        try
        {
            var reason = c.Input.Text("reason") is { Length: > 0 } given ? SafeText.Render(given, ReasonChars) : NoReasonKey;
            var toolInput = c.Input.Member("tool_input") is { } t ? SafeText.Render(JsonText.Stringify(t, 0), ToolInputChars) : "(none)";
            c.Services.ErrorLog.Log(new HookErrorRecord(
                "permission-denied-alarm",
                $"denied {c.Input.Text("tool_name") ?? "(unknown tool)"}: reason={reason} tool_input={toolInput}",
                "PermissionDenied",
                sessionId.Length == 0 ? null : sessionId));
        }
        catch (Exception e) when (e is not (OperationCanceledException or StoreUnavailableException))
        {
        }
    }

    /// <summary>The role the team file lists for the member, or null when the team file does not name it.</summary>
    private static string? RoleOf(GateServices s, string dir, MemberBox address)
    {
        try
        {
            return s.Roster.ReadTeamRecord(dir, address.Team).Member("members") is { IsArray: true } members
                ? members.AsArray().Where(m => m.Member("name") is { IsText: true } n && n.AsString() == address.Name)
                    .Select(static m => m.Member("role") is { IsText: true } r ? r.AsString() : null).FirstOrDefault()
                : null;
        }
        catch (Exception e) when (e is not (OperationCanceledException or StoreUnavailableException))
        {
            return null;
        }
    }

    /// <summary>
    /// The line that says which rule would have allowed the denied command for the member (<c>rule that would allow it: &lt;rule&gt;</c>) or why
    /// none can (<c>no sound rule: &lt;reason&gt;</c>), by the generator's own answer; null when the member's work root, role or the plugin is unknown.
    /// </summary>
    private static string? RuleLineOf(GateServices s, string dir, string cwd, MemberBox address, string? role, string command)
    {
        try
        {
            var pluginRoot = s.Environment.Plugin?.Root;
            var root = s.Roster.ReadTeamRecord(dir, address.Team).Member("members") is { IsArray: true } members
                ? members.AsArray().Where(m => m.Member("name") is { IsText: true } n && n.AsString() == address.Name)
                    .Select(static m => m.Member("expected_root") is { IsText: true } r ? r.AsString() : null).FirstOrDefault()
                : null;
            if (pluginRoot is not { Length: > 0 } || role is null || root is not { Length: > 0 })
            {
                return null;
            }

            var request = RosterModule.BuildMemberPermissionsRequest(
                s.Files,
                s.Environment.HomeDirectory,
                root,
                s.Config.CheckoutRoot(root) ?? cwd,
                pluginRoot,
                s.Config.AhCommandPath(),
                address.Name,
                role,
                root,
                null);
            var answer = HooksModule.QueryMemberRule(request, command);
            return answer.Rule is { } rule ? $"rule that would allow it: {rule}" : $"no sound rule: {answer.Why}";
        }
        catch (Exception e) when (e is not (OperationCanceledException or StoreUnavailableException))
        {
            return null;
        }
    }

    /// <summary>The cause of the denial by the same function <c>permissions diagnose</c> prints, or null when it cannot be computed.</summary>
    private static (string Name, string Fix)? CauseOf(GateServices s, string dir, string cwd, string member, string command)
    {
        try
        {
            var verdict = RosterModule.DiagnosePermission(
                s.Files,
                s.Environment.Plugin?.Root,
                s.Roster.TeamHome(dir),
                Path.Combine(s.Roster.TeamHome(dir), "asks.jsonl"),
                RosterModule.MemberAllowEnabled(s.Files, [s.Config.ProjectConfigPath(JsonValue.FromString(cwd)), s.Config.UserConfigPath()]),
                member,
                command,
                out _,
                RosterModule.MemberAllowOnAt(s.Files, s.Config.UserConfigPath()));
            return (verdict.Name, verdict.Fix);
        }
        catch (Exception e) when (e is not (OperationCanceledException or StoreUnavailableException))
        {
            return null;
        }
    }

    private static bool Send(GateServices s, AdvisoryServices advisory, string dir, string cwd, string team, Plan plan, DateTimeOffset now) =>
        SendEvent(s, advisory, dir, cwd, team, plan.Event, plan.Text, plan.Data, now);

    /// <summary>Mails one event to the Orchestrator box of <paramref name="team"/>; false when it could not be sent.</summary>
    internal static bool SendEvent(GateServices s, AdvisoryServices advisory, string dir, string cwd, string team, string eventName, string text, JsonObject data, DateTimeOffset now)
    {
        try
        {
            var request = MeshModule.EventRequest(HierarchyModule.OrchestratorAddress(team), "ahd", cwd, dir, eventName, text);
            request["session_id"] = JsonValue.Null;
            request["data"] = data;
            if (s.Roster.ReadTeamRecord(dir, team).Member("orchestrator")?.Member("session_id") is { IsText: true } orchestrator && orchestrator.AsString().Length > 0)
            {
                request["to_session"] = orchestrator;
            }

            return advisory.Mailbox.Send(request, now).Ok;
        }
        catch (Exception e) when (e is not (OperationCanceledException or StoreUnavailableException))
        {
            return false;
        }
    }

    /// <summary>A verdict denial row of this member and team, from any of its sessions.</summary>
    private static bool IsMemberDenial(JsonValue row, string member, string? team) =>
        row.IsMap && row.Member("type") is { IsText: true } t && t.AsString() == "watch-event"
        && row.Member("kind") is { IsText: true } k && k.AsString() == Kind
        && !IsNoVerdictRow(row)
        && row.Member("member") is { IsText: true } m && m.AsString() == member
        && row.Member("team") is { IsText: true } rt && rt.AsString() == team;

    private static bool IsDenialOf(JsonValue row, string sessionId) =>
        row.IsMap && row.Member("type") is { IsText: true } t && t.AsString() == "watch-event"
        && row.Member("kind") is { IsText: true } k && k.AsString() == Kind
        && row.Member("session_id") is { IsText: true } s && s.AsString() == sessionId;

    internal sealed record MemberBox(string Name, string Team);

    /// <summary>The session's own <c>name@team</c> address when it is a member or sub-orchestrator; null for a top Orchestrator.</summary>
    internal static MemberBox? MemberAddress(AdvisoryServices advisory, string sessionId)
    {
        var registration = advisory.Mailbox.ReadRegistration(sessionId);
        foreach (var box in advisory.Mailbox.RegistrationBoxes(registration is null ? JsonValue.Null : registration))
        {
            var at = box.Address.IndexOf('@', StringComparison.Ordinal);
            if (at > 0 && at < box.Address.Length - 1 && !box.Address.StartsWith("orchestrator@", StringComparison.Ordinal))
            {
                return new MemberBox(box.Address[..at], box.Address[(at + 1)..]);
            }
        }

        return null;
    }

    private static string Cap(string text, int chars) => text.Length <= chars ? text : text[..chars];
}
