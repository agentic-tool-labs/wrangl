using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// Whether a payload's session is the Orchestrator: a top-level session launched with <c>--agent &lt;plugin&gt;:orchestrator</c>, or a
/// session without an <c>agent_id</c> whose entry in the session-roles map carries the Orchestrator-by-command flag. A subagent is
/// never the Orchestrator, whatever the flag says. Every hook that asks the question asks here.
/// </summary>
internal static class OrchestratorIdentity
{
    private const string AgentFile = "agents/orchestrator.md";

    internal static bool LaunchedAsAgent(GateContext c) =>
        c.Services.Config.IsTopLevelAgentSession(c.Input) && c.Services.Environment.LocalName(c.Input.Text("agent_type")) == "orchestrator";

    /// <summary>Whether the session is flagged by command. A flag store that cannot be read means not flagged.</summary>
    internal static bool Flagged(GateContext c)
    {
        if (c.Services.Config.IsSubagent(c.Input))
        {
            return false;
        }

        try
        {
            return c.Services.Config.ReadOrchestratorFlag(c.Input.Member("session_id")) is { IsMap: true };
        }
        catch (Exception e) when (e is not (OperationCanceledException or Ah.Core.Json.StoreUnavailableException))
        {
            c.LogError(e);
            return false;
        }
    }

    internal static bool Is(GateContext c) => LaunchedAsAgent(c) || Flagged(c);

    /// <summary>
    /// Whether the payload's session is part of ah: a member (marker, team file, or registration), a top-level session launched as the
    /// Orchestrator, the owner of a team, or a session that opted in. Anything else is bare and gets nothing from any hook. A subagent is
    /// judged on its <c>session_id</c>, which is its parent's, and on the environment it inherits, so a subagent follows its parent. Every guard asks here.
    /// </summary>
    internal static bool In(GateContext c)
    {
        var s = c.Services;
        if (s.Environment.GetVariable(Ah.Mesh.MeshModule.MemberMarkerEnv) is { Length: > 0 } || s.Environment.GetVariable("WRANGL_TEAM_FILE") is { Length: > 0 } || LaunchedAsAgent(c))
        {
            return true;
        }

        var cwd = c.Cwd();
        var declined = Declined(c);
        return (!declined && (FlagStored(c) || AlwaysOn(c, cwd))) || OwnsTeam(c, cwd) || (!declined && Registered(c));
    }

    /// <summary>Whether the session holds a mailbox registration. A mailbox that cannot be read means no.</summary>
    private static bool Registered(GateContext c)
    {
        try
        {
            return c.Services.Advisory is { } advisory && c.Input.NonEmpty("session_id") is { } id && advisory.Mailbox.ReadRegistration(id) is not null;
        }
        catch (Exception e) when (e is not (OperationCanceledException or Ah.Core.Json.StoreUnavailableException))
        {
            c.LogError(e);
            return false;
        }
    }

    /// <summary>Whether the session's own record says it turned itself off. A store that cannot be read means no.</summary>
    internal static bool Declined(GateContext c)
    {
        try
        {
            return c.Services.Config.ReadOrchestratorDeclined(c.Input.Member("session_id"));
        }
        catch (Exception e) when (e is not (OperationCanceledException or Ah.Core.Json.StoreUnavailableException))
        {
            c.LogError(e);
            return false;
        }
    }

    /// <summary>
    /// The flag as stored, for a subagent as well: <see cref="Flagged"/> keeps saying no for a subagent, since it answers who the Orchestrator is. A stored
    /// <c>orchestrator</c> role counts too: a session that began before the flag was written has only that, and a StopFailure carries no agent type to tell by.
    /// </summary>
    private static bool FlagStored(GateContext c)
    {
        try
        {
            var session = c.Input.Member("session_id");
            return c.Services.Config.ReadOrchestratorFlag(session) is { IsMap: true }
                || (c.Services.Config.ReadSessionRole(session) is { IsText: true } role && role.AsString() == "orchestrator");
        }
        catch (Exception e) when (e is not (OperationCanceledException or Ah.Core.Json.StoreUnavailableException))
        {
            c.LogError(e);
            return false;
        }
    }

    /// <summary>
    /// Whether the session owns a team: by its process and id in either pool (a <c>/clear</c> keeps the pid), by a team record naming
    /// this id (a resume in a new process keeps only the id), or by its registered <c>orchestrator@</c> box.
    /// </summary>
    internal static bool OwnsTeam(GateContext c, string cwd)
    {
        var s = c.Services;
        var sessionId = c.Input.NonEmpty("session_id");
        var parent = s.Process.ParentPid;
        var invoker = new TeamInvoker(parent > 1 ? parent : null, sessionId);
        try
        {
            var pool = s.Config.HierarchyDir(cwd);
            var main = s.Config.MainHierarchyDir(cwd);
            foreach (var dir in string.IsNullOrEmpty(main) || main == pool ? [pool] : new[] { pool, main })
            {
                if (s.Roster.OwnedTeams(dir, invoker).Count > 0
                    || new string?[] { null }.Concat(s.Roster.ListTeamNames(dir)).Any(team => SessionStart.RecordsThisSession(s.Roster.ReadTeamRecord(dir, team), invoker)))
                {
                    return true;
                }
            }

            return s.Advisory is { } advisory && sessionId is not null && Ah.Hooks.Activity.UserPromptSubmitPeerTracking.OwnsOrchestratorBox(advisory, sessionId);
        }
        catch (Exception e) when (e is not (OperationCanceledException or Ah.Core.Json.StoreUnavailableException))
        {
            c.LogError(e);
            return false;
        }
    }

