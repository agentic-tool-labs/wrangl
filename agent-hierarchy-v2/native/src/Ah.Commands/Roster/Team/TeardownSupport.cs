using System.Text.RegularExpressions;
using Ah.Core.Json;
using Ah.Launch;

namespace Ah.Commands;

/// <summary>
/// What the teardown verbs (<c>disband</c>, <c>dismiss</c>, <c>untrack</c>, <c>reap</c>) share: the team records a scope reads, the
/// close set, the plan token gate, and the shapes of the plan and close answers.
/// </summary>
internal static partial class TeardownSupport
{
    /// <summary>The scope of a teardown that reads no team file: herdr is the only registry a loose peer can be in.</summary>
    internal static readonly TeamScope NoTeamScope = new(null, NoTeam: true);

    /// <summary>The team record read for the resolved scope, the older-release copy beside it that joins the close set, and the files read.</summary>
    internal sealed record TeamRead(JsonValue Team, List<JsonValue> Extra, JsonArray Files);

    /// <summary>The members a teardown can close and those it cannot, with the live registry peers outside the team file.</summary>
    internal sealed record CloseSet(JsonArray Extras, List<JsonValue> Closable, JsonArray NotClosable, List<string> Excluded);

    internal static string TeamName(RosterContext c) => c.TeamFile ?? "(default)";

    internal static TeamScope ScopeOf(RosterContext c) => new(c.TeamFile);

    /// <summary>Whether a team record names an orchestrator: such a team is kept, empty, when its last member leaves (ah2-97).</summary>
    internal static bool NamesOrchestrator(JsonValue team) => team.Member("orchestrator") is { IsMap: true };

    // ---------------------------------------------------------------- the team records

    /// <summary>
    /// The team record(s) a teardown reads for the resolved scope: the file <c>TeamPath</c> resolves to, plus this pool's own copy when an
    /// older release left one beside the home's. The pool copy joins the close set only when this session owns it or its owner is gone.
    /// </summary>
    internal static TeamRead TeardownTeam(RosterContext c, string dir, List<string> warnings)
    {
        var roster = c.Services.Roster;
        var team = roster.ReadTeamRecord(dir, c.TeamFile);
        var split = roster.SplitTeamCopy(dir, c.TeamFile);
        var extra = new List<JsonValue>();
        if (split.IsMap)
        {
            warnings.Add($"Team {TeamName(c)} has records in both {roster.TeamHome(dir)} and {dir} (left by an older release). Both were read.");
            var splitTeam = split.Member("team") ?? JsonValue.Null;
            if (roster.IsTeamOwnedBy(splitTeam, c.Invoker()) || roster.TeamIsOrphaned(splitTeam))
            {
                extra.Add(split);
            }
            else
            {
                var owner = RosterJson.Member(splitTeam.Member("orchestrator"), "pid");
                var count = splitTeam.Member("members") is { IsArray: true } m ? m.AsArray().Count : 0;
                warnings.Add(
                    $"Team {TeamName(c)} in {MemberPlanning.JsConvertText(split.Member("path"))} belongs to another live orchestrator (pid {MemberPlanning.JsConvertText(owner)}). Its {count} members were left alone.");
            }
        }

        var files = new JsonArray();
        if (!team.IsNull)
        {
            files.Add(roster.TeamPath(dir, c.TeamFile));
            foreach (var x in extra)
            {
                files.Add(x.Member("path") ?? JsonValue.Null);
            }
        }

        return new TeamRead(team, extra, files);
    }

    private static string MemberKey(JsonValue m) =>
        m.Member("transport_id") is { IsNull: false } id ? $"p:{MemberPlanning.JsConvertText(id)}" : $"n:{MemberPlanning.JsConvertText(m.Member("name"))}";

