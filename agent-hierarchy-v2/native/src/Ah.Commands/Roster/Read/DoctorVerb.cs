using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Mesh;
using Ah.Watch;

namespace Ah.Commands;

/// <summary>
/// <c>doctor</c>: one row per check, read-only. A check that throws degrades to a <c>warn</c> row carrying the message: a
/// self-check that dies on the first surprise is the one thing it must never do.
/// </summary>
internal static partial class DoctorVerb
{
    [Verb("roster:doctor")]
    internal static int Doctor(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, Run);

    internal sealed record Check(string Status, string Detail);

    /// <summary>A warn row naming each settings file (user, then this project) where an <c>ah@*</c> (v1) plugin is enabled; null when none. Reads only.</summary>
    private static Check? AhV1Row(RosterContext c)
    {
        var files = new[] { LifecyclePermissions.SettingsPath(c.Services.Environment), Ah.Core.Text.NodePath.Join(c.Cwd, ".claude", "settings.json") };
        var where = files.Distinct().Where(f => EnablesAhV1(c, f)).ToList();
        return where.Count == 0
            ? null
            : new Check("warn", $"ah v1 is enabled in {string.Join(", ", where)}; disable it while using Wrangl: /plugin disable ah@agent-tools");
    }

    private static bool EnablesAhV1(RosterContext c, string file)
    {
        try
        {
            return c.Services.Files.Exists(file)
                && JsonText.TryParse(System.Text.Encoding.UTF8.GetBytes(c.Services.Files.ReadText(file)), out var doc)
                && doc.Member("enabledPlugins") is { IsMap: true } plugins
                && plugins.AsObject() is var map
                && map.Keys.Any(k => k.StartsWith("ah@", StringComparison.Ordinal) && map[k] is { IsBool: true } on && on.AsBool());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The team's progress records: ok, off, or a warning naming the streams without a checklist, the stale ones and the files a newer ah wrote. Never red.</summary>
    private static Check ProgressRow(RosterContext c)
    {
        var team = c.ProgressTeam;
        var records = c.Services.Hierarchy.Progress.Read(c.ProgressPool, team);
        if (records.Off)
        {
            return new Check("ok", $"off: `ah progress on {team}` turns it on");
        }
        var record = c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile);
        if (record.IsNull)
        {
            return new Check("ok", "no team file");
        }

        if (records.Streams.Count == 0 && !records.RollupPresent)
        {
            return new Check("ok", $"no progress records yet for {team}; the first report or `stream-open` creates them");
        }

        var open = new List<string>();
        if (records.Streams.Any(p => p.Stream == "main"))
        {
            open.Add("main");
        }

        if (record.Member("streams") is { IsMap: true } streams)
        {
            foreach (var key in streams.AsObject().Keys)
            {
                if (MemberPlanning.Text(streams.Member(key)?.Member("state")) == "open" && !open.Contains(key))
                {
                    open.Add(key);
                }
            }
        }

        var problems = new List<string>();
        var missing = open.Where(s => records.Streams.FirstOrDefault(p => p.Stream == s) is not { HasChecklist: true }).ToList();
        if (missing.Count > 0)
        {
            problems.Add($"no checklist for {string.Join(", ", missing)}");
        }

        var stale = records.Streams.Where(s => s.Stale).Select(s => s.Stream).ToList();
        if (stale.Count > 0)
        {
            problems.Add($"checklist behind its journal for {string.Join(", ", stale)} (a report is more than 30 minutes newer than `Updated:`)");
        }

        problems.AddRange(records.Streams.SelectMany(s => s.Warnings.Select(w => $"{s.Stream}.md: {w}")));

        if (records.NewerFormatFiles.Count > 0)
        {
            problems.Add($"written by a newer ah, read only: {string.Join(", ", records.NewerFormatFiles)}");
        }

        if (c.Services.Scratch?.IsIgnored(c.TeamHome, Ah.Core.Text.NodePath.Join(c.ProgressPool, "progress", team, ".probe")) == false)
        {
            problems.Add("progress files are under a tracked .claude/; they will be committed");
        }

        return problems.Count > 0
            ? new Check("warn", string.Join("; ", problems))
            : new Check("ok", $"{open.Count} open stream(s) with checklists, none stale");
    }

    /// <summary>Warns when the charter of a running lane holds a response, and is absent otherwise: `msg new --type response` refuses one, so it was written by hand and the charter stays open.</summary>
    /// <summary>The background jobs ahd ended as unseen in the last 24 h; more than <see cref="UnseenWarnAbove"/> points at pid discovery not matching.</summary>
    private const int UnseenWarnAbove = 3;

    private static Check JobsUnseenRow(RosterContext c)
    {
        var root = Path.Combine(c.MainDir ?? c.Dir, "jobs");
        var since = c.Services.Clock.UtcNow.AddHours(-24);
        var unseen = 0;
        foreach (var team in c.Services.Hierarchy.Status.JobRows(c.MainDir ?? c.Dir).Select(static r => r.Team).Distinct(StringComparer.Ordinal))
        {
            unseen += WatchModule.JobRecordFiles(c.Services.Hierarchy.Status, Path.Combine(root, team)).Count(f =>
                f.Record.Get("exit") is { IsText: true } exit && exit.AsString() == "unseen"
                && f.Record.Get("ended_at") is { IsText: true } ended && Ah.Core.Time.Timestamps.TryParseMs(ended.AsString(), out var ms) && ms >= since.ToUnixTimeMilliseconds());
        }

        return unseen > UnseenWarnAbove
            ? new Check("warn", $"{unseen} background job(s) ended unseen in the last 24 h: ahd found no process for them, which points at process discovery not matching rather than at short commands")
            : new Check("ok", $"{unseen} background job(s) ended unseen in the last 24 h");
    }

    private static Check? CharterResponseRow(RosterContext c)
    {
        var early = c.Services.Hierarchy.Core.ListExchanges(c.Dir)
            .Where(e => e.Member("charter") is { IsTruthy: true } && e.Member("response") is { IsMap: true })
            .Select(e => MemberPlanning.JsConvertText(e.Member("id")))
            .ToList();
        return early.Count > 0
            ? new Check("warn", $"{string.Join(", ", early)}: a lane's charter holds a response written by hand (`msg new --type response` refuses one); the charter stays open until the lane is done. Remove the response file, or send status with `msg send --kind note`")
            : null;
    }

    /// <summary>
    /// Warns when two or more live writers share a tree, when a live member is cross-stream, or when a stream's worktree has only
    /// cross-stream occupants. Never red.
    /// </summary>
    private static Check TreeOwnershipRow(RosterContext c)
    {
        if (c.TeamFile is not { } team || c.Services.Roster.ReadTeamRecord(c.Dir, team).IsNull)
        {
            return new Check("ok", "no team file");
        }

        var problems = Ah.Hierarchy.HierarchyModule.TreeProblems(c.Services.Hierarchy.Roster.TreeFactsOfTeam(c.Dir, team));
        return problems.Count > 0 ? new Check("warn", string.Join("; ", problems)) : new Check("ok", "one writer per tree, trees match their streams");
    }

    /// <summary>How many answers the Codex grant log holds, and the latest three, so a grant the Orchestrator made on the user's standing approval can be seen.</summary>
    private static Check CodexGrants(RosterContext c)
    {
        var path = Path.Combine(c.Services.Environment.AhHome, "codex-grants.jsonl");
        if (!File.Exists(path))
        {
            return new Check("ok", "no Codex approvals logged");
        }

        var rows = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();
        var latest = rows.TakeLast(3).Select(l =>
        {
            try
            {
                var row = JsonText.Parse(l);
                string Field(string key) => row.Member(key) is { IsText: true } t ? t.AsString() : "-";
                return $"{Field("at")} {Field("by")} {Field("choice")} {Field("kind")} {Field("member")}";
            }
            catch (JsonParseException)
            {
                return "(unreadable row)";
            }
        });
        return new Check("ok", $"{rows.Count} Codex approval answer(s) logged; latest: {string.Join("; ", latest)}");
    }

    private static readonly (string Id, string Hint)[] DesignSourceRows =
    [
        ("frontend-design", "re-run install.sh"),
        ("vercel-labs", "install by hand: npx skills add vercel-labs/agent-skills"),
        ("jakubkrehel", "/plugin marketplace add jakubkrehel/skills, then /plugin install interfaces@interfaces"),
        ("addyosmani", "/plugin marketplace add addyosmani/web-quality-skills, then /plugin install web-quality-skills@addy-web-quality-skills"),
    ];

    private static Check UxSkills(RosterContext c)
    {
        if (c.Services.Doctor is not { } doctor)
        {
            return new Check("warn", "design sources were not probed: no doctor services");
        }

        var present = doctor.DesignSources();
        var missing = DesignSourceRows.Where(r => !present.Contains(r.Id)).ToList();
        if (missing.Count == 0)
        {
            return new Check("ok", $"{DesignSourceRows.Length}/{DesignSourceRows.Length} design sources installed");
        }

        var names = string.Join("; ", missing.Select(r => $"{r.Id} ({r.Hint})"));
        return new Check("warn", $"{DesignSourceRows.Length - missing.Count}/{DesignSourceRows.Length} design sources installed; missing: {names}. ux-engineer works without them");
    }

    private static int Run(RosterContext c)
    {
        var rows = new JsonArray();
        var red = new JsonArray();
        if (c.Opts.Has("statusline-tap") && !c.Opts.IsTrue("fix"))
        {
            throw new RosterFailure("--statusline-tap needs --fix");
        }

        if (c.Opts.IsTrue("brief") && c.Opts.IsTrue("fix"))
        {
            throw new RosterFailure("--brief cannot be combined with --fix");
        }

        var fixes = c.Opts.IsTrue("fix") ? Fix(c) : null;

        void Add(string name, Func<Check> check)
        {
            Check result;
            try
            {
                result = check();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                result = new Check("warn", $"check failed: {(string.IsNullOrEmpty(e.Message) ? e.GetType().Name : e.Message)}");
            }

            rows.Add(new JsonObject { ["name"] = name, ["status"] = result.Status, ["detail"] = result.Detail });
            if (result.Status == "red")
            {
                red.Add(name);
            }
        }

        var root = OwnRoot(c);
        Add("version", () => new Check("ok", $"ah {PluginVersion(c)} at {root}"));
        Add("install", () => Install(c, root));
        Add("identity", () => Identity(c));
        Add("owner-proof", () => OwnerProofRow(c));
        Add("release-handshake", ReleaseHandshakeRow);
        Add("lifecycle-permissions", () => LifecyclePermissions.DoctorRow(c));
        Add("runtime-dir", () => RuntimeDir(c));
        Add("config", () => ConfigRow(c));
        Add("team", () => Team(c));
        Add("progress", () => ProgressRow(c));
        Add("jobs-unseen", () => JobsUnseenRow(c));
        Add("evidence", () =>
        {
            var (n, bytes) = HierarchyModule.EvidenceTotals(c.Services.Environment.AhHome);
            return new Check("ok", $"{n} files, {HierarchyModule.EvidenceSize(bytes)}"); // informational (adr-39): never a failure
        });
        Add("alarm", () => AlarmRow(c));
        if (LeadMailRow(c) is { } leadMail)
        {
            Add("lead-mail", () => leadMail);
        }
        Add("test-bucket", () =>
        {
            var (level, detail) = c.Services.Doctor?.TestBucket() ?? ("ok", "not probed: no doctor services");
            return new Check(level, detail);
        });
        Add("peers", () => Peers(c));
        try
        {
            if (BlockTriage.DoctorRow(c) is { } blocks)
            {
                rows.Add(blocks);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Add("classifier blocks (7 d)", () => new Check("warn", $"check failed: {e.Message}"));
        }

        Add("work-roots", () => WorkRoots(c));
        Add("tree-ownership", () => TreeOwnershipRow(c));
        Add("registrations", () => Registrations(c));
        Add("codex-grants", () => CodexGrants(c));
        Add("state-db", () => StateDbRow(c));
        Add("ahd-heal", () => AhdHealRow(c));
        Add("ahd-build", () => AhdBuildRow(c));
        List<string> paused = [];
        try
        {
            paused = PausedLanes(c);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Add("paused-lane", () => new Check("warn", $"paused lanes could not be read: {e.Message}"));
        }

        foreach (var detail in paused)
        {
            Add("paused-lane", () => new Check("ok", detail));
        }

        if (c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile).Member("members") is { IsArray: true } members)
        {
            foreach (var member in members.AsArray())
            {
                if (MemberPlanning.Text(member.Member("kind")) == "codex" && member.Member("adapter_note") is { IsText: true } note)
                {
                    Add("codex-adapter", () => new Check("warn", $"{MemberPlanning.JsConvertText(member.Member("name"))}: {note.AsString()}"));
                }
            }
        }

        Add("hook-errors", () => HookErrors(c));
        Add("rejected-requests", () => RejectedRequests(c));
        Add("hooks-syntax", () => Compatible(c, root));
        Add("marketplace", () => Marketplace(c, root));
        Add("ux-skills", () => UxSkills(c));
        foreach (var report in c.Services.Doctor?.TrustFiles() ?? [])
        {
            Add(report.File == TrustFile.Homes ? "trusted-homes" : "trusted-hierarchy-dirs", () => TrustRow(report));
        }

        Add("statusline-tap", () => StatuslineTap.Row(c));
        if (AhV1Row(c) is { } v1)
        {
            Add("ah-v1", () => v1);
        }

        // Keys that name or lay out a team from config do nothing; this is where a person reading output learns so, and only
        // when there is something to say.
        var config = c.Services.Config;
        var stale = config.StaleTeamKeys(c.Cwd, c.Registry).Get("warnings") is { IsArray: true } w
            ? w.AsArray().Select(x => MemberPlanning.JsConvertText(x)).ToList()
            : [];
        if (stale.Count > 0)
        {
            rows.Add(new JsonObject { ["name"] = "stale-config-keys", ["status"] = "warn", ["detail"] = string.Join(' ', stale) });
        }

        // A relay that was sent and never shown stays out of the plugin prompt's way on purpose; say so only when there is one.
        if (c.Services.Doctor is { } relays && relays.HeldRelays() is { Count: > 0 } heldRelays)
        {
            rows.Add(new JsonObject
            {
                ["name"] = "held-relays",
                ["status"] = "warn",
                ["detail"] = "sent, not acknowledged (`ah msg release <entry>` hands it to the plugin prompt, and a message that was only delayed then shows twice): " + string.Join("; ", heldRelays),
            });
        }

        if (c.Services.Doctor is { } restart && restart.DaemonRestartNote() is { } restartNote)
        {
            rows.Add(new JsonObject { ["name"] = "daemon-contract", ["status"] = "red", ["detail"] = restartNote });
            red.Add("daemon-contract");
        }
        else if (c.Services.Doctor is { } unanswered && unanswered.DaemonUnansweredNote() is { } unansweredNote)
        {
            rows.Add(new JsonObject { ["name"] = "daemon-contract", ["status"] = "warn", ["detail"] = unansweredNote });
        }
        else if (c.Services.Doctor is { } down && down.DaemonDown())
        {
            // ah2-616 D4: the lowest severity, so a stopped daemon never changes the exit code; D5: AH_DAEMON=off gets no start hint
            var detail = c.Services.Environment.GetVariable("AH_DAEMON") == "off" ? "off (AH_DAEMON=off)" : "not running; doctor did not start one (start it with ahd start)";
            rows.Add(new JsonObject { ["name"] = "daemon", ["status"] = "ok", ["detail"] = detail });
        }

        if (c.Services.Doctor is { } readModel && readModel.DaemonReadModelNote() is { } readModelNote)
        {
            rows.Add(new JsonObject { ["name"] = "read-model", ["status"] = "ok", ["detail"] = readModelNote });
        }

        if (c.Services.Doctor is { } doctor)
        {
            try
            {
                if (doctor.ConventionsNote(c.Cwd) is { } note)
                {
                    rows.Add(new JsonObject { ["name"] = "conventions", ["status"] = "warn", ["detail"] = note });
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                rows.Add(new JsonObject { ["name"] = "conventions", ["status"] = "warn", ["detail"] = $"check failed: {e.Message}" });
            }
        }

        // Only when the file is there: a crashed install leaves the rebuild lock behind, and a rebuild started from a hook has
        // nowhere to show its warning.
        try
        {
            if (RebuildLock(c) is { } held)
            {
                rows.Add(new JsonObject { ["name"] = "rebuild-lock", ["status"] = held.Status, ["detail"] = held.Detail });
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            rows.Add(new JsonObject { ["name"] = "rebuild-lock", ["status"] = "warn", ["detail"] = $"check failed: {e.Message}" });
        }

        // Only when broken: every template verb and create refuses such a selection until it is fixed. Owning several live
        // teams, each team's selection is checked, and a broken one names its team.
        var pid = c.OrchestratorPid();
        var options = new ResolveOptions(Pid: double.IsNaN(pid) ? null : (JsonValue?)JsonValue.FromNumber(pid));
        var problems = new List<string>();
        var perTeam = config.OwnedRosterSelections(c.Cwd, options);
        if (perTeam is not null)
        {
            foreach (var entry in perTeam)
            {
                var team = entry.Member("team") is { IsText: true } t ? t.AsString() : null;
                if (entry.Member("selection") is { IsMap: true } selection
                    && config.RosterSelectionProblem(c.Cwd, selection.AsObject()) is { IsTruthy: true } problem)
                {
                    problems.Add($"team {Ah.Roster.RosterModule.TeamArgName(team)}: {MemberPlanning.JsConvertText(problem)}");
                }
            }
        }
        else if (config.RosterSelectionProblem(c.Cwd, config.SessionRosterSelection(c.Cwd, options)) is { IsTruthy: true } single)
        {
            problems.Add(MemberPlanning.JsConvertText(single));
        }

        if (problems.Count > 0)
        {
            rows.Add(new JsonObject { ["name"] = "roster-selection", ["status"] = "red", ["detail"] = string.Join(' ', problems) });
            red.Add("roster-selection");
        }

        try
        {
            if (CharterResponseRow(c) is { } charter)
            {
                rows.Add(new JsonObject { ["name"] = "charter-response-early", ["status"] = charter.Status, ["detail"] = charter.Detail });
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            rows.Add(new JsonObject { ["name"] = "charter-response-early", ["status"] = "warn", ["detail"] = $"check failed: {e.Message}" });
        }

        try
        {
            if (StreamEmptyDoctor.Row(c) is { } emptyRow)
            {
                rows.Add(new JsonObject { ["name"] = "stream-empty", ["status"] = emptyRow.Status, ["detail"] = emptyRow.Detail });
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            rows.Add(new JsonObject { ["name"] = "stream-empty", ["status"] = "warn", ["detail"] = $"check failed: {e.Message}" });
        }

        try
        {
            if (ClaudeEnvDoctor.Row(c) is { } claudeEnvRow)
            {
                rows.Add(new JsonObject { ["name"] = "claude-env", ["status"] = claudeEnvRow.Status, ["detail"] = claudeEnvRow.Detail });
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            rows.Add(new JsonObject { ["name"] = "claude-env", ["status"] = "warn", ["detail"] = $"check failed: {e.Message}" });
        }

        try
        {
            if (MaxLanesDoctor.Row(c) is { } maxLanesRow)
            {
                rows.Add(new JsonObject { ["name"] = "max-lanes", ["status"] = maxLanesRow.Status, ["detail"] = maxLanesRow.Detail });
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            rows.Add(new JsonObject { ["name"] = "max-lanes", ["status"] = "warn", ["detail"] = $"check failed: {e.Message}" });
        }

        try
        {
            foreach (var (lentName, lentStatus, lentDetail) in LentMemberDoctor.Rows(c))
            {
                rows.Add(new JsonObject { ["name"] = lentName, ["status"] = lentStatus, ["detail"] = lentDetail });
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            rows.Add(new JsonObject { ["name"] = "lent-member", ["status"] = "warn", ["detail"] = $"check failed: {e.Message}" });
        }

        try
        {
            if (MemberLaunchDoctor.Row(c) is { } launchRow)
            {
                rows.Add(new JsonObject { ["name"] = "launched-outside-ah", ["status"] = launchRow.Status, ["detail"] = launchRow.Detail });
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            rows.Add(new JsonObject { ["name"] = "launched-outside-ah", ["status"] = "warn", ["detail"] = $"check failed: {e.Message}" });
        }

        try
        {
            if (MemberTmpDoctor.Row(c) is { } tmpRow)
            {
                rows.Add(new JsonObject { ["name"] = "member-tmp", ["status"] = tmpRow.Status, ["detail"] = tmpRow.Detail });
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            rows.Add(new JsonObject { ["name"] = "member-tmp", ["status"] = "warn", ["detail"] = $"check failed: {e.Message}" });
        }

        try
        {
            if (CoreBareDoctor.Row(c) is { } bareRow)
            {
                rows.Add(new JsonObject { ["name"] = "core-bare", ["status"] = bareRow.Status, ["detail"] = bareRow.Detail });
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            rows.Add(new JsonObject { ["name"] = "core-bare", ["status"] = "warn", ["detail"] = $"check failed: {e.Message}" });
        }

        try
        {
            if (CodexTrustRow(c) is { } trustRow)
            {
                rows.Add(trustRow);
            }

            if (CodexPreapproval.AutoAnswersSince(c.Services.Environment, c.Services.Clock.UtcNow, 7) is > 0 and var answered)
            {
                rows.Add(new JsonObject
                {
                    ["name"] = "codex-trust-autoanswer",
                    ["status"] = "warn",
                    ["detail"] = $"the Codex trust screen was auto-answered {answered} time(s) in the last 7 days, so the setup step missed that root; see ah roster codex-trust --list",
                });
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            rows.Add(new JsonObject { ["name"] = "codex-trust", ["status"] = "warn", ["detail"] = $"check failed: {e.Message}" });
        }

        if (fixes is not null)
        {
            try
            {
                if (c.Services.Doctor?.AhdBuildFix() is { } buildFix)
                {
                    fixes.Add(buildFix);
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                fixes.Add($"ahd-build: fix failed: {(string.IsNullOrEmpty(e.Message) ? e.GetType().Name : e.Message)}");
            }
        }

        if (c.Opts.IsTrue("brief"))
        {
            foreach (var row in rows)
            {
                var status = MemberPlanning.JsConvertText(row.Member("status"));
                var name = MemberPlanning.JsConvertText(row.Member("name"));
                c.Stdout.WriteLine(status == "ok" ? $"ok {name}" : $"{status} {name}: {MemberPlanning.JsConvertText(row.Member("detail"))}");
            }

            return c.Opts.IsTrue("check") && red.Count > 0 ? 1 : 0;
        }

        var output = new JsonObject { ["cwd"] = c.Cwd, ["rows"] = rows, ["red"] = red };
        if (fixes is not null)
        {
            output["fix"] = fixes;
        }

        c.Out(output);
        return c.Opts.IsTrue("check") && red.Count > 0 ? 1 : 0;
    }

    /// <summary>
    /// A warn row when a harness member of the default roster would launch in a directory Codex does not trust. Never red, and
    /// <c>--fix</c> never grants it: the row's <c>fix</c> names the command and the user runs it.
    /// </summary>
    private static JsonObject? CodexTrustRow(RosterContext c)
    {
        var resolved = c.Services.Config.ResolveRoster(c.Cwd, c.RosterArg, c.RepoBasename, c.Registry);
        if (!resolved.IsMap || resolved.Member("members") is not { IsArray: true } members)
        {
            return null;
        }

        var needs = CodexTrustNeeds.For(c, members.AsArray());
        if (needs.Count == 0)
        {
            return null;
        }

        var first = MemberPlanning.Obj(needs[0]);
        return new JsonObject
        {
            ["name"] = "codex-trust",
            ["status"] = "warn",
            ["detail"] = $"Codex does not trust {MemberPlanning.Text(first, "root")}: a Codex member launched there stops at the trust screen",
            ["fix"] = first,
        };
    }

    /// <summary>
    /// <c>--fix</c>, the only thing that makes doctor write: it prunes the tap's old files, and installs or removes the tap when
    /// <c>--statusline-tap on|off</c> is given. A plain <c>--fix</c> never installs the tap.
    /// </summary>
    private static JsonArray Fix(RosterContext c)
    {
        var done = new JsonArray();
        var tap = c.Opts.Text("statusline-tap");
        if (c.Opts.Has("statusline-tap") && tap is not ("on" or "off"))
        {
            throw new RosterFailure("--statusline-tap takes on or off");
        }

        var pruned = StatuslineTap.Prune(c);
        if (pruned > 0)
        {
            done.Add($"pruned {pruned} statusline tap file(s) older than 7 days");
        }

        if (tap is not null)
        {
            done.Add(StatuslineTap.Apply(c, tap));
        }

        return done;
    }

    private static string OwnRoot(RosterContext c) =>
        c.Services.Environment.Plugin?.Root is { Length: > 0 } root
            ? root
            : c.PluginRoot;

    private static string PluginVersion(RosterContext c) =>
        c.Services.Environment.Plugin?.Version is { Length: > 0 } v ? v : MemberPlanning.JsConvertText(c.Services.Config.PluginVersion());

    private static string RealPath(RosterContext c, string path)
    {
        try
        {
            return c.Services.Files.RealPath(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Path.GetFullPath(path);
        }
    }

    // ---------------------------------------------------------------- rows

    private static Check Install(RosterContext c, string root)
    {
        string? recorded;
        var file = LifecyclePermissions.InstalledPluginsPath(c.Services.Environment);
        try
        {
            recorded = RecordedInstallPath(c, file);
        }
        catch (Exception e) when (e is IOException or JsonParseException or UnauthorizedAccessException)
        {
            var cached = root.Contains(Path.Combine(".claude", "plugins", "cache"), StringComparison.Ordinal);
            var message = e is FileNotFoundException or DirectoryNotFoundException
                ? $"ENOENT: no such file or directory, open '{file}'"
                : e.Message;
            return cached
                ? new Check("warn", $"installed_plugins.json unreadable ({message}) while running from the plugin cache")
                : new Check("ok", $"installed_plugins.json unreadable ({message}); running from a local checkout at {root}");
        }

        if (recorded is null)
        {
            return new Check("warn", $"no {c.Services.Config.PluginName}@… install record found; own root {root}");
        }

        return RealPath(c, recorded) == RealPath(c, root)
            ? new Check("ok", $"installed path matches own root: {root}")
            : new Check("warn", $"stale root in use — installed at {recorded}, running from {root}; take the most recent `ah CLI root` line");
    }

    /// <summary>The installPath the plugin manager records for this plugin, or null when nothing readable says.</summary>
    private static string? RecordedInstallPath(RosterContext c, string file)
    {
        var raw = JsonText.Parse(c.Services.Files.ReadText(file));
        if (!(raw.IsMap && raw.Member("plugins") is { IsMap: true } plugins))
        {
            return null;
        }

        foreach (var key in plugins.AsObject().Keys)
        {
            if (!key.StartsWith(c.Services.Config.PluginName + "@", StringComparison.Ordinal))
            {
                continue;
            }

            // The value is an array of install records, one per marketplace that supplies the plugin.
            var value = plugins.AsObject()[key];
            var first = value.IsArray ? (value.AsArray().Count > 0 ? value.AsArray()[0] : default) : value;
            return first.IsMap && first.Member("installPath") is { IsText: true } p ? p.AsString() : null;
        }

        return null;
    }

    private static Check Identity(RosterContext c)
    {
        var raw = c.Env("CLAUDE_PID");
        if (string.IsNullOrEmpty(raw))
        {
            return new Check("red", "CLAUDE_PID unset — every write verb must be given --orchestrator-pid <pid>");
        }

        var pid = JsConvert.ToNumber(JsonValue.FromString(raw));
        if (!RosterContext.IsInteger(pid))
        {
            return new Check("red", $"CLAUDE_PID is not an integer: {JsonText.Quote(raw)}");
        }

        var shown = JsConvert.ToJsString(JsonValue.FromNumber(pid));
        return c.PidAlive(pid) ? new Check("ok", $"CLAUDE_PID {shown}, alive") : new Check("red", $"CLAUDE_PID {shown} names no live process");
    }

    private static Check RuntimeDir(RosterContext c)
    {
        var dir = c.Dir;
        var root = c.Services.Config.FindGitRoot(c.Cwd);
        // A worktree's .git is a file pointing at the real git dir; state resolution differs there.
        var worktree = root is not null && c.Services.Files.Stat(Path.Combine(root, ".git"))?.Kind == FileKind.File;
        var note = worktree ? " (cwd is a git worktree: .git is a file)" : string.Empty;
        var stat = c.Services.Files.Stat(dir);
        if (stat is null)
        {
            return new Check("ok", $"{dir} does not exist yet — created at session start or by the first project state write{note}");
        }

        // owner, group or other write bit: the effective-uid check of access(2) needs the user id, which is not read here
        return (stat.Value.Mode & 0x92) == 0
            ? new Check("red", $"{dir} exists but is not writable{note}")
            : new Check("ok", $"{dir} exists and is writable{note}");
    }

    /// <summary>Whether this build's <c>dismiss</c> and <c>disband --close</c> hold members that have not released their work.</summary>
    internal static Check ReleaseHandshakeRow() => new("ok", "dismiss and disband --close hold members that have not released their work");

    /// <summary>Whether the process table can be read, which every owner verb needs to prove who runs it.</summary>
    internal static Check OwnerProofRow(RosterContext c) =>
        AncestorKind(c) is { } kind && kind != NearestKind.Unavailable
            ? new Check("ok", "ancestry")
            : new Check("warn", "process table unavailable");

    /// <summary>What the process ancestry says about this command's caller; null when no reader is composed.</summary>
    internal static NearestKind? AncestorKind(RosterContext c)
    {
        var rowDirs = new[] { c.Dir, c.MainDir }.Where(d => !string.IsNullOrEmpty(d)).Select(d => d!).ToList();
        return c.Services.OwnerProof?.NearestAncestor(c.TeamHome, rowDirs).Kind;
    }

    private static Check ConfigRow(RosterContext c)
    {
        var config = c.Services.Config;
        var pid = c.OrchestratorPid();
        var resolved = config.ResolveConfig(c.Cwd, new ResolveOptions(Pid: double.IsNaN(pid) ? null : (JsonValue?)JsonValue.FromNumber(pid)));
        var report = c.Services.Doctor?.StatusReport(c.Cwd) ?? string.Empty;
        var reportLines = report.Split('\n');
        var detail = string.Join(" | ", reportLines.Take(4).Concat(reportLines.Where(static l => l.StartsWith("qa cadence:", StringComparison.Ordinal) || l.StartsWith("dispatch:", StringComparison.Ordinal)).Take(2)));
        if (!(resolved.Get("configured") is { IsTruthy: true }))
        {
            return new Check("warn", $"not configured — {detail}");
        }

        // Custom roles: an invalid row is excluded, and a row whose agent fails its class contract is unavailable. Either is a
        // warning, named here so it is visible without reading the directive.
        var problems = new List<string>();
        if (resolved.Get("excludedRoles") is { IsArray: true } excluded)
        {
            foreach (var e in excluded.AsArray())
            {
                problems.Add($"invalid custom role {MemberPlanning.JsConvertText(e.Member("name"))} ({MemberPlanning.JsConvertText(e.Member("reason"))})");
            }
        }

        var roles = resolved.Get("roles") is { IsMap: true } r ? r.AsObject() : new JsonObject();
        foreach (var role in config.RegistryRoles(resolved))
        {
            if (config.IsBuiltinRole(role) && !config.IsOverride(role, roles.Get(role)))
            {
                continue;
            }

            var v = config.ValidateRole(role, resolved);
            if (v.IsMap && config.HasContractErrors(v.Member("findings")))
            {
                problems.Add(
                    $"role {role} is unavailable: its agent fails the {config.RoleClass(role, resolved)} contract (ah roster role list)");
            }
        }

        // An unknown qa.cadence, or a string qa (ah2-154): the resolver's warning, named here; either counts as not configured.
        // An unknown dispatch (ah2-200) likewise, treated as "peers".
        if (resolved.Get("warnings") is { IsArray: true } warnings)
        {
            problems.AddRange(warnings.AsArray().Where(static w => w.IsText && (w.AsString().StartsWith("ah: qa ", StringComparison.Ordinal) || w.AsString().StartsWith("ah: qa.cadence ", StringComparison.Ordinal) || w.AsString().StartsWith("ah: dispatch ", StringComparison.Ordinal))).Select(static w => w.AsString()));
        }

        if (problems.Count > 0)
        {
            return new Check("warn", $"{detail} | {string.Join("; ", problems)}");
        }

        return new Check(resolved.Get("enabled") is { IsTruthy: true } ? "ok" : "warn", detail);
    }

    /// <summary>
    /// ah2-180 D2: the caller's team lead's oldest unread mail and the last pane wake's result, one line; null when its box holds no
    /// unread mail (or there is no team file or mailbox), so nothing is printed.
    /// </summary>
    internal static Check? LeadMailRow(RosterContext c)
    {
        // ah2-180 Amendment 3: a team under a user hold of any phase has nothing unread, and mail a hold drained is not unread.
        if (c.Services.Mailbox is not { } mailbox || !c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile).IsMap || c.Services.Roster.HoldOf(c.Dir, c.TeamFile) is not null)
        {
            return null;
        }

        var box = mailbox.BoxDirFor(c.Services.Roster.TeamPath(c.Dir, c.TeamFile), "orchestrator");
        var settled = mailbox.LadderSweep(box).Settled;
        var oldest = mailbox.ReadEntries(box).Entries
            .Where(static e => e.IsMap)
            .Where(e => e.Member("id") is { IsText: true } id && !mailbox.IsAcked(box, id.AsString())
                && !(settled.TryGetValue(id.AsString(), out var state) && state == "drained"))
            .Select(static e => e.Member("created") is { IsText: true } at && Ah.Core.Time.Timestamps.TryParseMs(at.AsString(), out var ms) ? ms : double.NaN)
            .Where(double.IsFinite)
            .DefaultIfEmpty(double.NaN)
            .Min();
        if (!double.IsFinite(oldest))
        {
            return null;
        }

        var minutes = (int)((c.Services.Clock.UtcNow.ToUnixTimeMilliseconds() - oldest) / 60_000);
        var wake = mailbox.ReadAttempts(box).LastOrDefault(static r => r.Get("state") is { IsText: true } s && s.AsString() == "doorbell-wake");
        var last = wake is null ? "none"
            : MemberPlanning.JsConvertText(wake.Get("outcome") ?? JsonValue.Null) + (wake.Get("reason") is { IsText: true } why ? $" ({why.AsString()})" : string.Empty);
        return new Check("ok", string.Create(System.Globalization.CultureInfo.InvariantCulture, $"oldest unread {minutes} min; last wake: {last}"));
    }

    private static Check Team(RosterContext c)
    {
        var dir = c.Dir;
        var roster = c.Services.Roster;
        var config = c.Services.Config;
        var messages = c.Services.Hierarchy.Messages;
        var owned = roster.OwnedTeams(dir, c.Invoker());
        var known = new HashSet<string>(config.RegistryRoles(c.Registry), StringComparer.Ordinal);
        if (owned.Count > 1)
        {
            // Owning several live teams: every one is named, with the roster it was built from.
            var parts = new List<(int Orphans, string Text)>();
            foreach (var name in owned)
            {
                var t = roster.ReadTeamRecord(dir, name);
                var orphans = t.Member("members") is { IsArray: true } ms
                    ? ms.AsArray().Count(m => m.IsMap && m.Member("role") is { IsTruthy: true } role && !known.Contains(MemberPlanning.JsConvertText(role)))
                    : 0;
                var rosterName = t.Member("roster") is { IsNull: false } rr ? MemberPlanning.JsConvertText(rr) : "default";
                var teamId = t.Member("team_id") is { IsTruthy: true } tid ? MemberPlanning.JsConvertText(tid) : "?";
                parts.Add((orphans, $"{Ah.Roster.RosterModule.TeamArgName(name)} (roster {rosterName}, team_id {teamId}{(orphans > 0 ? $", {orphans} member(s) with a role not defined here" : string.Empty)})"));
            }

            return new Check(
                parts.Any(p => p.Orphans > 0) ? "warn" : "ok",
                $"this session owns {owned.Count} live teams: {string.Join("; ", parts.Select(p => p.Text))}");
        }

        var team = roster.ReadTeamRecord(dir, c.TeamFile);
        if (!team.IsMap)
        {
            return new Check("ok", $"no team file at {roster.TeamPath(dir, c.TeamFile)}");
        }

        var pidValue = team.Member("orchestrator")?.Member("pid");
        var alive = pidValue is { IsNumber: true } pn && RosterContext.IsInteger(pn.AsNumber()) && c.PidAlive(pn.AsNumber());
        var age = team.Member("created_at") is { IsTruthy: true } created
            ? $"{messages.FmtAge(JsonValue.FromNumber(messages.AgeSecOf(created)))} ago"
            : "unknown age";
        var live = roster.TeamIsLive(team, null, c.Services.Clock.UtcNow);
        var orphanRows = team.Member("members") is { IsArray: true } members
            ? members.AsArray().Where(m => m.IsMap && m.Member("role") is { IsTruthy: true } role && !known.Contains(MemberPlanning.JsConvertText(role))).ToList()
            : [];
        var orphanNote = orphanRows.Count > 0
            ? "; " + string.Join(
                "; ",
                orphanRows.Select(m =>
                    $"member {(m.Member("name") is { IsTruthy: true } n ? MemberPlanning.JsConvertText(n) : "(unnamed)")} has role \"{MemberPlanning.JsConvertText(m.Member("role"))}\", which is not defined here — it can still be dismissed or disbanded"))
            : string.Empty;
        var teamId2 = team.Member("team_id") is { IsTruthy: true } id2 ? MemberPlanning.JsConvertText(id2) : "?";
        var pidText = pidValue is { IsNull: false } pv ? MemberPlanning.JsConvertText(pv) : "none";
        return new Check(
            live && orphanRows.Count == 0 ? "ok" : "warn",
            $"team_id {teamId2} , orchestrator pid {pidText} {(alive ? "alive" : "dead")}, created {age}, live={(live ? "true" : "false")}{orphanNote}");
    }

    /// <summary>Live members that share a work root: a warning, or an info row when every sharer got the root from the same stream.</summary>
    private static Check WorkRoots(RosterContext c)
    {
        var warn = new List<string>();
        var info = new List<string>();
        var unknown = new List<string>();
        foreach (var team in c.Services.Roster.ListTeamNames(c.Dir))
        {
            foreach (var share in WorkTreeAssignment.SharedRoots(c, team, c.Services.Roster.ReadTeamRecord(c.Dir, team), unknownRoot: unknown))
            {
                var text = $"{share.Root} is shared by {string.Join(", ", share.Members)} in team {team}";
                if (share.Stream is { } stream)
                {
                    info.Add($"{text} (shared by stream {stream})");
                }
                else
                {
                    warn.Add(text);
                }
            }
        }

        var unknownText = unknown.Count > 0 ? $"; unknown root (no work_root in the grants): {string.Join(", ", unknown)}" : string.Empty;
        if (warn.Count > 0)
        {
            return new Check("warn", string.Join("; ", warn.Concat(info)) + unknownText);
        }

        return new Check("ok", (info.Count > 0 ? "info: " + string.Join("; ", info) : "no live members share a work root") + unknownText);
    }

    /// <summary>One info line per lane whose status record says paused: how long, who paused it and why. A paused lane is a decision, not a fault, so the row never warns.</summary>
    private static List<string> PausedLanes(RosterContext c)
    {
        var rows = new List<string>();
        foreach (var team in c.Services.Roster.ListTeamNames(c.Dir))
        {
            var record = c.Services.Roster.ReadTeamRecord(c.Dir, team);
            if (record.Member("parent") is not { IsMap: true } || record.Member("workstream") is not { IsMap: true } ws
                || ws.Member("state") is not { IsText: true } state || state.AsString() != "paused")
            {
                continue;
            }

            string Of(string key) => ws.Member(key) is { IsText: true } t ? t.AsString() : "?";
            var age = Of("updated") is var u && DateTimeOffset.TryParse(u, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at)
                ? $"{Math.Max(0, (long)(c.Services.Clock.UtcNow - at).TotalMinutes)}m"
                : "?";
            rows.Add($"info: lane {team} paused {age} by {Of("paused_by")}: {Of("note")}");
        }

        return rows;
    }

    private static Check Peers(RosterContext c)
    {
        var dir = c.Dir;
        var records = c.Services.Hierarchy.Roster.ReadRoster(dir);
        if (records.Count == 0)
        {
            return new Check("ok", "no roster records");
        }

        var byStatus = new List<(string Key, int Count)>();
        var live = 0;
        var dead = 0;
        foreach (var r in c.Services.Hierarchy.Roster.LatestRoster(dir))
        {
            var key = r.Member("status") is { IsTruthy: true } s ? MemberPlanning.JsConvertText(s) : "?";
            var at = byStatus.FindIndex(x => x.Key == key);
            if (at < 0)
            {
                byStatus.Add((key, 1));
            }
            else
            {
                byStatus[at] = (key, byStatus[at].Count + 1);
            }

            if (r.Member("pid") is { IsNumber: true } p && RosterContext.IsInteger(p.AsNumber()) && c.PidAlive(p.AsNumber()))
            {
                live++;
            }
            else
            {
                dead++;
            }
        }

        var counts = string.Join(' ', byStatus.Select(x => $"{x.Key}={x.Count}"));
        return new Check("ok", $"{records.Count} records; latest-per-slot: {counts}; {live} live pid(s), {dead} dead");
    }

    private const int UnregisteredListed = 5;

    /// <summary>
    /// Live members and Orchestrators of every named team here that the daemon holds no registration for. Never red: the fix is
    /// a <c>checkin</c> in that session, which this verb cannot do.
    /// </summary>
    private static Check Registrations(RosterContext c)
    {
        if (c.Services.Doctor is not { } doctor)
        {
            return new Check("ok", "every live member is registered");
        }

        var held = doctor.LiveRegistrationAddresses().ToHashSet(StringComparer.Ordinal);
        var dir = c.Dir;
        var roster = c.Services.Roster;
        var peers = c.Services.Hierarchy.Roster.LatestRoster(dir).ToList();
        var missing = new List<(string Team, string Text)>();
        foreach (var team in roster.ListTeamNames(dir))
        {
            var record = roster.ReadTeamRecord(dir, team);
            if (!record.IsMap)
            {
                continue;
            }

            if (record.Member("orchestrator") is { IsMap: true } orchestrator
                && orchestrator.Member("pid") is { IsNumber: true } opid && RosterContext.IsInteger(opid.AsNumber()) && c.PidAlive(opid.AsNumber())
                && !held.Contains(HierarchyModule.OrchestratorAddress(team)))
            {
                missing.Add((team, HierarchyModule.OrchestratorAddress(team)));
            }

            if (record.Member("members") is not { IsArray: true } members)
            {
                continue;
            }

            foreach (var member in members.AsArray())
            {
                if (member.Member("transport_id") is not { IsText: true } pane || pane.AsString().Length == 0
                    || member.Member("name") is not { IsText: true } name || name.AsString().Length == 0)
                {
                    continue;
                }

                if (global::Ah.Roster.RosterModule.StartingAgeSeconds(member, c.Services.Clock.UtcNow) is not null)
                {
                    continue;
                }

                var up = peers.Any(r =>
                    r.Member("status") is { IsText: true } st && st.AsString() == "up"
                    && r.Member("pane_id") is { IsText: true } rp && rp.AsString() == pane.AsString()
                    && r.Member("pid") is { IsNumber: true } p && RosterContext.IsInteger(p.AsNumber()) && c.PidAlive(p.AsNumber()));
                var address = $"{name.AsString()}@{team}";
                if (up && !held.Contains(address))
                {
                    missing.Add((team, $"{address} (pane {pane.AsString()})"));
                }
            }
        }

        if (missing.Count == 0)
        {
            return new Check("ok", "every live member is registered");
        }

        var listed = string.Join(", ", missing.Take(UnregisteredListed).Select(m => m.Text));
        var more = missing.Count > UnregisteredListed ? ", …" : string.Empty;
        return new Check(
            "warn",
            $"{missing.Count} live but unregistered: {listed}{more} — run `ah roster checkin --team {missing[0].Team}` in that session");
    }

    private static Check HookErrors(RosterContext c)
    {
        var config = c.Services.Config;
        var recent = config.RecentHookErrors(c.Services.Clock.UtcNow, 24);
        if (recent.Count == 0)
        {
            return new Check("ok", $"no hook errors in the last 24 h ({config.HookErrorLogPath})");
        }

        var last = recent.AsEnumerable().Skip(Math.Max(0, recent.Count - 5))
            .Select(e => $"{MemberPlanning.JsConvertText(e.Member("ts"))} {MemberPlanning.JsConvertText(e.Member("hook"))}: {MemberPlanning.JsConvertText(e.Member("message"))}")
            .ToList();
        return new Check("warn", $"{recent.Count} hook error(s) in 24 h — last {last.Count}: {string.Join(" || ", last)}");
    }

    private static Check RejectedRequests(RosterContext c)
    {
        var config = c.Services.Config;
        var recent = config.RecentRows(config.RejectedLogPath, c.Services.Clock.UtcNow, 24);
        if (recent.Count == 0)
        {
            return new Check("ok", $"no rejected daemon requests in the last 24 h ({config.RejectedLogPath})");
        }

        static string Text(JsonValue row, string key) => MemberPlanning.JsConvertText(row.Member(key));
        var last = recent.AsEnumerable().Skip(Math.Max(0, recent.Count - 5))
            .Select(e =>
            {
                var who = e.Member("caller") is { IsText: true } caller ? caller.AsString() : $"pid {Text(e, "peer_pid")}";
                return $"{Text(e, "ts")} {Text(e, "status")} {Text(e, "code")} {Text(e, "method")} {Text(e, "path")} {who}: {Text(e, "message")}";
            })
            .ToList();
        return new Check("warn", $"{recent.Count} rejected daemon request(s) in 24 h — last {last.Count}: {string.Join(" || ", last)}");
    }

    /// <summary>
    /// The native replacement for the hook files' syntax check: there are no scripts to parse, so the row says whether the binary
    /// that is running is the one this plugin's manifest names.
    /// </summary>
    private static Check Compatible(RosterContext c, string root)
    {
        var plugin = c.Services.Environment.Plugin;
        if (plugin is null)
        {
            return new Check("warn", $"no plugin manifest was read for {root}, so the running ahd cannot be compared with it");
        }

        return new Check("ok", $"ahd {plugin.Version} is compatible with the plugin at {root} (contract {plugin.Contract})");
    }

    /// <summary>One trust file: absent, ignored whole with the reason, or each entry trusted or ignored with its reason.</summary>
    private static Check TrustRow(TrustFileReport report)
    {
        if (!report.Present)
        {
            return new Check("ok", $"{report.Path} is absent: nothing is trusted from it");
        }

        if (report.IgnoredReason is { } reason)
        {
            return new Check("warn", $"{report.Path} is ignored: {reason}");
        }

        if (report.Entries.Count == 0)
        {
            return new Check("ok", $"{report.Path} has no entries");
        }

        var parts = report.Entries.Select(e => e.Trusted ? $"{e.Line} (trusted)" : $"{e.Line} (ignored: {e.Reason ?? "not trusted"})").ToList();
        var ignored = report.Entries.Count(e => !e.Trusted);
        return new Check(
            ignored > 0 ? "warn" : "ok",
            $"{report.Path}: {report.Entries.Count} entr{(report.Entries.Count == 1 ? "y" : "ies")}: {string.Join("; ", parts)}");
    }

    /// <summary>The install script's own bound: a lock older than this is stale.</summary>
    private const double RebuildLockStaleMs = 10 * 60 * 1000;

    /// <summary>
    /// The rebuild lock the install script keeps at <c>&lt;home&gt;/bin/.rebuild.lock</c>, or null when there is none. A fresh one
    /// is an install or rebuild at work; a stale one is warned about, because without flock(1) the script fails while its pid is alive.
    /// </summary>
    private static Check? RebuildLock(RosterContext c)
    {
        var path = Path.Combine(c.Services.Environment.AhHome, "bin", ".rebuild.lock");
        if (!c.Services.Files.Exists(path))
        {
            return null;
        }

        var at = double.NaN;
        var holder = "unknown";
        try
        {
            var doc = JsonText.Parse(c.Services.Files.ReadText(path));
            if (doc.Member("at") is { IsNumber: true } n)
            {
                at = n.AsNumber();
            }

            if (doc.Member("pid") is { IsNumber: true } p)
            {
                holder = JsConvert.ToJsString(p);
            }
        }
        catch (JsonParseException)
        {
            // an unreadable lock has no age, which the script also counts as stale
        }

        var ageMs = double.IsFinite(at) ? c.Services.Clock.UtcNow.ToUnixTimeMilliseconds() - at : double.PositiveInfinity;
        var age = double.IsFinite(ageMs) ? $"{Math.Max(0, Math.Floor(ageMs / 60000)).ToString("0", System.Globalization.CultureInfo.InvariantCulture)} min old" : "age unknown";
        if (ageMs < RebuildLockStaleMs)
        {
            return new Check("ok", $"{path} is held by an install or rebuild ({age}, pid {holder})");
        }

        return new Check(
            "warn",
            $"{path} is stale ({age}, pid {holder}): a crashed install leaves it; the next install takes it over once pid {holder} is gone, and where flock(1) is absent (macOS) fails while that pid is still running; if no install is running, remove it: rm {path}");
    }

    private static Check Marketplace(RosterContext c, string root)
    {
        var clone = Path.GetDirectoryName(root) ?? string.Empty;
        if (!c.Services.Files.Exists(Path.Combine(root, "..", ".git")))
        {
            return new Check("ok", "own root is not inside a git checkout — nothing to be dirty");
        }

        var porcelain = c.Services.Doctor?.GitPorcelain(clone) ?? string.Empty;
        var dirty = porcelain.Split('\n').Count(l => l.Length > 0);
        return dirty > 0
            ? new Check("warn", $"{clone} has {dirty} uncommitted change(s) — hooks run from this tree, so an unfinished edit is live")
            : new Check("ok", $"{clone} is clean");
    }
}
