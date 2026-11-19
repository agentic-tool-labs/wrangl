using Ah.Core.Json;
using Ah.Roster;

namespace Ah.Commands;

/// <summary>
/// The roster CLI's shared layer: what every verb does before its own body, and how a verb's failure becomes an exit code.
/// A verb is a <c>[Verb("roster:&lt;name&gt;")]</c> function that hands its arguments and a handler to <see cref="Execute"/>;
/// a stream adds verbs by adding such functions, and edits nothing here.
/// <para>
/// Rules for every stream that adds roster verbs (R2, R3, R4):
/// a verb takes its <see cref="RosterContext"/> and nothing else, and never asks <c>CommandsModule</c> for services itself;
/// a service it needs goes on <see cref="IRosterCliServices"/> and is supplied by the composition root;
/// no new static field and no allowlist entry (a <c>static readonly</c> table is fine);
/// every test that composes the roster services is in <c>[Collection("RosterServices")]</c>, because the composed services
/// are one process-wide slot;
/// a failure is <c>throw new RosterFailure(message)</c>, which prints <c>ah roster: message</c> and exits 2, and any other exit
/// code is the handler's return value.
/// </para>
/// </summary>
internal static class RosterCli
{
    /// <summary>The verbs that edit or read a roster template; <c>--team</c> means nothing to them.</summary>
    private static readonly HashSet<string> TemplateVerbs =
        new(StringComparer.Ordinal) { "init", "add", "edit", "remove", "show" };

    /// <summary>The verbs whose purpose is writing a roster level file, and the two removed ones that name what they used to write.</summary>
    private static readonly HashSet<string> RosterMutating =
        new(StringComparer.Ordinal) { "init", "add", "edit", "remove", "layout", "alias" };

    /// <summary>
    /// Whether this invocation writes a roster level file: one of <see cref="RosterMutating"/>, or <c>roster use</c> and the
    /// <c>roster copy</c> / <c>roster delete</c> that are not dry runs.
    /// </summary>
    private static bool MutatesTemplate(RosterContext c)
    {
        if (RosterMutating.Contains(c.Command))
        {
            return true;
        }

        if (c.Command != "roster")
        {
            return false;
        }

        var sub = c.Opts.Positionals.Count > 0 ? c.Opts.Positionals[0] : null;
        return sub == "use" || (sub is "copy" or "delete" && !c.Opts.IsTrue("dry-run"));
    }

    /// <summary>Team-side verbs that, given no <c>--team</c>, act on the team the invoking session owns before any default.</summary>
    private static readonly HashSet<string> OwnedTeamVerbs = new(StringComparer.Ordinal)
    {
        "create", "spawn-one", "spawn-ad-hoc", "dismiss", "disband", "snapshot", "untrack", "resync", "move", "transfer", "deliver",
        "answer", "stream-open", "stream-status", "stream-done", "stream-label", "progress", "stream-report", "checkpoint", "checklist", "lane-caps", "ledger", "timing",
        "quit", "pause", "stop", "duty", "pause-member", "resume-member", "rename",
    };

    /// <summary>The verb words of the Node CLI, in its usage message.</summary>
    internal static readonly IReadOnlyList<string> NodeVerbs =
    [
        "show", "init", "add", "edit", "remove", "roster", "role", "pack", "create", "next-split", "layout-splits", "disband",
        "resync", "move", "spawn-one", "spawn-ad-hoc", "adopt", "untrack", "teams", "team", "reap", "history", "checkin", "whoami",
        "doctor", "wake", "overdue", "stall-diagnose", "checklist",
    ];

    internal const string NotNative = "ah: roster is not implemented natively";

    /// <summary>
    /// Runs one roster verb: parse the flags, answer <c>--help</c>, resolve the team scope, apply the pre-dispatch refusals,
    /// then call <paramref name="handler"/>. A failure is printed as <c>ah roster: &lt;message&gt;</c> and exits 2.
    /// </summary>
    /// <param name="rest">The arguments after <c>roster</c>, the verb word first.</param>
    /// <param name="stdout">Where the verb's output goes.</param>
    /// <param name="stderr">Where a refusal's message goes.</param>
    /// <param name="handler">The verb's body; returns its exit code.</param>
    internal static int Execute(string[] rest, TextWriter stdout, TextWriter stderr, Func<RosterContext, int> handler)
    {
        ArgumentNullException.ThrowIfNull(rest);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(handler);
        var composeStart = Ah.Core.Time.PhaseTrace.Begin();
        var services = CommandsModule.RosterServices();
        Ah.Core.Time.PhaseTrace.End("compose-services", composeStart);
        if (services is null)
        {
            stderr.WriteLine(NotNative);
            return 70;
        }

        var opts = RosterArgs.Parse(rest);
        string? command = null;
        if (opts.Positionals.Count > 0)
        {
            command = opts.Positionals[0];
            opts.Positionals.RemoveAt(0);
        }

        if (opts.IsTrue("help") || command is null)
        {
            stdout.Write(RosterUsage.For(command));
            stdout.Write('\n');
            return 0;
        }

        var context = new RosterContext(services, opts, command, opts.Text("cwd") ?? services.CurrentDirectory, stdout, stderr, rest);
        var code = Guarded(context, handler, command, stderr);
        if (code is not (0 or 3) && command is "spawn-one" or "spawn-ad-hoc" or "create")
        {
            stderr.Write(TeamCommandsLine + "\n");
        }

        return code;
    }

