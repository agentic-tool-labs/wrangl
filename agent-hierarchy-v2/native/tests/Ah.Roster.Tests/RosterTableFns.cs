using System.Reflection;
using Ah.Abstractions;
using Ah.Config;
using Ah.Hierarchy;
using Ah.Launch;
using Ah.Mesh;
using Ah.Core.Json;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Roster.Tests;

/// <summary>Calls the native roster function a unit table names, with the case's arguments.</summary>
internal static class RosterTableFns
{
    internal static readonly HashSet<string> All =
    [
        "resolveTeamByPane", "resolveMemberTeam", "teamMemberByName", "teamIsLive", "teamFileHome", "teamArgName", "teamListText",
        "validateMember", "validateTeamMember", "validateRosterBlock", "kindFieldErrors", "kindFieldWarnings", "kindAutoModeArgs",
        "memberArgs", "unmappedAdviseMessage", "codexTrust", "codexTrustRoot", "isComposer", "recognizeClaude", "recognizeScreen",
        "promptOptions", "screenHash",
        "readTeamFile", "readTeamWithDigest", "teamFileState", "teamHomeDir", "poolTeamPath","snapshotsHome", "listSnapshots",
        "readSnapshot", "teamMemberRenamedFrom", "teamMembersForRole", "teamMemberNameSet", "teamsWithMember", "memberNamePrefix",
        "legacyTeamPrefix", "teamRosterKey", "teamRosterEntry", "defaultTeamScope", "sessionMemberRow", "resolveSessionTeam",
        "envTeamFile", "attributeSessionTeam", "locateSession", "expectedRootFor", "splitTeamCopy", "teamOwnedBy", "historyPath",
        "readHistory", "fingerprint", "normalizeMembers", "historyEntryIsActive", "write", "clear", "read", "snapshot", "history",
        "links.limits", "links.readLinks", "links.covers", "links.linkLapseMs", "links.endReason", "links.judge",
        "links.activeCovering", "links.activeCount", "links.recentlyCapped", "links.read", "links.grant", "links.revoke", "links.use",
        "links.release", "links.extend", "links.pend", "links.sweep", "links.deny",
        "teamIntentPhrases", "matchedTeamIntentPhrase", "streamsOf", "streamMembers", "streamGlyph", "GLYPH_STATES", "streamGlyphState",
        "streamMemberStatuses", "streamFacts", "openTeamExchanges", "snapshotStamp", "snapshotTeamName", "planMembers", "topTierCount",
        "ROSTER_ROUTE_VALUES", "EFFORT_VALUES", "AUTO_MODE_VALUES", "ON_MISSING_VALUES", "ON_MISSING_DEFAULT", "ADVISE_TIERS",
        "SNAPSHOT_KEEP", "TEAM_STALE_AGE_SEC", "KIND_AUTO_MODE_ARGS",
        "KIND_HARNESS.codex.modelArgs", "KIND_HARNESS.codex.instructionsArgs", "KIND_HARNESS.codex.writableDirArgs",
        "KIND_HARNESS.codex.argsSetModel", "KIND_HARNESS.codex.nativeLegwork", "KIND_HARNESS.codex.approvalsArgs",
        "KIND_HARNESS.codex.approvalsKey", "KIND_HARNESS.codex.label", "KIND_HARNESS.codex.modelsCommand",
    ];

    private static readonly string NativeRoot = typeof(RosterTableFns).Assembly
        .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>().Single(a => a.Key == "AhNativeRoot").Value!;