    /// <summary><paramref name="team"/> with the pool copy's members added, each pane or name once.</summary>
    internal static JsonValue MergedTeam(JsonValue team, List<JsonValue> extra)
    {
        if (extra.Count == 0)
        {
            return team;
        }

        var members = team.Member("members") is { IsArray: true } own ? own.AsArray().ToList() : [];
        var seen = new HashSet<string>(members.Select(MemberKey), StringComparer.Ordinal);
        var merged = new JsonArray();
        merged.AddRange(members);
        foreach (var x in extra)
        {
            if (x.Member("team")?.Member("members") is not { IsArray: true } theirs)
            {
                continue;
            }

            foreach (var m in theirs.AsArray())
            {
                if (seen.Add(MemberKey(m)))
                {
                    merged.Add(m);
                }
            }
        }

        var copy = team.AsObject().Clone();
        copy["members"] = merged;
        return copy;
    }

    // ---------------------------------------------------------------- the close set

    /// <summary>Members that hold a pane route but have nothing to close: no pane id, or a transport with no panes.</summary>
    internal static JsonArray NotClosableOf(RosterContext c, IEnumerable<JsonValue> members, string? transport)
    {
        var result = new JsonArray();
        foreach (var m in members)
        {
            if (MemberPlanning.HasPane(c, m.Member("route")) && (m.Member("transport_id") is not { IsNull: false } || transport == "terminal"))
            {
                var label = m.Member("name") is { IsNull: false } n ? n : m.Member("role") ?? JsonValue.Null;
                result.Add(new JsonObject { ["name"] = label, ["reason"] = "no pane" });
            }
        }

        return result;
    }

    /// <summary>W8: members a teardown cannot close from here, as a warnings entry (none when every member has a pane).</summary>
    internal static List<string> NotClosableWarnings(JsonArray notClosable)
    {
        if (notClosable.Count == 0)
        {
            return [];
        }

        var names = string.Join(", ", notClosable.Select(m => MemberPlanning.JsConvertText(m.Member("name"))));
        return
        [
            $"W8: {notClosable.Count} member{(notClosable.Count == 1 ? string.Empty : "s")} cannot be closed from here, because they have no pane: {names}. Close them by hand.",
        ];
    }

    /// <summary>The close set a team's teardown acts on, from the healed rows of every file read plus live registry peers.</summary>
    internal static CloseSet TeamCloseSet(RosterContext c, string dir, JsonValue eff, IReadOnlyList<JsonValue> healed)
    {
        var teardown = c.Services.Launch!.Teardown;
        var excluded = new List<string>();
        var extras = new JsonArray();
        extras.AddRange(WithoutInvoker(c, teardown.PeerExtras(dir, healed, ScopeOf(c), c.LaunchContext()), excluded));
        var all = healed.Concat(extras).ToList();
        var transport = MemberPlanning.Text(eff.Member("transport"));
        var closable = transport == "terminal" ? [] : teardown.ClosableMembers(all).ToList();
        return new CloseSet(extras, closable, NotClosableOf(c, all, transport), excluded);
    }

    /// <summary>
    /// Two rows can name one session (a nameless <c>up</c> row and a named <c>briefed</c> row), so the close set is deduplicated on
    /// <c>transport_id</c>, falling back to <c>session_id</c>. A row with neither is kept as its own entry.
    /// </summary>
    internal static List<JsonValue> DedupPeers(IEnumerable<JsonValue> members)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<JsonValue>();
        foreach (var m in members)
        {
            var key = m.Member("transport_id") is { IsTruthy: true } t ? MemberPlanning.JsConvertText(t)
                : m.Member("session_id") is { IsTruthy: true } s ? MemberPlanning.JsConvertText(s)
                : null;
            if (key is not null && !seen.Add(key))
            {
                continue;
            }

            result.Add(m);
        }

