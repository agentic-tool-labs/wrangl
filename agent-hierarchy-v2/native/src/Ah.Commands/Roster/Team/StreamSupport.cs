using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy.StateCalls;

namespace Ah.Commands;

/// <summary>
/// What the stream verbs share: the flag checks, a stream's tab and its label, the one herdr snapshot a command takes, and the
/// read-modify-write of a stream record on the team file as it stands.
/// </summary>
internal static partial class StreamSupport
{
    /// <summary>A stream's name: branch-safe, directory-safe, and short enough that <c>&lt;glyph&gt; &lt;name&gt;</c> fits herdr's metadata limit.</summary>
    [GeneratedRegex("^" + Ah.Config.ConfigModule.StreamNamePattern + "\\z")]
    internal static partial Regex NameRe();

    /// <summary>The words a member's own state may be given as in <c>stream-label --self-state</c>.</summary>
    internal static readonly IReadOnlyList<string> SelfStates = ["working", "idle", "blocked"];

    /// <summary><c>--team &lt;T&gt;</c> as it must be repeated in a command this invocation prints for the same team.</summary>
    internal static string TeamFlagText(RosterContext c) => c.TeamFile is { } t ? $" --team {t}" : string.Empty;

    internal static void RejectUnknownFlags(RosterContext c, IReadOnlyList<string> allowed, string verb)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (!allowed.Contains(key))
            {
                throw new RosterFailure(
                    $"{verb}: unrecognized flag --{key} (use {string.Join(", ", allowed.Select(f => $"--{f}"))})");
            }
        }
    }

    /// <summary>A value flag's string, or null when absent; given with no value is a usage error.</summary>
    internal static string? ValueFlag(RosterContext c, string key, string verb)
    {
        if (!c.Opts.Has(key))
        {
            return null;
        }

        return c.Opts.Text(key) ?? throw new RosterFailure($"{verb}: --{key} requires a value");
    }

    /// <summary>Open streams whose recorded worktree is gone from disk, each with the command that settles it.</summary>
    internal static JsonArray StaleStreamWorktrees(RosterContext c, JsonValue team)
    {
        var stale = new JsonArray();
        if (team.Member("streams") is not { IsMap: true } streams)
        {
            return stale;
        }

        var objects = streams.AsObject();
        foreach (var name in objects.Keys)
        {
            if (objects[name].Member("state") is not { IsText: true } state || state.AsString() != "open"
                || objects[name].Member("worktree") is not { IsText: true } worktree || worktree.AsString().Length == 0
                || c.Services.Files.Exists(worktree.AsString()))
            {
                continue;
            }

            stale.Add(new JsonObject
            {
                ["reason"] = "stale-stream-worktree",
                ["stream"] = name,
                ["worktree"] = worktree.AsString(),
                ["remedy"] = $"ah roster stream-done {name}, or re-create the worktree",
            });
        }

        return stale;
    }

    /// <summary>The public view of a stream record.</summary>
    internal static JsonObject StreamView(string name, JsonValue record)
    {
        var view = new JsonObject
        {
            ["name"] = name,
            ["state"] = record.Member("state") ?? JsonValue.Null,
            ["branch"] = RosterJson.OrNull(record.Member("branch")),
            ["base"] = RosterJson.OrNull(record.Member("base")),
            ["base_sha"] = RosterJson.OrNull(record.Member("base_sha")),
            ["worktree"] = RosterJson.OrNull(record.Member("worktree")),
            ["tab_id"] = RosterJson.OrNull(record.Member("tab_id")),
        };
        if (record.Member("opened") is { } opened)
        {
            view["opened"] = opened;
        }

        if (record.Member("closed") is { IsTruthy: true } closed)
        {
            view["closed"] = closed;
        }

        return view;
    }

    /// <summary>The team's open exchanges with the names they carry.</summary>
    internal static JsonArray TeamOpenExchanges(RosterContext c, string dir) =>
        c.Services.Launch!.Streams.OpenTeamExchanges(dir, c.TeamFile, allTeams: false);

    /// <summary>
    /// <c>herdr agent list</c> (unless asked not to) and <c>herdr tab list</c> of every server the team's members live on, once each; a failed
    /// query leaves its half null. <c>TabServers</c> names the one server that listed each tab, null for an id more than one server lists.
    /// </summary>
    internal sealed record Snapshot(JsonArray? Agents, JsonArray? Tabs, IReadOnlyDictionary<string, Ah.Launch.IHerdr?>? TabServers = null);

    internal static Snapshot HerdrSnapshot(RosterContext c, List<string> warnings, bool wantAgents = true)
    {
        var launch = c.Services.Launch!;
        JsonArray? agents = null;
        JsonArray? tabs = null;
        IReadOnlyList<Ah.Launch.IHerdr> asked = [];
        try
        {
            asked = IRosterLaunch.TeamHerdrs(launch, c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile), Ah.Core.Mux.HerdrFlavour.SessionFlavour(c.EnvText));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            warnings.Add(error.Message);
        }

        // ah2-145: agents come only from the servers the team's members live on; a session on no herdr with no stamped member asks none
        if (wantAgents && asked.Count > 0)
        {
            try
            {
                var found = new JsonArray();
                foreach (var herdr in asked)
                {
                    foreach (var a in ReferenceEquals(herdr, launch.Herdr) ? launch.Topology.Query() : herdr.Agents())
                    {
                        found.Add(a);
                    }
                }

                agents = found;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                warnings.Add(error.Message);
            }
        }

        // a tab list missing any one server is no list: a tab it did not show is not proven gone
        var servers = new Dictionary<string, Ah.Launch.IHerdr?>(StringComparer.Ordinal);
        var all = new JsonArray();
        var whole = true;
        try
        {
            // no server asked is no list either: a tab nobody listed is not proven gone
            whole = asked.Count > 0;
            foreach (var herdr in asked)
            {
                if (herdr.Invoke(["tab", "list"]).Member("result")?.Member("tabs") is not { IsArray: true } list)
                {
                    warnings.Add("herdr tab list produced unexpected shape (missing .result.tabs)");
                    whole = false;
                    break;
                }

                foreach (var t in list.AsArray())
                {
                    all.Add(t);
                    IRosterLaunch.NoteTab(servers, MemberPlanning.Text(t.Member("tab_id")) ?? string.Empty, herdr);
                }
            }

            tabs = whole ? all : null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            warnings.Add(error.Message);
        }

        return new Snapshot(agents, tabs, servers);
    }

    /// <summary>What rendering a stream's label came to: the label, the members' statuses and what happened to the tab.</summary>
    internal sealed record Rendered(string? Label, JsonArray Statuses, bool? TabLive, bool Renamed, JsonValue? Previous);

    /// <summary>
    /// Derives stream <paramref name="name"/>'s label and, when its tab shows something else, renames the tab. Never writes the team
    /// file; a failed rename is a warning. <c>TabLive</c> is false only when <c>tab list</c> answered without the tab.
    /// </summary>
    internal static Rendered RenderStream(
        RosterContext c, string dir, JsonValue team, string name, Snapshot snap, List<string> warnings, JsonObject? self = null)
    {
        var launch = c.Services.Launch!;
        var rules = launch.Streams;
        var record = rules.StreamsOf(team).Get(name) ?? JsonValue.Null;
        var statuses = rules.StreamMemberStatuses(rules.StreamMembers(team, name), snap.Agents, self is { } selfObject ? (JsonValue)selfObject : (JsonValue?)null);
        var state = MemberPlanning.Text(record.Member("state"));
        var owed = state == "done" ? [] : TeamOpenExchanges(c, dir).Select(e => MemberPlanning.JsConvertText(e.Member("to_name"))).ToList();
        var label = $"{rules.StreamGlyph(state, statuses, owed)} {name}";
        if (record.Member("tab_id") is not { IsTruthy: true } tabId)
        {
            return new Rendered(null, statuses, null, false, null);
        }

        var tabText = MemberPlanning.JsConvertText(tabId);
        JsonValue? tab = snap.Tabs?.FirstOrDefault(t => MemberPlanning.Text(t.Member("tab_id")) == tabText) is { IsMap: true } t ? t : (JsonValue?)null;
        if (snap.Tabs is not null && tab is null)
        {
            warnings.Add($"stream {name}: its tab {tabText} is gone");
            return new Rendered(label, statuses, false, false, null);
        }

        if (snap.TabServers is { } byTab && byTab.TryGetValue(tabText, out var listedBy) && listedBy is null)
        {
            warnings.Add($"stream {name}: tab {tabText} is listed by more than one herdr server, so it was not renamed");
            return new Rendered(label, statuses, true, false, null);
        }

        if (tab is { } existing && MemberPlanning.Text(existing.Member("label")) == label)
        {
            return new Rendered(label, statuses, true, false, null);
        }

        try
        {
            (snap.TabServers?.GetValueOrDefault(tabText) ?? launch.Herdr).Invoke(["tab", "rename", tabText, label]);
            return new Rendered(label, statuses, true, true, tab?.Member("label"));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            warnings.Add($"stream {name}: renaming tab {tabText} failed: {error.Message}");
            return new Rendered(label, statuses, tab is not null ? true : null, false, null);
        }
    }

    /// <summary>
    /// <c>herdr tab create</c> for a stream's tab. With no <c>--workspace</c> it lands in herdr's default workspace, not the caller's:
    /// the CLI sends no caller context. A stream's tab never takes focus; <paramref name="focus"/> is for a tab the user asked to work in.
    /// </summary>
    internal static List<string> TabCreateArgv(RosterContext c, string label, string rootCwd, bool focus = false)
    {
        var argv = new List<string> { "tab", "create" };
        if (c.EnvText("HERDR_WORKSPACE_ID") is { } workspace)
        {
            argv.AddRange(["--workspace", workspace]);
        }

        argv.AddRange(["--label", label, "--cwd", rootCwd]);
        if (!focus)
        {
            argv.Add("--no-focus");
        }

        return argv;
    }

    internal sealed record StreamTab(string? TabId, bool Created, string? RootPane, string? Error);

    /// <summary>
    /// The stream's tab: its recorded one while <c>tab list</c> still shows it (or cannot say), else a new one labelled
    /// <paramref name="label"/>. A new tab starts in the stream's work tree while it is on disk, else in <paramref name="rootCwd"/>:
    /// a spawn launches into the tab's one pane as is, so a tab made by a reopen starts its first member where the tab started.
    /// </summary>
    internal static StreamTab EnsureStreamTab(RosterContext c, JsonValue record, string label, string rootCwd, JsonArray? tabs)
    {
        if (record.Member("tab_id") is { IsTruthy: true } recorded)
        {
            var id = MemberPlanning.JsConvertText(recorded);
            if (tabs is null || tabs.Any(t => MemberPlanning.Text(t.Member("tab_id")) == id))
            {
                return new StreamTab(id, false, null, null);
            }
        }

        var cwd = MemberPlanning.Text(record.Member("worktree")) is { Length: > 0 } tree && c.Services.Files.Stat(tree) is { Kind: FileKind.Directory } ? tree : rootCwd;
        try
        {
            var r = c.Services.Launch!.Herdr.Invoke(TabCreateArgv(c, label, cwd));
            if (r.Member("result")?.Member("tab")?.Member("tab_id") is not { IsTruthy: true } tabId)
            {
                throw new InvalidOperationException("herdr tab create produced unexpected shape (missing .result.tab.tab_id)");
            }

            var root = r.Member("result")?.Member("root_pane")?.Member("pane_id") is { IsTruthy: true } rp ? MemberPlanning.JsConvertText(rp) : null;
            return new StreamTab(MemberPlanning.JsConvertText(tabId), true, root, null);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new StreamTab(null, false, null, error.Message);
        }
    }

    /// <summary>Read-modify-write of one stream record on the team file as it stands now.</summary>
    internal static JsonObject WriteStreamRecord(RosterContext c, string dir, string name, JsonValue record)
    {
        JsonObject? written = null;
        c.Services.Hierarchy.StateCalls.UpdateTeam(
            dir,
            c.TeamFile,
            fresh =>
            {
                if (fresh.IsNull)
                {
                    throw new RosterFailure(
                        $"the team file {c.Services.Roster.TeamPath(dir, c.TeamFile)} is gone or unreadable — stream {name} was not recorded");
                }

                var streams = c.Services.Launch!.Streams.StreamsOf(fresh).Clone();
                streams[name] = record;
                var next = fresh.AsObject().Clone();
                next["streams"] = streams;
                written = next;
                return TeamFileOutcome.Write(next);
            });
        return written!;
    }
}