    private static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var v in values)
        {
            array.Add(v);
        }

        return array;
    }

    /// <summary>The libraries whose unit tables belong to this assembly; a table of any other library is another assembly's, even when a function name repeats.</summary>
    internal static readonly HashSet<string> Libs = ["lib-roster", "lib-links", "lib-snapshot", "lib-streams", "lib-team-intent"];

    /// <summary>The dispatch key of a table: the function name, prefixed for the libraries whose names repeat another's.</summary>
    internal static string KeyOf(string lib, string fn) => lib == "lib-links" ? "links." + fn : fn;

    /// <summary>Whether this assembly dispatches the table: its library is one of ours and the function is in <see cref="All"/>.</summary>
    internal static bool OwnsTable(string lib, string fn) => Libs.Contains(lib) && All.Contains(KeyOf(lib, fn));

    internal static JsonValue? Dispatch(string lib, string fn, UnitCase c, UnitSandbox sandbox)
    {
        fn = KeyOf(lib, fn);
        var envVars = sandbox.Environment(c);
        var env = new FakeEnvironment(envVars);
        var io = PlatformModule.CreateIo(null);
        var cwd = sandbox.WorkingDirectory(c);

        JsonValue? Arg(int i) => i < c.Args.Count ? sandbox.Decode(c.Args[i]) : null;
        string? Text(int i) => Arg(i) is { IsText: true } t ? t.AsString() : null;

        IConfigCore config = null!;
        IRosterCore roster = null!;
        config = ConfigModule.CreateCore(io.Files, env, () => roster, new ProcessFacts(cwd, 4242));
        roster = RosterModule.CreateCore(io.Files, env, config, io.Processes, PlatformModule.CreateProcessTable(PlatformModule.CreateProcessRunner()));
        var clock = new FakeClock(c.Now is not null ? DateTimeOffset.Parse(c.Now, System.Globalization.CultureInfo.InvariantCulture) : DateTimeOffset.UtcNow) { Offset = TimeSpan.Zero };
        double ArgOrNow(int i) => Arg(i) is { IsNumber: true } n ? n.AsNumber() : clock.UtcNow.ToUnixTimeMilliseconds();
        var now = c.Now is not null ? DateTimeOffset.Parse(c.Now, System.Globalization.CultureInfo.InvariantCulture) : DateTimeOffset.UtcNow;

        switch (fn)
        {
            case "resolveTeamByPane":
            {
                var liveOnly = Arg(2)?.Member("liveOnly") is { IsTruthy: true };
                var match = roster.PaneResolver(Text(0)!, liveOnly)(Text(1));
                return match is null
                    ? JsonValue.Null
                    : new JsonObject
                    {
                        ["teamName"] = match.TeamName is null ? JsonValue.Null : match.TeamName,
                        ["team"] = match.Team,
                        ["member"] = match.Member,
                    };
            }

            case "resolveMemberTeam":
            {
                var r = roster.ResolveMemberTeam(Text(0)!, Text(1));
                return new JsonObject { ["found"] = r.Found, ["team"] = r.Team is null ? JsonValue.Null : r.Team };
            }

            case "teamMemberByName":
                return roster.TeamMemberByName(Text(0)!, Text(1), Text(2));
            case "teamIsLive":
                return roster.TeamIsLive(Arg(0) ?? JsonValue.Null, Invoker(Arg(1)), now);
            case "teamFileHome":
                return RosterModule.TeamFileHome(Text(0)!) is { } home ? home : JsonValue.Null;
            case "teamArgName":
                return RosterModule.TeamArgName(Text(0));
            case "teamListText":
                return RosterModule.TeamListText(Arg(0)!.Value.AsArray().Select(v => v.IsText ? v.AsString() : null));
            case "validateMember":
                return Strings(Rules(sandbox).ValidateMember(Arg(0), Resolved(Arg(1))));
            case "validateTeamMember":
                return Strings(Rules(sandbox).ValidateTeamMember(Arg(0), Resolved(Arg(1))));
            case "validateRosterBlock":
                return Strings(Rules(sandbox).ValidateRosterBlock(Arg(0), Resolved(Arg(1))));
            case "kindFieldErrors":
                return Strings(Rules(sandbox).KindFieldErrors(Arg(0), Resolved(Arg(1))));
            case "kindFieldWarnings":
            {
                var tier = !(Arg(2) is { IsMap: true } o && o.AsObject().Get("tier") is { IsBool: true } t && !t.AsBool());
                return Strings(Rules(sandbox).KindFieldWarnings(Arg(0), Resolved(Arg(1)), tier));
            }

            case "kindAutoModeArgs":
                return Rules(sandbox).KindAutoModeArgs(Arg(0)) is { } flags ? Strings(flags) : JsonValue.Null;
            case "memberArgs":
                return Rules(sandbox).MemberArgs(Arg(0)) is { } args ? args : JsonValue.Null;
            case "unmappedAdviseMessage":
                return Rules(sandbox).UnmappedAdviseMessage(Text(0)!);
            case "codexTrust":
                return Trust(io, env, config).Check(Text(0)!);
            case "codexTrustRoot":
                return Trust(io, env, config).TrustRoot(Text(0)!);
            case "isComposer":
                return Screens.IsComposer(Text(0)!, Text(1));
            case "recognizeClaude":
                return Screens.RecognizeClaude(Text(0));
            case "recognizeScreen":
                return Screens.RecognizeScreen(Text(0)!, Text(1), Text(2));
            case "promptOptions":
                return Screens.PromptOptions(Text(0)!, Text(1)!, Arg(2), Arg(3));
            case "screenHash":
                return Screens.ScreenHash(Text(0));
            case "ROSTER_ROUTE_VALUES":
                return Strings(RosterModule.RouteValues);
            case "EFFORT_VALUES":
                return Strings(RosterModule.EffortValues);
            case "AUTO_MODE_VALUES":
                return Strings(RosterModule.AutoModeValues);
            case "ON_MISSING_VALUES":
                return Strings(RosterModule.OnMissingValues);
            case "ON_MISSING_DEFAULT":
                return RosterModule.OnMissingDefault;
            case "ADVISE_TIERS":
                return Strings(RosterModule.AdviseTiers);
            case "SNAPSHOT_KEEP":
                return RosterModule.SnapshotKeep;
            case "TEAM_STALE_AGE_SEC":
                return RosterModule.TeamStaleAgeSeconds;
            case "KIND_AUTO_MODE_ARGS":
            {
                var all = new JsonObject();
                foreach (var (kind, table) in RosterModule.KindAutoModeArgs)
                {
                    var perMode = new JsonObject();
                    foreach (var (mode, flags2) in table)
                    {
                        perMode[mode] = Strings(flags2);
                    }

                    all[kind] = perMode;
                }

                return all;
            }

            case "KIND_HARNESS.codex.modelArgs":
                return Strings(Harness.ModelArgs(Text(0)!));
            case "KIND_HARNESS.codex.instructionsArgs":
                return Strings(Harness.InstructionsArgs(Text(0)!));
            case "KIND_HARNESS.codex.writableDirArgs":
                return Strings(Harness.WritableDirArgs(Text(0)!));
            case "KIND_HARNESS.codex.argsSetModel":
                return Harness.ArgsSetModel(Arg(0)!.Value.AsArray().ToList());
            case "KIND_HARNESS.codex.nativeLegwork":
                return Harness.NativeLegwork(Text(0));
            case "KIND_HARNESS.codex.approvalsArgs":
                return Strings(Harness.ApprovalsArgs);
            case "KIND_HARNESS.codex.approvalsKey":
                return Harness.ApprovalsKey;
            case "KIND_HARNESS.codex.label":
                return Harness.Label;
            case "KIND_HARNESS.codex.modelsCommand":
                return Harness.ModelsCommand;
            case "readTeamFile":
                return roster.ReadTeamFileAt(Text(0)!);
            case "readTeamWithDigest":
                return roster.ReadTeamWithDigest(Text(0)!);
            case "teamFileState":
                return roster.TeamFileState(Text(0)!, Text(1));
            case "teamHomeDir":
                return roster.TeamHome(Text(0)!);
            case "poolTeamPath":
                return roster.PoolTeamPath(Text(0)!, Text(1));
            case "snapshotsHome":
                return roster.SnapshotsHome(Text(0)!);
            case "listSnapshots":
            {
                var list = new JsonArray();
                list.AddRange(roster.ListSnapshots(Text(0)!, Text(1)!).Select(o => (JsonValue)o));
                return list;
            }

            case "readSnapshot":
                return roster.ReadSnapshot(Text(0)!);
            case "teamMemberRenamedFrom":
                return roster.TeamMemberRenamedFrom(Text(0)!, Text(1), Text(2));
            case "teamMembersForRole":
                return roster.TeamMembersForRole(Text(0)!, Text(1), Text(2));
            case "teamMemberNameSet":
                return roster.TeamMemberNameSet(Text(0)!, Text(1));
            case "teamsWithMember":
                return roster.TeamsWithMember(Text(0)!, Arg(1)!.Value.AsArray().Select(v => v.IsText ? v.AsString() : null).ToList(), Text(2));
            case "memberNamePrefix":
                return roster.MemberNamePrefixOf(Arg(0), Arg(1)) is { } prefix ? prefix : JsonValue.Null;
            case "legacyTeamPrefix":
                return roster.LegacyTeamPrefix(Text(0)!) is { } legacy ? legacy : JsonValue.Null;
            case "teamRosterKey":
                return roster.TeamRosterKey(Text(0)!, Text(1)) is { } key ? key : JsonValue.Null;
            case "teamRosterEntry":
                {
                var rosterEntry = roster.TeamRosterEntry(Text(0)!, Text(1));
                if (rosterEntry is null)
                {
                    return null;
                }

                return rosterEntry.Key is null ? JsonValue.Null : rosterEntry.Key;
            }
            case "defaultTeamScope":
            {
                var suggested = c.Expect?.Member("suggested") is { IsText: true } sg ? sg.AsString() : null;
                return roster.DefaultTeamScope(Text(0)!, Arg(1) ?? JsonValue.Null, _ => suggested);
            }

            case "sessionMemberRow":
                return roster.SessionMemberRow(Arg(0) ?? JsonValue.Null, Text(1));
            case "resolveSessionTeam":
                return roster.ResolveSessionTeam(Text(0)!, Text(1), Text(2));
            case "envTeamFile":
                return roster.EnvTeamFileFor(StringList(Arg(0)), Text(1));
            case "attributeSessionTeam":
            {
                var o = Arg(2) is { IsMap: true } m ? m.AsObject() : new JsonObject();
                var homes = o.Get("homes") is { IsArray: true } h ? StringList(h) : null;
                return roster.AttributeSessionTeam(
                    Text(0)!,
                    Text(1),
                    o.Get("explicitTeam") is { IsText: true } et ? et.AsString() : null,
                    o.Get("paneId") is { IsText: true } pid ? pid.AsString() : null,
                    homes);
            }

            case "locateSession":
            {
                var o = Arg(0)!.Value.AsObject();
                return roster.LocateSession(
                    o["dir"].AsString(),
                    StringList(o["homes"]),
                    o.Get("teamArg") is { IsText: true } ta ? ta.AsString() : null,
                    o.Get("teamDefaultExplicit") is { IsTruthy: true },
                    o.Get("paneId") is { IsText: true } pn ? pn.AsString() : null);
            }

            case "expectedRootFor":
                return roster.ExpectedRootFor(Arg(0) ?? JsonValue.Null, Arg(1) ?? JsonValue.Null, Text(2));
            case "splitTeamCopy":
                return roster.SplitTeamCopy(Text(0)!, Text(1));
            case "teamOwnedBy":
            {
                var invoker = Invoker(Arg(1));
                var held = Arg(1) is { IsMap: true } inv && inv.Member("provenSessions") is { IsArray: true } list
                    ? list.AsArray().ToList().Where(static v => v.IsText).Select(static v => v.AsString()).ToList()
                    : null;
                var owner = held is null
                    ? roster
                    : RosterModule.CreateCore(
                        io.Files, env, config, io.Processes, PlatformModule.CreateProcessTable(PlatformModule.CreateProcessRunner()),
                        new SessionProofs(pid => invoker?.Pid == pid ? held : null, static _ => null));
                return invoker is not null && owner.IsTeamOwnedBy(Arg(0) ?? JsonValue.Null, invoker);
            }

            case "historyPath":
                return roster.HistoryPath(Text(0)!);
            case "readHistory":
                return roster.ReadHistory(Text(0)!);
            case "fingerprint":
            {
                var o = Arg(0)!.Value.AsObject();
                return roster.Fingerprint(o.Get("roster_level") ?? JsonValue.Null, o.Get("transport") ?? JsonValue.Null, o.Get("members"));
            }

            case "normalizeMembers":
                return Rules(sandbox).NormalizeMembers(Arg(0));
            case "historyEntryIsActive":
                return roster.HistoryEntryIsActive(Text(0)!, Arg(1) ?? JsonValue.Null, now);
            case "write":
            case "clear":
            case "read":
            case "snapshot":
            case "history":
            {
                var domain = RosterModule.CreateStateDomain(roster, clock, new SequenceRandom(), null);
                var op = domain.Ops.Single(x => x.Name == fn);
                var opArgs = JsonText.ToUtf8(Arg(0)!.Value, 0);
                var outcome = op.Body(
                    new OpCall(fn, opArgs, new RequestContext(cwd, cwd, cwd, cwd)),
                    op.Kind == OpKind.Write ? new DirectStoreWriteContext() : null);
                if (!outcome.Ok)
                {
                    throw new InvalidOperationException(outcome.Error);
                }

                return JsonText.Parse(outcome.ResultJson.Span);
            }

            case "teamIntentPhrases":
                return Strings(TeamIntentOf(io, env).Phrases());
            case "matchedTeamIntentPhrase":
                return TeamIntentOf(io, env).MatchedPhrase(Text(0)!) is { } phrase ? phrase : JsonValue.Null;
            case "streamsOf":
                return Streams(io, env, clock, roster).StreamsOf(Arg(0));
            case "streamMembers":
                return Streams(io, env, clock, roster).StreamMembers(Arg(0), Text(1)!);
            case "streamGlyph":
                return Streams(io, env, clock, roster).StreamGlyph(Text(0), Arg(1)!.Value.AsArray(), StringList(Arg(2)));
            case "GLYPH_STATES":
            {
                var states = new JsonObject();
                foreach (var (glyph, word) in Streams(io, env, clock, roster).GlyphStates)
                {
                    states[glyph] = word;
                }

                return states;
            }

            case "streamGlyphState":
                return Streams(io, env, clock, roster).StreamGlyphState(Text(0), Arg(1)!.Value.AsArray(), StringList(Arg(2)), !(Arg(3) is { IsBool: true } known && !known.AsBool()));
            case "streamMemberStatuses":
                return Streams(io, env, clock, roster).StreamMemberStatuses(Arg(0)!.Value.AsArray(), Arg(1) is { IsArray: true } agentList ? agentList.AsArray() : null, Arg(2));
            case "streamFacts":
                return Streams(io, env, clock, roster).StreamFacts(
                    Arg(0)!.Value,
                    Arg(1)!.Value.AsArray(),
                    Arg(2)!.Value.AsArray(),
                    Arg(3)!.Value.AsArray(),
                    Arg(4) is { IsArray: true } agents ? agents.AsArray() : null);
            case "openTeamExchanges":
            {
                var opts = Arg(2) is { IsMap: true } om ? om.AsObject() : new JsonObject();
                var team = Arg(1);
                return Streams(io, env, clock, roster).OpenTeamExchanges(
                    Text(0)!,
                    team is { IsText: true } tn ? tn.AsString() : null,
                    allTeams: team is null,
                    created: opts.Get("created") is { IsTruthy: true });
            }

            case "snapshotStamp":
                return Snapshots(io).SnapshotStamp(Arg(0) is { IsText: true } iso ? DateTimeOffset.Parse(iso.AsString(), System.Globalization.CultureInfo.InvariantCulture) : now);
            case "snapshotTeamName":
                return Snapshots(io).SnapshotTeamName(Text(0));
            case "planMembers":
                return Snapshots(io).PlanMembers(Arg(0) ?? JsonValue.Null, id => FakePsAliveReason(sandbox, id));
            case "topTierCount":
            {
                var facts = new FactsRegistry(NativeRoot, sandbox.Home);
                return Snapshots(io).TopTierCount(Arg(0)!.Value.AsArray(), facts.TierOf, facts.TopTier);
            }

            case "links.limits":
                return LinksOf(io, env, clock, roster, config).Limits();
            case "links.readLinks":
                return LinksOf(io, env, clock, roster, config).ReadLinks(Text(0)!, Text(1)!);
            case "links.covers":
                return LinksOf(io, env, clock, roster, config).Covers(Arg(0) ?? JsonValue.Null, Arg(1), Arg(2));
            case "links.linkLapseMs":
                return LinksOf(io, env, clock, roster, config).LinkLapseMs(Arg(0) ?? JsonValue.Null);
            case "links.endReason":
                return LinksOf(io, env, clock, roster, config).EndReason(Arg(0) ?? JsonValue.Null, Text(1)!, Text(2)!, Arg(3)!.Value.AsNumber()) is { } why ? why : JsonValue.Null;
            case "links.judge":
                return LinksOf(io, env, clock, roster, config).Judge(Text(0)!, Text(1)!, Arg(2)!.Value.AsNumber());
            case "links.activeCovering":
                return LinksOf(io, env, clock, roster, config).ActiveCovering(Text(0)!, Text(1)!, Arg(2), Arg(3), ArgOrNow(4));
            case "links.activeCount":
                return LinksOf(io, env, clock, roster, config).ActiveCount(Text(0)!, Text(1)!, Text(2)!, ArgOrNow(3));
            case "links.recentlyCapped":
                return LinksOf(io, env, clock, roster, config).RecentlyCapped(Text(0)!, Text(1)!, Arg(2), Arg(3), ArgOrNow(4)) is { } capped ? capped : JsonValue.Null;
            case "links.read":
            case "links.grant":
            case "links.revoke":
            case "links.use":
            case "links.release":
            case "links.extend":
            case "links.pend":
            case "links.sweep":
            case "links.deny":
            {
                var op = fn["links.".Length..];
                var domain = LinksOf(io, env, clock, roster, config).StateDomain();
                var stateOp = domain.Ops.Single(x => x.Name == op);
                var call = new OpCall(op, JsonText.ToUtf8(Arg(0)!.Value, 0), new RequestContext(cwd, cwd, cwd, cwd));
                OpResult outcome;
                if (stateOp.Kind == OpKind.Write)
                {
                    // links writes are db-only (per-dir-files-to-db S11b): the DbBody runs in one transaction, as the engine runs it
                    var ran = Ah.StateDb.StateDbModule.CreateOpRunner(env.AhHome).Run(new DbOpRequest("links", Guid.NewGuid().ToString(), "direct", tx => stateOp.DbBody!(call, tx), DbOnly: true));
                    outcome = ran.Status == DbOpStatus.Done ? new OpResult(true, ran.ResultJson) : new OpResult(false, default, ran.Error);
                }
                else
                {
                    outcome = stateOp.Body(call, null);
                }
                if (!outcome.Ok)
                {
                    throw new InvalidOperationException(outcome.Error);
                }

                return JsonText.Parse(outcome.ResultJson.Span);
            }

            default:
                throw new NotSupportedException("no native dispatcher for " + fn);
        }
    }

    private static readonly IScreenRules Screens = RosterModule.CreateScreenRules();

    private static List<string> StringList(JsonValue? array) =>
        array is { IsArray: true } a ? a.AsArray().Where(v => v.IsText).Select(v => v.AsString()).ToList() : [];

    private static KindHarness Harness => KindHarness.Get("codex")!;

    /// <summary>
    /// The alive-reason of a session id as the Node library gives it with the case's fake <c>ps</c> (the script under <c>bin/</c> prints
    /// <c>ps.out</c>, or fails when <c>ps.fail</c> exists): the id on a line that also has a <c>claude</c> word, or doubt when it fails.
    /// </summary>
    private static string? FakePsAliveReason(UnitSandbox sandbox, JsonValue? session)
    {
        if (session is not { IsTruthy: true })
        {
            return null;
        }

        if (File.Exists(Path.Combine(sandbox.Root, "bin", "ps.fail")))
        {
            return "ps failed";
        }

        var outPath = Path.Combine(sandbox.Root, "bin", "ps.out");
        var text = File.Exists(outPath) ? File.ReadAllText(outPath) : string.Empty;
        var id = JsConvert.ToJsString(session);
        var claude = new System.Text.RegularExpressions.Regex("(^|[" + Ah.Core.Text.JsText.SpaceClass + "/])claude([" + Ah.Core.Text.JsText.SpaceClass + "]|\\z)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return text.Split('\n').Any(l => l.Contains(id, StringComparison.Ordinal) && claude.IsMatch(l)) ? "claude process" : null;
    }

    private sealed class NoWrites : IStateWriter
    {
        public StateResult Invoke(string domain, string op, ReadOnlyMemory<byte> argsJson, RequestContext context, StateCallOptions? options = null) =>
            throw new NotSupportedException("the links library never writes through the hierarchy services");

        public StateUpdateResult Update(string domain, StateUpdatePlan plan, RequestContext context, StateCallOptions? options = null) =>
            throw new NotSupportedException("the links library never writes through the hierarchy services");
    }

    private static Links LinksOf(PlatformIo io, FakeEnvironment env, IClock clock, IRosterCore roster, IConfigCore config)
    {
        var hierarchy = HierarchyModule.Create(new HierarchyEnvironment(io.Files, env, clock, new SequenceRandom(), io.Processes, config, roster, new NoWrites(), new ProcessFacts(NativeRoot, 4242)) { MsgsRoute = TestMsgStore.Route(env.AhHome) });
        // a home with a state database, as any home the daemon has run in: a planted links file is then taken on its first read
        _ = Ah.StateDb.StateDbModule.EnsureDatabase(env.AhHome);
        return new(Ah.StateDb.StateDbModule.CreateLinks(env.AhHome), env, roster, hierarchy.Core, hierarchy.Messages, clock, new SequenceRandom());
    }

    private static TeamIntent TeamIntentOf(PlatformIo io, IAhEnvironment env) =>
        new TeamIntent(io.Files, Path.GetFullPath(Path.Combine(NativeRoot, "..")));

    private static StreamRules Streams(PlatformIo io, IAhEnvironment env, IClock clock, IRosterCore roster) =>
        new StreamRules(io.Files, new ExchangeSource(io, env, clock));

    private static SnapshotRules Snapshots(PlatformIo io) => new SnapshotRules(io.Files, "claude");

    private static IMemberRules Rules(UnitSandbox sandbox) => RosterModule.CreateMemberRules(new FactsRegistry(NativeRoot, sandbox.Home));

    private static JsonObject? Resolved(JsonValue? v) => v is { IsMap: true } m ? m.AsObject() : null;

    private static ICodexTrust Trust(PlatformIo io, IAhEnvironment env, IConfigCore config) =>
        RosterModule.CreateCodexTrust(io.Files, env, cwd =>
        {
            var main = config.MainHierarchyDir(cwd);
            return main is not null ? Ah.Core.Text.NodePath.Dirname(Ah.Core.Text.NodePath.Dirname(main)) : config.FindGitRoot(cwd);
        });

    private static TeamInvoker? Invoker(JsonValue? raw)
    {
        if (raw is not { IsTruthy: true } inv)
        {
            return null;
        }

        var pid = JsConvert.AsInteger(inv.Member("pid"));
        var session = inv.Member("sessionId") is { IsText: true } s && s.AsString().Length > 0 ? s.AsString() : null;
        return new TeamInvoker(pid is >= int.MinValue and <= int.MaxValue ? (int)pid : null, session);
    }
}