        return result;
    }

    // ---------------------------------------------------------------- the session running the command

    /// <summary>
    /// Whether <paramref name="m"/> is the session running this command (by its session id or its pane) or an orchestrator: neither
    /// is ever on a close list, because closing it would end the command's own session.
    /// </summary>
    internal static bool IsInvokerOrOrchestrator(RosterContext c, JsonValue m)
    {
        if (MemberPlanning.Text(m.Member("role")) == "orchestrator")
        {
            return true;
        }

        var session = c.EnvText("CLAUDE_CODE_SESSION_ID") ?? c.Opts.Text("session");
        if (session is not null && MemberPlanning.Text(m.Member("session_id")) == session)
        {
            return true;
        }

        var pane = c.SessionPaneId(null);
        return pane is not null && m.Member("transport_id") is { IsTruthy: true } id && MemberPlanning.JsConvertText(id) == pane;
    }

    /// <summary><paramref name="members"/> without the session running this command and any orchestrator; their names go to <paramref name="excluded"/>.</summary>
    internal static List<JsonValue> WithoutInvoker(RosterContext c, IEnumerable<JsonValue> members, List<string> excluded)
    {
        var kept = new List<JsonValue>();
        foreach (var m in members)
        {
            if (IsInvokerOrOrchestrator(c, m))
            {
                excluded.Add(MemberPlanning.JsConvertText(m.Member("name")));
            }
            else
            {
                kept.Add(m);
            }
        }

        return kept;
    }

    /// <summary>The warning that names what was left off a close list because it is the running session or an orchestrator, or nothing.</summary>
    internal static List<string> ExcludedWarnings(IReadOnlyList<string> excluded) => excluded.Count == 0
        ? []
        : [$"Left out of the close set because {(excluded.Count == 1 ? "it is" : "they are")} the session running this command or an orchestrator: {string.Join(", ", excluded)}."];

    // ---------------------------------------------------------------- peers without a team record

    /// <summary>How a peer identifies itself to <c>dismiss</c>: any of these values names it.</summary>
    internal static JsonObject PeerIdentifiers(JsonValue m) => new()
    {
        ["name"] = m.Member("name") ?? JsonValue.Null,
        ["pane_id"] = RosterJson.Truthy(m.Member("transport_id")),
        ["session_id"] = RosterJson.Truthy(m.Member("session_id")),
        ["role"] = RosterJson.Truthy(m.Member("role")),
    };

    internal static JsonObject PeerFallbackPlanEntry(JsonValue m)
    {
        var id = m.Member("transport_id");
        var entry = new JsonObject();
        Put(entry, "role", m.Member("role"));
        Put(entry, "name", m.Member("name"));
        Put(entry, "route", m.Member("route"));
        entry["transport"] = "herdr";
        Put(entry, "transport_id", id);
        entry["command"] = id is { IsTruthy: true } ? $"herdr pane close {MemberPlanning.JsConvertText(id)}" : JsonValue.Null;
        Put(entry, "live", m.Member("live"));
        Put(entry, "how", m.Member("how"));
        entry["source"] = m.Member("source") is { IsTruthy: true } src ? src : JsonValue.FromString("peers");
        if (m.Member("stray") is { IsTruthy: true })
        {
            entry["stray"] = true;
        }

        return entry;
    }

    private static void Put(JsonObject target, string key, JsonValue? value)
    {
        if (value is { } v)
        {
            target[key] = v;
        }
    }

    /// <summary>The error a name that resolves to several live peers is: every candidate is listed.</summary>
    internal static JsonValue? ResolvePeer(RosterContext c, List<JsonValue> fallback, string name, string? teamTransport)
    {
        var target = c.Services.Launch!.Teardown.ResolvePeerTarget(fallback, name, teamTransport, c.LaunchContext());
        if (target.Fail is not null)
        {
            throw new RosterFailure(target.Fail);
        }

        return target.Member;
    }

    // ---------------------------------------------------------------- warnings and summaries

    private static string ScopePrefix(RosterContext c, string? scope) =>
        !string.IsNullOrEmpty(scope) ? scope : c.Services.Config.TeamPrefix(c.Cwd, c.TeamArg);

    /// <summary>Residual risk of closing by name: spelled out in every plan and close that has strays.</summary>
    internal static string StrayWarning(RosterContext c, IReadOnlyList<string> strays) =>
        $"Closing {strays.Count} herdr agent{(strays.Count == 1 ? string.Empty : "s")} named `{ScopePrefix(c, c.TeamFile)}-<role>` that {(strays.Count == 1 ? "is" : "are")} not in the team record: {string.Join(", ", strays)}. " +
        "They are matched by name and checkout only. A live agent from another orchestrator in this repo or one of its worktrees, with the same name prefix, would be closed too.";

    /// <summary>Where the live sessions found without a team record came from, for the no-record warning.</summary>
    internal static string NoRecordWarning(RosterContext c, string dir, IReadOnlyList<JsonValue> found)
    {
        var via = found
            .Select(m => MemberPlanning.Text(m.Member("source")) == "herdr" ? "herdr agent list" : "peers.jsonl")
            .Distinct()
            .ToList();
        var home = c.Services.Roster.TeamHome(dir);
        return $"No team record for {TeamName(c)} in {home}{(home == dir ? string.Empty : $" or {dir}")}, but {found.Count} live member{(found.Count == 1 ? " was" : "s were")} found via {string.Join(" and ", via)}.";
    }

    /// <summary>The team file's path when it exists but could not be parsed, else null: a corrupt file is never treated as absent.</summary>
    internal static string? UnreadableTeamFile(RosterContext c, string dir)
    {
        var path = c.Services.Roster.TeamPath(dir, c.TeamFile);
        return c.Services.Files.Exists(path) ? path : null;
    }

    /// <summary>The additive keys every close answer carries beside <c>closed</c>.</summary>
    internal static JsonObject CloseSummary(
        RosterContext c, IReadOnlyList<JsonValue> closable, CloseVerification verified, List<string> warnings, ReleaseGate.Outcome? release = null)
    {
        var strays = closable.Where(m => m.Member("stray") is { IsTruthy: true }).Select(m => MemberPlanning.JsConvertText(m.Member("name"))).ToList();
        if (strays.Count > 0)
        {
            warnings.Insert(0, StrayWarning(c, strays));
        }

        var held = release?.Held ?? [];
        var summary = new JsonObject
        {
            ["closed"] = verified.Results.All(r => r.Get("closed") is { IsTruthy: true }),
            ["expected"] = closable.Count + held.Count,
            ["closed_count"] = verified.Results.Count(r => r.Get("closed") is { IsTruthy: true }),
            ["still_live"] = Strings(verified.StillLive.Concat(held.Where(v => v.Live).Select(v => v.Member))),
            ["held_not_live"] = HeldNotLive(held),
            ["strays"] = Strings(strays),
            ["warnings"] = Strings(warnings),
        };
        if (held.Count > 0 && closable.Count > 0)
        {
            summary["reason"] = $"{held.Count} member(s) held by the release check and not closed: {string.Join("; ", held.Select(v => $"{v.Member} {v.Text}"))}";
        }

        return summary;
    }

    /// <summary>The held members whose session the release check did not find live: not running, or liveness undecidable.</summary>
    private static JsonArray HeldNotLive(IEnumerable<ReleaseGate.Verdict> held)
    {
        var array = new JsonArray();
        array.AddRange(held.Where(v => !v.Live).Select(v => (JsonValue)new JsonObject { ["member"] = v.Member, ["status"] = v.Status }));
        return array;
    }

    internal static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();
        array.AddRange(values.Select(v => (JsonValue)v));
        return array;
    }

    // ---------------------------------------------------------------- the token gate

    private static JsonValue ScopeValue(string scope) => JsonValue.FromString(scope);

    /// <summary>
    /// Refuses a flag given twice: the permission hook and this parser fold a repeated flag differently, so a close names each flag once.
    /// <c>--ignore-release</c> is the one flag that repeats, once per member it names.
    /// </summary>
    internal static void RequireEachFlagOnce(RosterContext c, string verb)
    {
        if (c.Opts.Repeated.FirstOrDefault(k => k != ReleaseGate.IgnoreFlag) is { } repeated)
        {
            throw new RosterFailure($"{verb}: give --{repeated} once");
        }
    }

    /// <summary>
    /// The shared check of every <c>--close</c> variant. A close runs in one call: <c>--confirm</c> and <c>--plan-token</c> are
    /// accepted and ignored, so an older caller (the mod's Dismiss button) keeps working.
    /// </summary>
    internal static void GateClose(RosterContext c, string verb, IReadOnlyList<JsonValue> closable, CleanClose.Subject? clean = null)
    {
        SubOrchestratorClose.RequireSomethingToClose(c, verb, closable.Count);
        SubOrchestratorClose.RequireGreenLight(c, verb, clean is null ? null : () => CleanClose.NotReleased(clean) is null);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_/.:@%+=-]+\z")]
    private static partial Regex SafeShellWord();

    internal static string ShellWord(string s) => SafeShellWord().IsMatch(s) ? s : RosterContext.ShQuote(s);

    /// <summary>The exact <c>--close</c> command a plan's caller runs after the user says yes.</summary>
    internal static string CloseCommand(RosterContext c, string verb, string? name, string token)
    {
        var words = new List<string> { verb };
        if (name is not null)
        {
            words.Add(name);
        }

        words.AddRange(["--close", "--confirm", "--plan-token", token]);
        if (c.Opts.IsTrue(ReleaseGate.IgnoreFlag))
        {
            words.Add("--" + ReleaseGate.IgnoreFlag);
        }

        foreach (var named in c.Opts.All(ReleaseGate.IgnoreFlag))
        {
            words.AddRange(["--" + ReleaseGate.IgnoreFlag, named]);
        }

        if (c.Opts.Text("team") is { } team)
        {
            words.AddRange(["--team", team]);
        }

        if (c.Opts.Text("cwd") is { } cwd)
        {
            words.AddRange(["--cwd", cwd]);
        }

        return c.SelfCommand + " " + string.Join(' ', words.Select(ShellWord));
    }

    /// <summary>The token for a team's close plan.</summary>
    internal static string Token(RosterContext c, JsonValue scope, IReadOnlyList<JsonValue> closable) =>
        c.Services.Launch!.Teardown.CloseToken(scope, closable);

    internal static JsonValue Scope(string scope) => ScopeValue(scope);

    /// <summary>
    /// Closes the targets and verifies each is gone; the targets are objects, as the close rows are. Each row is cloned: the close stamps a
    /// found terminal address on it, and a row of the held team document is shared (b459 step 13).
    /// </summary>
    internal static CloseVerification CloseAndVerify(RosterContext c, IReadOnlyList<JsonValue> closable, string? teamTransport, List<string> warnings) =>
        c.Services.Launch!.Teardown.CloseAndVerify(closable.Select(static m => MemberPlanning.Obj(m).Clone()).ToList(), teamTransport ?? string.Empty, warnings, c.LaunchContext(), c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile), c.Dir);

    /// <summary>The snapshot a whole-team close takes first: a failure to write it is a warning, never a reason to keep the team.</summary>
    internal static JsonValue AutoSnapshot(RosterContext c, string dir, List<string> warnings)
    {
        try
        {
            return SnapshotWriter.Write(c, dir, c.TeamFile).Get("snapshot") ?? JsonValue.Null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            warnings.Add($"no snapshot was taken before closing: {error.Message}");
            return JsonValue.Null;
        }
    }

    // ---------------------------------------------------------------- the roster template of a team

    /// <summary>
    /// The roster block the team at this scope reads its members from: its recorded key (or the name rule) as that key actually
    /// resolves. A key with no block falls through to the default block on read, so an edit made for the team must land there too.
    /// </summary>
    internal static string? TemplateKey(RosterContext c, string dir)
    {
        var key = c.Services.Roster.TeamRosterKey(dir, c.TeamFile);
        var resolved = c.Services.Config.ResolveRoster(c.Cwd, key, c.RepoBasename, c.Registry);
        return resolved.IsMap && resolved.Member("teamKey") is { IsTruthy: true, IsText: true } k ? k.AsString() : key;
    }

    /// <summary><c>removeConfigMember</c>'s answer as the object a dismiss prints under <c>config</c>.</summary>
    internal static JsonObject ConfigRemovalJson(RemoveResult r)
    {
        var o = new JsonObject
        {
            ["level"] = RosterJson.Text(r.Level),
            ["path"] = RosterJson.Text(r.Path),
            ["wasDefaulted"] = r.WasDefaulted,
            ["removed"] = r.Removed,
        };
        if (r.Removed)
        {
            o["idx"] = r.Index;
            o["before"] = r.Before ?? new JsonArray();
            o["after"] = r.After ?? new JsonArray();
            o["container"] = RosterJson.Text(r.Container);
            return o;
        }

        if (r.Unusable)
        {
            o["unusable"] = true;
        }

        o["reason"] = RosterJson.Text(r.Reason);
        return o;
    }

    // ---------------------------------------------------------------- liveness in output

    /// <summary>Three-valued liveness as an output field: <c>null</c> is "could not determine", which is not <c>false</c>.</summary>
    internal static JsonObject LiveField(RosterContext c, string dir, JsonValue member)
    {
        var st = c.Services.Launch!.Teardown.MemberLiveness(dir, member, c.TeamFile);
        if (st.Get("indeterminate") is { IsTruthy: true })
        {
            return new JsonObject { ["live"] = JsonValue.Null, ["live_unknown"] = st.Get("why") ?? JsonValue.Null };
        }

        return new JsonObject { ["live"] = st.Get("live") is { IsTruthy: true } };
    }

    /// <summary>
    /// The guard of <c>untrack</c> for a sub-orchestrator: a live member is never forgotten, whatever <c>--keep-sessions</c> says,
    /// because the row would leave the closable set the green light was given for. A member whose liveness could not be determined
    /// proceeds; the untrack notes it as liveness unknown, as <see cref="UntrackLiveGuard"/> does.
    /// </summary>
    internal static void CarvedUntrackGuard(RosterContext c, string dir, IEnumerable<JsonValue> members)
    {
        foreach (var m in members)
        {
            var st = c.Services.Launch!.Teardown.MemberLiveness(dir, m, c.TeamFile);
            if (st.Get("live") is { IsTruthy: true })
            {
                throw new RosterFailure($"untrack: {MemberPlanning.JsConvertText(m.Member("name"))} is still running; dismiss it (green light) instead; nothing was changed.");
            }
        }
    }

    /// <summary>
    /// The guard of <c>untrack</c>: forgetting a record cannot be undone, so a live member is refused unless the caller said to leave
    /// it running. A member whose liveness could not be determined proceeds; the untrack notes it as liveness unknown.
    /// </summary>
    internal static void UntrackLiveGuard(RosterContext c, string dir, IEnumerable<JsonValue> members, string verb)
    {
        var stillUp = new List<(string Name, string Why)>();
        var allHavePanes = true;
        foreach (var m in members)
        {
            var st = c.Services.Launch!.Teardown.MemberLiveness(dir, m, c.TeamFile);
            if (st.Get("live") is { IsTruthy: true })
            {
                stillUp.Add((MemberPlanning.JsConvertText(m.Member("name")), "live"));
                allHavePanes &= MemberPlanning.HasPane(c, m.Member("route")) && m.Member("transport_id") is { IsTruthy: true };
            }
        }

        if (stillUp.Count == 0)
        {
            return;
        }

        var one = stillUp.Count == 1;
        var detail = new JsonArray();
        foreach (var s in stillUp)
        {
            detail.Add(new JsonObject { ["name"] = s.Name, ["why"] = s.Why });
        }

        throw new RosterFailure(
            $"untrack: {(one ? $"{stillUp[0].Name} is" : $"{string.Join(", ", stillUp.Select(s => s.Name))} are")} still live — " +
            "untrack only forgets the record and CANNOT BE UNDONE; the session would keep running with nothing tracking it. " +
            (allHavePanes ? $"Use `{verb}` to close {(one ? "it" : "them")}, or re-run" : "Re-run") +
            $" with --keep-sessions to forget the record and leave {(one ? "it" : "them")} running. " +
            $"Detail: {JsonText.Stringify(detail, 0)}");
    }
}