    /// <summary>ah2-216 D4: the line a refused or failed <c>spawn-one</c>, <c>spawn-ad-hoc</c> or <c>create</c> ends with; not on exit 3, a partial result whose output is JSON (Amendment 4).</summary>
    internal const string TeamCommandsLine = "Team commands: `ah rules show peer-roster`.";

    private static int Guarded(RosterContext context, Func<RosterContext, int> handler, string command, TextWriter stderr)
    {
        try
        {
            var prepareStart = Ah.Core.Time.PhaseTrace.Begin();
            Prepare(context);
            Ah.Core.Time.PhaseTrace.End("prepare", prepareStart);
            var verbStart = Ah.Core.Time.PhaseTrace.Begin();
            var exit = handler(context);
            Ah.Core.Time.PhaseTrace.End("verb", verbStart);
            return exit;
        }
        catch (OwnerRefusal refusal)
        {
            var refused = new JsonObject { ["status"] = "refused", ["refused"] = refusal.Refused, ["proof"] = refusal.Proof };
            if (refusal.ProofFailed is { } failed)
            {
                refused["proof_failed"] = failed;
            }

            context.Out(refused);
            stderr.Write($"{refusal.First}\n{refusal.Why}\n");
            return 2;
        }
        catch (RosterFailure failure)
        {
            stderr.Write($"ah roster: {failure.Message}\n");
            return failure.ExitCode;
        }
        catch (StoreUnavailableException unreadable)
        {
            context.Out(new JsonObject { ["status"] = "refused", ["refused"] = "store-unreadable", ["error"] = unreadable.Message });
            stderr.Write($"ah roster {command}: the state store cannot be read ({unreadable.Message}); nothing was changed.\n{(unreadable.Advice is { } advice ? advice + "\n" : string.Empty)}");
            return 2;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var message = string.IsNullOrEmpty(error.Message) ? error.GetType().Name : error.Message;
            var prefix = error.Data["code"] is "conflict" ? "conflict: " : string.Empty;
            stderr.Write($"ah roster: {prefix}{message}\n");
            return 2;
        }
    }

    /// <summary>The refusal a verb that does not exist gets: Node's usage line, with the unknown word in it.</summary>
    internal static RosterFailure UnknownVerb(string command) => new(
        $"usage: ah roster {string.Join('|', NodeVerbs)} [--commit] [--level global|repo|repo-user] [--team <name>] [--cwd <path>] " +
        $"(unknown command {JsonText.Quote(command)})");

    private static void Prepare(RosterContext c)
    {
        var cmd = c.Command;
        if (TemplateVerbs.Contains(cmd) && c.Opts.Has("team"))
        {
            throw new RosterFailure($"{cmd}: `--team` names a live team; to edit a named roster use `--roster <r>`");
        }

        var rosterFlag = ResolveRosterFlag(c);
        var createFrom = cmd == "create" && c.Opts.Text("from") is not null;
        if ((TemplateVerbs.Contains(cmd) || cmd == "create") && !createFrom)
        {
            var options = new JsonObject { ["flag"] = rosterFlag is null ? JsonValue.Null : JsonValue.FromString(rosterFlag) };
            c.Selection = c.Services.Config.RosterSelection(c.Cwd, JsonValue.FromObject(options));
        }

        c.RosterArg = c.Selection is not null && c.Selection.Get("key") is { IsText: true } key ? key.AsString() : rosterFlag;
        c.TeamArg = ResolveTeamArg(c);
        c.RepoBasename = c.Services.Config.TeamPrefix(c.Cwd, c.TeamArg);
        ResolveTeamFileScope(c);
        OwnerGuard.Check(c);
        RefuseRosterEditWhileOwningTeam(c);
        RefuseMissingSelection(c);
    }