    /// <summary>
    /// The team this Orchestrator runs and the hierarchy dir that holds it: by its launcher's team file, else the one team the ownership
    /// <see cref="OwnsTeam"/> reads finds across both pools that is not held and has a live member. Null when it has none yet, or several and nothing says which.
    /// </summary>
    internal static (string Team, string? Dir)? OwnedTeam(GateContext c, string cwd)
    {
        var s = c.Services;
        if (s.Environment.GetVariable("WRANGL_TEAM_FILE") is { Length: > 0 } file && Path.GetFileNameWithoutExtension(file) is { Length: > 0 } fromFile)
        {
            return (fromFile, DirOfTeamFile(file));
        }

        var sessionId = c.Input.NonEmpty("session_id");
        var parent = s.Process.ParentPid;
        var invoker = new TeamInvoker(parent > 1 ? parent : null, sessionId);
        var pool = s.Config.HierarchyDir(cwd);
        var main = s.Config.MainHierarchyDir(cwd);
        var found = new List<(string Team, string? Dir)>();
        foreach (var dir in string.IsNullOrEmpty(main) || main == pool ? [pool] : new[] { pool, main })
        {
            // ponytail: the legacy unnamed team counts as no team, so it gets repo and held-item rules only; name it if that ever matters
            found.AddRange(s.Roster.OwnedTeams(dir, invoker)
                .Concat(s.Roster.ListTeamNames(dir).Where(team => SessionStart.RecordsThisSession(s.Roster.ReadTeamRecord(dir, team), invoker)))
                .OfType<string>()
                .Where(team => s.Roster.HoldOf(dir, team) is null && HasLiveMember(s, dir, team))
                .Select(team => (team, (string?)dir)));
        }

        return found.Select(f => f.Team).Distinct(StringComparer.Ordinal).Count() == 1 ? found[0] : null;
    }

    private static bool HasLiveMember(GateServices s, string dir, string team) =>
        s.Hierarchy.Core.Liveness is { } live && s.Roster.ReadTeamRecord(dir, team).Member("members") is { IsArray: true } members
        && members.AsArray().Any(m => m.Member("name") is { IsText: true } name && live.ForMember(name.AsString(), team) is { Live: true });

    /// <summary>The hierarchy dir of a team file <c>&lt;dir&gt;/teams/x.json</c>; null for any other shape.</summary>
    internal static string? DirOfTeamFile(string file) =>
        Path.GetDirectoryName(file) is { } parent && Path.GetFileName(parent) == "teams" ? Path.GetDirectoryName(parent) : null;

    internal static bool AlwaysOn(GateContext c, string cwd)
    {
        try
        {
            return c.Services.Config.ResolveConfig(cwd).Get("alwaysOrchestrator") is { IsTruthy: true };
        }
        catch (Exception e) when (e is not (OperationCanceledException or Ah.Core.Json.StoreUnavailableException))
        {
            c.LogError(e);
            return false;
        }
    }

    /// <summary>The Orchestrator's brief: the body of the plugin's <c>agents/orchestrator.md</c> without its frontmatter, or null when it cannot be read.</summary>
    internal static string? Body(GateContext c)
    {
        try
        {
            if (c.Services.Environment.Plugin?.Root is not { Length: > 0 } root)
            {
                return null;
            }

            var text = c.Services.Files.ReadText(NodePath.Join(root, AgentFile)).Replace("\r\n", "\n", StringComparison.Ordinal);
            if (text.StartsWith("---\n", StringComparison.Ordinal))
            {
                var end = text.IndexOf("\n---\n", 3, StringComparison.Ordinal);
                text = end < 0 ? text : text[(end + 5)..];
            }

            return text.Trim().Length == 0 ? null : text.Trim();
        }
        catch (Exception e) when (e is not (OperationCanceledException or Ah.Core.Json.StoreUnavailableException))
        {
            c.LogError(e);
            return null;
        }
    }

    /// <summary>
    /// One <c>session-roles orchestrator</c> write for the payload's session, owned by the harness process (the hook's parent).
    /// <paramref name="owner"/> false sends no process, as <c>off</c> needs none.
    /// </summary>
    internal static StateResult Write(GateContext c, string action, string cwd, bool owner = true)
    {
        var s = c.Services;
        var advisory = s.Advisory ?? throw new InvalidOperationException("the advisory services are not composed");
        var args = new JsonObject { ["action"] = action, ["session_id"] = c.Input.Member("session_id") ?? JsonValue.Null };
        var pid = s.Process.ParentPid;
        if (owner && pid > 1 && s.Sessions?.Table.StartTime(pid) is { Length: > 0 } start)
        {
            args["pid"] = pid;
            args["pid_start"] = start;
        }

        try
        {
            s.Bootstrap.EnsureHierarchyDir(s.Config.HierarchyDir(cwd));
        }
        catch (Exception e) when (e is not (OperationCanceledException or Ah.Core.Json.StoreUnavailableException))
        {
            // the helper then writes directly, as it does for any context the daemon refuses
        }

        return s.State.Invoke("session-roles", "orchestrator", JsonText.ToUtf8(args, 0), advisory.Callers.RequestContext(cwd), new StateCallOptions(Hook: true));
    }

    internal static bool Changed(StateResult result) =>
        result.Ok && JsonText.Parse(result.ResultJson.Span) is { IsMap: true } v && v.AsObject().Get("changed") is { IsTruthy: true };
}
