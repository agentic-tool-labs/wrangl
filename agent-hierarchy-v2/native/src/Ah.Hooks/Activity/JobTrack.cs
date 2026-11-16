using Ah.Abstractions;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;
using Ah.Watch;

namespace Ah.Hooks.Activity;

/// <summary>
/// Registers a long-running Bash command as a tracked job and ends it. On PreToolUse a command in the long-job catalogue, or any
/// command run in the background, gets a record under <c>&lt;hierarchy&gt;/jobs/&lt;team&gt;</c> for the calling member or Orchestrator;
/// on PostToolUse the record of a foreground call ends. A background job is ended by the daemon when its process is gone. It never
/// blocks the call and any failure lets it through. Never writes stdout.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static class JobTrack
{
    public static HookOutcome Run(GateContext c)
    {
        if (c.Services.Config.IsSubagent(c.Input))
        {
            return HookEmit.Silent;
        }

        if (c.Input.Text("hook_event_name") is "Stop" or "UserPromptSubmit")
        {
            try
            {
                EndForeground(c);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                c.LogError(ex);
            }

            return HookEmit.Silent;
        }

        if (c.ToolName() != "Bash")
        {
            return HookEmit.Silent;
        }

        try
        {
            var phase = c.Input.Text("hook_event_name");
            if (WatchModule.JobMatch(c.Command(), c.ToolInput().Get("run_in_background") is { IsBool: true } bg && bg.AsBool(), c.Services.Environment.HomeDirectory, c.Cwd()) is null || Excluded(c))
            {
                return HookEmit.Silent;
            }

            if (phase == "PreToolUse")
            {
                Register(c);
            }
            else if (phase == "PostToolUse")
            {
                End(c, null);
            }
            else if (phase == "PostToolUseFailure")
            {
                End(c, "failed");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            c.LogError(ex);
        }

        return HookEmit.Silent;
    }

    /// <summary>The calling session as a job owner: the member or Orchestrator name, its team, session, the hierarchy dir its jobs live under and its role.</summary>
    internal sealed record Who(string Owner, string Team, string Session, string Dir, string Role);

    private static void Register(GateContext c)
    {
        var background = c.ToolInput().Get("run_in_background") is { IsBool: true } flag && flag.AsBool();
        if (WatchModule.JobMatch(c.Command(), background, c.Services.Environment.HomeDirectory, c.Cwd()) is not { } spec || c.Input.NonEmpty("tool_use_id") is not { } toolUse || WhoIs(c) is not { } who)
        {
            return;
        }

        var s = c.Services;
        var dir = WatchModule.JobsDir(who.Dir, who.Team);
        // b416: a read that cannot answer throws, so the caller's catch logs it and nothing is registered: a missed auto-record beats a duplicate
        var records = WatchModule.JobRecordFiles(s.Hierarchy.Status, dir, mustAnswer: true);
        if (WatchModule.JobFindOpen(records, who.Owner, who.Session, toolUse, null) is not null)
        {
            return;
        }

        var id = s.Hierarchy.Core.NewId();
        var record = WatchModule.JobNewRecord(id, who.Team, who.Owner, who.Session, spec, null, s.Hierarchy.Messages.LocalIso(), toolUse, null);
        s.Hierarchy.StateCalls.PutJob(who.Dir, Path.GetFileName(dir), id, record);
    }

    /// <summary>A turn cannot end while one of its foreground calls still runs, so at the owner's Stop or next prompt every open foreground job of the session is over.</summary>
    private static void EndForeground(GateContext c)
    {
        if (WhoIs(c) is not { } who)
        {
            return;
        }

        var s = c.Services;
        foreach (var open in WatchModule.JobRecordFiles(s.Hierarchy.Status, WatchModule.JobsDir(who.Dir, who.Team)))
        {
            if (open.Record.Get("ended_at") is { IsText: true }
                || WatchModule.JobRecordBackground(open.Record)
                || open.Record.Get("owner") is not { IsText: true } owner || owner.AsString() != who.Owner
                || open.Record.Get("session_id") is not { IsText: true } session || session.AsString() != who.Session
                || !(open.Record.Get("tool_use_id") is { IsText: true } toolUse && toolUse.AsString().Length > 0
                    || open.Record.Get("pid") is { IsNumber: true } pid && !s.Hierarchy.Core.PidAlive((int)pid.AsNumber())))
            {
                continue;
            }

            s.Hierarchy.StateCalls.PutJob(who.Dir, open.Team, open.Id, WatchModule.JobPatch(open.Record, WatchModule.JobEnded(open.Record, s.Hierarchy.Messages.LocalIso(), "turn-ended")), ifOpen: true);
        }
    }

    private static void End(GateContext c, string? exit)
    {
        var background = c.ToolInput().Get("run_in_background") is { IsBool: true } flag && flag.AsBool();
        if (background || c.Input.NonEmpty("tool_use_id") is not { } toolUse || WhoIs(c) is not { } who)
        {
            return;
        }

        var s = c.Services;
        var records = WatchModule.JobRecordFiles(s.Hierarchy.Status, WatchModule.JobsDir(who.Dir, who.Team));
        if (WatchModule.JobFindOpenByToolUse(records, toolUse) is { } open)
        {
            s.Hierarchy.StateCalls.PutJob(who.Dir, open.Team, open.Id, WatchModule.JobPatch(open.Record, WatchModule.JobEnded(open.Record, s.Hierarchy.Messages.LocalIso(), exit)), ifOpen: true);
        }
    }

    /// <summary>Whether the command is one wake source or a bare sleep, none of which is a job; see <see cref="WatchModule.JobExcluded"/>.</summary>
    private static bool Excluded(GateContext c)
    {
        var s = c.Services;
        var path = s.Environment.GetVariable("PATH") ?? string.Empty;
        var home = s.Environment.HomeDirectory;
        var cwd = c.Cwd();
        string? RealPath(string word)
        {
            if (word.Contains('/', StringComparison.Ordinal))
            {
                var expanded = word.Length > 1 && word[0] == '~' && word[1] == '/' && home is { Length: > 0 } ? Path.Combine(home, word[2..]) : word;
                return Resolve(Path.IsPathRooted(expanded) ? expanded : Path.Combine(cwd, expanded));
            }

            foreach (var dir in path.Split(':'))
            {
                if (!Path.IsPathRooted(dir))
                {
                    return null;
                }

                if (Resolve(Path.Combine(dir, word)) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        // Members are told to type the stable link or, when it does not lead to this install, the plugin's own launcher (ConfigCore.AhCommandPath).
        var binaries = new[]
            {
                Environment.ProcessPath,
                home is { Length: > 0 } ? Path.Combine(home, ".local", "bin", "ah") : null,
                s.Environment.Plugin is { } plugin ? Path.Combine(plugin.Root, "bin", "ah") : null,
            }
            .Select(static p => p is null ? null : Resolve(p)).OfType<string>().ToList();
        return WatchModule.JobExcluded(c.Command(), RealPath, binaries, File.Exists);
    }

    private static string? Resolve(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return File.Exists(full) ? new FileInfo(full).ResolveLinkTarget(true)?.FullName ?? full : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    internal static Who? WhoIs(GateContext c)
    {
        var s = c.Services;
        if (c.Input.NonEmpty("session_id") is not { } session)
        {
            return null;
        }

        var cwd = c.Cwd();
        var dir = s.Config.HierarchyDir(cwd);
        var main = s.Config.MainHierarchyDir(cwd);
        var home = main is { Length: > 0 } ? main : dir;
        var pane = Ah.Core.Mux.HerdrFlavour.SessionPaneId(s.Environment.GetVariable) is { Length: > 0 } herdr ? herdr : s.Environment.GetVariable("TMUX_PANE");
        var found = s.Roster.LocateSession(dir, main is { Length: > 0 } ? [dir, main] : [dir], null, false, pane);
        if (found.Get("answeredBy") is { IsTruthy: true } && found.Get("member") is { IsMap: true } member && member.AsObject().Get("name") is { IsText: true } name)
        {
            var team = found.Get("teamName") is { IsText: true } t && t.AsString().Length > 0 ? t.AsString() : "_default";
            return new Who(name.AsString(), team, session, home, member.AsObject().Get("role") is { IsText: true } role ? role.AsString() : string.Empty);
        }

        var owned = s.Roster.OwnedTeams(dir, new TeamInvoker(s.Process.ParentPid, session, null));
        return owned.Count == 1 ? new Who("orchestrator", owned[0] ?? "_default", session, home, "orchestrator") : null;
    }
}