    private static string? ResolveRosterFlag(RosterContext c)
    {
        if (!c.Opts.Has("roster"))
        {
            return null;
        }

        if (c.Opts.Text("roster") is not { } name)
        {
            throw new RosterFailure("--roster needs a value: --roster <name>");
        }

        var check = c.Services.Config.ValidateTeamAlias(JsonValue.FromString(name));
        if (!Truthy(check, "ok"))
        {
            throw new RosterFailure($"--roster: {Text(check, "why")}");
        }

        return name;
    }

    private static string? ResolveTeamArg(RosterContext c)
    {
        if (c.Opts.Text("team") is not { } team)
        {
            return null;
        }

        if (team == RosterModule.DefaultTeamArg)
        {
            c.TeamDefaultExplicit = true;
            return null;
        }

        var check = c.Services.Config.ValidateTeamAlias(JsonValue.FromString(team));
        if (!Truthy(check, "ok"))
        {
            throw new RosterFailure($"--team: {Text(check, "why")}");
        }

        return team;
    }

    internal static void ResolveTeamFileScope(RosterContext c)
    {
        var roster = c.Services.Roster;
        if (c.TeamArg is not null || c.TeamDefaultExplicit)
        {
            c.TeamFile = c.TeamArg;
            c.TeamFileDefaulted = false;
            if (c.TeamArg is { } named && OwnedHomeElsewhere(c, named) is { } home)
            {
                c.PinDir(home);
                c.TeamHomeElsewhere = home;
            }

            return;
        }

        if (c.UserActs && !OwnerGuard.ActsOnAllOwned(c) && !c.Opts.IsTrue("all"))
        {
            // the user owns no team by pid: the folder's top teams are the choice, and one alone needs no --team
            var tops = roster.ListTeamNames(c.Dir).Where(t => roster.ReadTeamRecord(c.Dir, t) is { IsNull: false } r && r.Member("parent") is not { IsMap: true }).ToList();
            if (tops.Count > 1)
            {
                throw new RosterFailure($"{c.Command}: this folder has {tops.Count} teams ({string.Join(", ", tops)}); pass --team <name> to say which, or --all");
            }

            if (tops.Count == 1)
            {
                c.TeamFile = tops[0];
                c.TeamFileDefaulted = false;
                c.RepoBasename = c.Services.Config.TeamPrefix(c.Cwd, c.TeamFile);
                return;
            }
        }

        if ((OwnedTeamVerbs.Contains(c.Command) || IsPermissionsDiagnose(c)) && !OwnerGuard.ActsOnAllOwned(c))
        {
            var owned = roster.OwnedTeams(c.Dir, c.Invoker());
            var member = owned.Count > 1 ? NamedMember(c) : null;
            var holders = roster.TeamsWithMember(c.Dir, owned, member);
            if (owned.Count > 1 && holders.Count != 1)
            {
                var why = member is null
                    ? string.Empty
                    : $"; \"{member}\" is a member of {(holders.Count > 0 ? "more than one of them" : "none of them")}";
                throw new RosterFailure(
                    $"{c.Command}: this session owns {owned.Count} live teams ({RosterModule.TeamListText(owned)}) — pass --team <name> to say which{(c.Command is "quit" or "pause" ? ", or --all" : string.Empty)}{why}");
            }

            if (owned.Count >= 1)
            {
                c.TeamFile = owned.Count > 1 ? TextOrNull(holders[0]) : owned[0];
                c.TeamFileDefaulted = false;
                c.RepoBasename = c.Services.Config.TeamPrefix(c.Cwd, c.TeamFile);
                return;
            }
        }

        var scope = roster.DefaultTeamScope(
            c.Dir,
            JsonValue.FromString(c.Services.Config.TeamPrefix(c.Cwd, null)),
            raw =>
            {
                var suggested = c.Services.Config.SuggestTeamAlias(raw, JsonValue.Null);
                return suggested.IsText ? suggested.AsString() : null;
            });
        c.TeamFile = scope.Get("team") is { IsText: true } file ? file.AsString() : null;
        c.TeamFileDefaulted = scope.Get("defaulted") is { IsTruthy: true };
        c.TeamFileUnnamable = scope.Get("unnamable") ?? JsonValue.Null;
        c.TeamFileUnreadable = scope.Get("unreadable") ?? JsonValue.Null;
    }

    /// <summary>
    /// The hierarchy directory of the one live team named <paramref name="team"/> this session owns, when no home of <c>--cwd</c> holds a team
    /// of that name: a live registration names its team file, so a <c>--cwd</c> in another repo acts on that team, never on a second one of the same name.
    /// </summary>
    private static string? OwnedHomeElsewhere(RosterContext c, string team)
    {
        var roster = c.Services.Roster;
        if (c.Services.Mailbox is not { } mailbox || c.Homes.Any(h => !roster.ReadTeamRecord(h, team).IsNull))
        {
            return null;
        }

        var invoker = c.Invoker();
        var homes = mailbox.LiveRegistrations()
            .SelectMany(r => (r.Get("team_file") is { IsText: true } single ? [single.AsString()] : Array.Empty<string>())
                .Concat(r.Get("team_files") is { IsMap: true } m ? m.AsObject().Keys.Select(k => m.AsObject().Get(k)).Where(f => f is { IsText: true }).Select(f => f!.Value.AsString()) : []))
            .Where(f => Path.GetFileName(f) == team + ".json")
            .Select(f => Path.GetDirectoryName(Path.GetDirectoryName(f)) ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .Where(h => roster.ReadTeamRecord(h, team) is { IsNull: false } t && roster.IsTeamOwnedBy(t, invoker))
            .ToList();
        return homes.Count == 1 ? homes[0] : null;
    }

    /// <summary><c>permissions diagnose</c> reads one member of a team, so it finds the team from the member's name like dismiss does; no other permissions verb does.</summary>
    private static bool IsPermissionsDiagnose(RosterContext c) =>
        c.Command == "permissions" && c.Opts.Positionals.Count > 0 && c.Opts.Positionals[0] == "diagnose";

    /// <summary>The existing member a team verb names, if any: the positional of dismiss, untrack, move, deliver and answer, or spawn's <c>--member</c>.</summary>
    private static string? NamedMember(RosterContext c) => c.Command switch
    {
        "dismiss" or "untrack" or "move" or "transfer" or "deliver" or "answer" or "pause-member" or "resume-member" => c.Opts.Positionals.Count > 0 ? c.Opts.Positionals[0] : null,
        "permissions" => c.Opts.Positionals.Count > 1 ? c.Opts.Positionals[1] : null,
        "spawn-one" or "spawn-ad-hoc" => c.Opts.Text("member"),
        _ => null,
    };

    /// <summary>
    /// The roster is a template for future teams, so a session that owns a live one is refused when it edits it. Ownership is
    /// decided by pid equality, never by a role scan, and every team file of the hierarchy dir is checked.
    /// </summary>
    private static void RefuseRosterEditWhileOwningTeam(RosterContext c)
    {
        // ah2-76: the user, and the session the user made admin, are never refused.
        if (!MutatesTemplate(c) || c.Opts.IsTrue("allow-roster-edit") || c.Services.OwnerProof?.IsUser() == true)
        {
            return;
        }

        var pid = c.OwnerPid();
        if (!RosterContext.IsInteger(pid))
        {
            return;
        }

        var invoker = c.Invoker();
        List<string?> names = [null];
        names.AddRange(c.Services.Roster.ListTeamNames(c.Dir));
        foreach (var name in names)
        {
            var existing = c.Services.Roster.ReadTeamRecord(c.Dir, name);
            if (existing.IsNull || !c.Services.Roster.IsTeamOwnedBy(existing, invoker) || TeamScopes.IsEmptyImplicitTeam(existing))
            {
                continue;
            }

            var teamId = existing.Member("team_id") is { } id ? JsonConvertText(id) : "undefined";
            var label = name is null ? "the default team" : $"team \"{name}\"";
            throw new RosterFailure(
                $"{(c.Command == "roster" ? $"roster {c.Opts.Positionals[0]}" : c.Command)} edits the roster TEMPLATE, and this session owns live team {teamId} ({label}). " +
                "To add or change a member of the RUNNING team — including one that diverges from the roster, or a role the roster does not define — " +
                "run `ah roster spawn-ad-hoc <role> [--model M] [--kind K] [--args '[...]']`, which writes only the team file. " +
                "If you genuinely mean to edit the template for future teams while this one runs, the user can re-run with --allow-roster-edit.");
        }
    }

    private static void RefuseMissingSelection(RosterContext c)
    {
        if (c.Selection is null || c.Command == "init")
        {
            return;
        }

        var problem = c.Services.Config.RosterSelectionProblem(c.Cwd, c.Selection);
        if (problem.IsTruthy)
        {
            throw new RosterFailure($"{c.Command}: {JsonConvertText(problem)}");
        }
    }

    private static bool Truthy(JsonObject o, string key) => o.Get(key) is { IsTruthy: true };

    private static string Text(JsonObject o, string key) => o.Get(key) is { } v ? JsonConvertText(v) : "undefined";

    private static string? TextOrNull(JsonValue v) => v.IsText ? v.AsString() : null;

    private static string JsonConvertText(JsonValue v) => JsConvert.ToJsString(v);
}
