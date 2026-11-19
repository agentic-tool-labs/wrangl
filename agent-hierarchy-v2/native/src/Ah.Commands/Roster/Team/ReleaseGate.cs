using System.Text;
using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hierarchy;

namespace Ah.Commands;

/// <summary>
/// The release check <c>dismiss</c> and <c>disband --close</c> run before they close a member. Nothing is stored. A member working
/// now holds, and so does every member while the message store cannot be read. Any other closes, and the output names what it leaves behind: an open request, uncommitted work, or
/// commits beyond the run's branch and the release, by branch and sha. <c>--ignore-release &lt;name&gt;</c> closes that member through a hold, with a warning; without a name it overrides no hold.
/// </summary>
internal static partial class ReleaseGate
{
    internal const string IgnoreFlag = "ignore-release";

    /// <summary>One member's status. <paramref name="Closable"/> is false for a member working now or an unreadable store; <paramref name="Warn"/> marks a close with something to check.</summary>
    internal sealed record Verdict(string Member, string Status, string Detail, bool Closable, bool Live = true, bool Warn = false)
    {
        internal string Text => Detail.Length == 0 ? Status : $"{Status} ({Detail})";

        internal JsonObject ToJson() => new() { ["member"] = Member, ["status"] = Status, ["detail"] = Detail, ["closable"] = Closable };
    }

    /// <summary>The members that may close and the verdict of every member checked.</summary>
    internal sealed record Outcome(List<JsonValue> Closable, List<Verdict> Verdicts)
    {
        internal List<Verdict> Held => Verdicts.Where(v => !v.Closable).ToList();

        internal JsonArray HeldJson()
        {
            var held = new JsonArray();
            held.AddRange(Held.Select(v => (JsonValue)new JsonObject { ["member"] = v.Member, ["status"] = v.Status, ["detail"] = v.Detail }));
            return held;
        }

        internal string Summary()
        {
            var closing = Verdicts.Count(v => v.Closable);
            var held = Held;
            var line = held.Count == 0
                ? $"release: {closing} may close, none held"
                : $"release: {closing} may close, {held.Count} held: {string.Join("; ", held.Select(v => $"{v.Member} {v.Text}"))}";
            var warned = Verdicts.Where(static v => v.Warn).Select(static v => $"{v.Member} {v.Text}").ToList();
            return warned.Count == 0 ? line : $"{line}; closing anyway, check: {string.Join("; ", warned)}";
        }

        /// <summary>The block a verb adds to its output: every member's status, who may close, and the one-line summary.</summary>
        internal JsonObject ReleaseJson()
        {
            var statuses = new JsonArray();
            statuses.AddRange(Verdicts.Select(v => (JsonValue)v.ToJson()));
            var closing = new JsonArray();
            closing.AddRange(Verdicts.Where(v => v.Closable).Select(v => (JsonValue)v.Member));
            var block = new JsonObject { ["statuses"] = statuses, ["closed"] = closing, ["summary"] = Summary() };
            var warned = Verdicts.Where(static v => v.Warn).ToList();
            if (warned.Count > 0)
            {
                var warnings = new JsonArray();
                warnings.AddRange(warned.Select(static v => (JsonValue)new JsonObject { ["member"] = v.Member, ["status"] = v.Status, ["detail"] = v.Detail }));
                block["warnings"] = warnings;
            }

            return block;
        }
    }

    /// <summary>Adds the release block and the held members to a verb's output, when the check ran.</summary>
    internal static void Annotate(JsonObject output, Outcome outcome)
    {
        if (outcome.Verdicts.Count > 0)
        {
            output["release"] = outcome.ReleaseJson();
            output["held"] = outcome.HeldJson();
        }
    }

    /// <summary>Prints <paramref name="output"/> with the release block and returns the verb's exit code: 3 when any member was held.</summary>
    internal static int Finish(RosterContext c, JsonObject output, Outcome outcome)
    {
        Annotate(output, outcome);
        c.Out(output);
        return outcome.Held.Count > 0 ? 3 : 0;
    }

    /// <summary>The output of a close in which the release check held every member that would have closed: nothing was closed or written.</summary>
    internal static int NothingMayClose(RosterContext c, Outcome outcome, JsonObject sources, List<string> warnings)
    {
        var output = new JsonObject
        {
            ["closed"] = false,
            ["reason"] = $"every member to close is held (see held); wait, or close one anyway with --{IgnoreFlag} <name>",
            ["expected"] = 0,
            ["warnings"] = TeardownSupport.Strings(warnings),
            ["sources"] = sources,
        };
        return Finish(c, output, outcome);
    }

    internal static readonly IReadOnlySet<string> NoTeamRows = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Of <paramref name="names"/>, the members of <paramref name="team"/> a bare <c>dismiss</c> run at <paramref name="projectRoot"/> would
    /// close as clean now: the same team read and the same clean check (<see cref="DismissVerb.CleanOf"/>). Writes nothing.
    /// </summary>
    internal static IReadOnlyList<string> ReadyOf(IRosterCliServices services, string dir, string projectRoot, string team, IReadOnlyList<string> names)
    {
        var c = new RosterContext(services, RosterArgs.Parse(["--team", team]), "dismiss", projectRoot, TextWriter.Null, TextWriter.Null) { TeamArg = team, TeamFile = team };
        c.PinDir(dir);
        var (_, record) = DismissVerb.TeamOf(c, dir, []);
        DismissVerb.TeamReads reads;
        try
        {
            // ah2-253: the herdr resync, roster and open exchanges are the same for every member, so one read serves them all
            reads = DismissVerb.ReadsOf(c, dir, record);
        }
        catch (StoreUnavailableException)
        {
            // as each member's own read would: no member is clean while the store cannot answer
            return [];
        }

        var ready = new List<string>();
        foreach (var m in DismissVerb.Members(record))
        {
            if (m.Member("name") is { IsText: true } n && names.Contains(n.AsString()) && DismissVerb.CleanOf(c, dir, n.AsString(), record, m, reads).Clean.Token is not null)
            {
                ready.Add(n.AsString());
            }
        }

        return ready;
    }

    /// <summary>
    /// Filters <paramref name="closable"/> down to the members whose release status lets them close. <paramref name="teamNames"/>
    /// are the members that have a row in the team file: their work tree comes from their grants or their row, and any other
    /// member's from the cwd of its latest peers row.
    /// </summary>
    internal static Outcome Apply(RosterContext c, string dir, IReadOnlyList<JsonValue> closable, IReadOnlySet<string> teamNames, JsonArray? sharedRoster = null)
    {
        var hierarchy = c.Services.Hierarchy;

        using var decisions = hierarchy.Core.DecisionReads();
        JsonArray? roster = sharedRoster;
        try
        {
            roster ??= hierarchy.Roster.AttributedRoster(dir);
        }
        catch (StoreUnavailableException)
        {
            roster = null;
        }

        var pools = new Dictionary<string, (IReadOnlyList<string> Pools, List<JsonObject> Exchanges)>(StringComparer.Ordinal);
        var verdicts = new List<Verdict>();
        var kept = new List<JsonValue>();
        foreach (var member in closable)
        {
            var name = MemberPlanning.JsConvertText(member.Member("name"));
            Verdict verdict;
            try
            {
                verdict = roster is null ? StoreUnreadable(name) : Judge(c, dir, member, name, teamNames.Contains(name), pools, roster);
            }
            catch (StoreUnavailableException)
            {
                verdict = StoreUnreadable(name);
            }

            if (!verdict.Closable && c.Opts.All(IgnoreFlag).Contains(name))
            {
                verdict = verdict with { Status = "ignored", Detail = $"--{IgnoreFlag} {name}: {verdict.Text}", Closable = true, Warn = true };
            }

            verdicts.Add(verdict);
            if (verdict.Closable)
            {
                kept.Add(member);
            }
        }

        return new Outcome(kept, verdicts);
    }

    /// <summary>The verdict for one member, carrying whether the gate found the session live (false: not running, or liveness undecidable).</summary>
    private static Verdict Judge(RosterContext c, string dir, JsonValue member, string name, bool inTeam, Dictionary<string, (IReadOnlyList<string> Pools, List<JsonObject> Exchanges)> pools, JsonArray roster)
    {
        var state = c.Services.Launch!.Teardown.MemberLiveness(dir, member, c.TeamFile);
        return JudgeWith(c, dir, member, name, inTeam, pools, roster, state) with { Live = state.Get("live") is { IsTruthy: true } };
    }

    private static Verdict JudgeWith(RosterContext c, string dir, JsonValue member, string name, bool inTeam, Dictionary<string, (IReadOnlyList<string> Pools, List<JsonObject> Exchanges)> pools, JsonArray roster, JsonObject state)
    {
        var unknown = state.Get("live") is not { IsTruthy: true } && state.Get("indeterminate") is { IsTruthy: true };
        var tree = WorkTree(c, member, name, inTeam, roster);
        if (!unknown && state.Get("live") is not { IsTruthy: true })
        {
            return NotRunning(c, name, tree, member);
        }

        // Working now is the one hold: closing it loses paid work. Everything else is named in the output and the close proceeds.
        if (WorkingNow(c, dir, member, name, roster))
        {
            return Hold(name, "busy", "busy now");
        }

        var notes = new List<(string Status, string Detail)>();
        if (unknown)
        {
            notes.Add(("liveness-unknown", $"its liveness cannot be decided ({MemberPlanning.JsConvertText(state.Get("why"))})"));
        }

        // Q4: no cheap per-member reader of the job-track records exists, so a running job is not checked here.
        if (OpenRequestTo(c, ExchangesFor(c, dir, tree, pools), name) is { } open)
        {
            notes.Add(("open-request", $"request {open} has no response yet"));
        }

        if (tree is null)
        {
            return notes.Count == 0 ? new Verdict(name, "ready", "idle, no work tree", true) : Noted(name, notes);
        }

        var status = Git(c, tree, "status", "--porcelain", "--untracked-files=all", "--ignore-submodules=none");
        if (!status.Ok && !status.Truncated)
        {
            notes.Add(("stale", $"git could not read the work tree {tree}: " + status.Detail));
            return Noted(name, notes);
        }

        if (MembersDirt(c, tree, member, status))
        {
            notes.Add(("dirty", $"uncommitted work in {tree}"));
        }

        if (AheadNote(c, tree) is { } ahead)
        {
            notes.Add(ahead);
        }

        return notes.Count == 0 ? new Verdict(name, "ready", "idle-clean", true) : Noted(name, notes);
    }

    /// <summary>A member that closes with a warning: the first note's status, and every note's detail.</summary>
    private static Verdict Noted(string name, List<(string Status, string Detail)> notes) =>
        new(name, notes[0].Status, string.Join("; ", notes.Select(static n => n.Detail)), true, Warn: true);

    /// <summary>What sits in the tree beyond the run's branch and the release, by branch and sha; null when nothing does.</summary>
    private static (string Status, string Detail)? AheadNote(RosterContext c, string tree)
    {
        var ahead = CheckpointVerb.CommitsAheadOfRun(c, tree, WorkTreeAssignment.TreeOf(c, c.Cwd)?.Root);
        if (ahead == 0)
        {
            return null;
        }

        string Read(params string[] args) => Git(c, tree, args) is { Ok: true } read && read.Stdout.Trim() is { Length: > 0 } text ? text : "unknown";
        var place = $"{Read("rev-parse", "--abbrev-ref", "HEAD")} at {Read("rev-parse", "HEAD")}";
        return ahead is { } count
            ? ("ahead", $"{count} commit(s) in {tree} not on the run's branch or the release: {place}")
            : ("stale", $"cannot tell whether {tree} holds unshipped commits: {place}");
    }

    /// <summary>The id of an open request (no response) whose <c>to_name</c> is <paramref name="name"/>, or null.</summary>
    private static string? OpenRequestTo(RosterContext c, List<JsonObject> exchanges, string name) =>
        exchanges
            .Where(e => e.Get("response") is not { IsMap: true } && e.Get("request") is { IsMap: true } r && r.Member("path") is { IsText: true })
            .FirstOrDefault(e => (e.Get("request")!.Value.Member("meta") is { IsMap: true } meta && meta.Member("to_name") is { IsText: true } toName
                ? toName.AsString()
                : MemberPlanning.Text(c.Services.Hierarchy.Core.ReadMsgFile(e.Get("request")!.Value.Member("path")!.Value.AsString()).Member("fm")?.Member("to_name"))) == name) is { } hit
            ? IdOf(hit)
            : null;

    /// <summary>The answer line a member should give, with its real branch and HEAD filled in when it has a work tree.</summary>
    internal static string ExpectedLine(RosterContext c, string prefix, string? tree, string role)
    {
        if (tree is null)
        {
            return $"{prefix}: ready no-tree inflight=none";
        }

        if (IsReadOnlyClass(c, role))
        {
            return $"{prefix}: ready read-only inflight=none";
        }

        return CheckpointVerb.BranchOf(c, tree) is { Length: > 0 } branch && TreeState(c, tree).Head is { Length: > 0 } head
            ? $"{prefix}: ready branch={branch} sha={head[..Math.Min(7, head.Length)]} inflight=none"
            : $"{prefix}: ready branch=<branch> sha=<sha> inflight=none";
    }

    /// <summary>The hold text for a member that answered <c>no-tree</c> but has a work tree.</summary>
    internal static string HaveTreeText(RosterContext c, string tree)
    {
        var branch = CheckpointVerb.BranchOf(c, tree) ?? "unknown";
        var head = TreeState(c, tree).Head;
        return $"you have a work tree at {tree} (branch {branch}, HEAD {(head is null ? "unknown" : head[..Math.Min(7, head.Length)])}); answer with branch and sha";
    }

    private static Verdict Hold(string name, string status, string detail) => new(name, status, detail, false);

    /// <summary>With the store unreadable nothing here can say the member is not working, so it holds until the store is back or it is named.</summary>
    private static Verdict StoreUnreadable(string name) => Hold(
        name,
        "store-unreadable",
        $"message store cannot be read: wait for it, or close this member anyway with --{IgnoreFlag} {name}");

    private static Verdict NotRunning(RosterContext c, string name, string? tree, JsonValue member)
    {
        if (tree is null)
        {
            return new Verdict(name, "stale", "no work tree", true, Warn: true);
        }

        var state = TreeState(c, tree, member);
        if (state.Failure is { } failure)
        {
            return new Verdict(name, "stale", failure, true, Warn: true);
        }

        var ahead = AheadNote(c, tree);
        var unshipped = ahead is { } note ? "; " + note.Detail : string.Empty;
        return state.Dirty
            ? new Verdict(name, "not-running-dirty", "the session is not running and its work tree has uncommitted changes" + unshipped, true, Warn: true)
            : new Verdict(name, "not-running", "work tree clean" + unshipped, true, Warn: ahead is not null);
    }

    /// <summary>Whether the session is running. A liveness that cannot be decided counts as running, the side that asks for an answer.</summary>
    internal static bool IsLive(RosterContext c, string dir, JsonValue member)
    {
        var state = c.Services.Launch!.Teardown.MemberLiveness(dir, member, c.TeamFile);
        return state.Get("live") is { IsTruthy: true } || state.Get("indeterminate") is { IsTruthy: true };
    }

    // ---------------------------------------------------------------- the request and its answer

    /// <summary>
    /// The exchanges of every pool the team's exchanges live in, plus the member's work tree, newest first (the shared pool
    /// enumeration). <c>msg new</c> files a message in the pool of the cwd it is given, so a request made for a member in its
    /// work tree sits there; a response is paired with its request across pools.
    /// </summary>
    internal static List<JsonObject> ExchangesFor(RosterContext c, string dir, string? tree, Dictionary<string, (IReadOnlyList<string> Pools, List<JsonObject> Exchanges)> cache) =>
        PoolsOf(c, dir, tree, cache).Exchanges;

    /// <summary>The pools of the run for a work tree and the exchanges over them, read once per tree.</summary>
    private static (IReadOnlyList<string> Pools, List<JsonObject> Exchanges) PoolsOf(
        RosterContext c, string dir, string? tree, Dictionary<string, (IReadOnlyList<string> Pools, List<JsonObject> Exchanges)> cache)
    {
        var key = tree ?? string.Empty;
        if (!cache.TryGetValue(key, out var entry))
        {
            var pools = c.Services.Hierarchy.Messages.PoolDirs(dir, TeamScope(c), tree is null ? null : [tree]);
            entry = (pools, [.. c.Services.Hierarchy.Core.ListExchanges(pools).Where(e => e.IsMap).Select(e => e.AsObject())]);
            cache[key] = entry;
        }

        return entry;
    }

    private static List<string> TeamScope(RosterContext c) => [WorkTreeAssignment.TeamOf(c)];

    private static string IdOf(JsonObject exchange) => exchange.Get("id") is { IsText: true } id ? id.AsString() : string.Empty;

    /// <summary>The ready form of an answer: the member's branch and sha, no work tree, or read-only (the gate reads the tree).</summary>
    internal const string BranchForm = "branch";

    internal const string NoTreeForm = "no-tree";

    internal const string ReadOnlyForm = "read-only";

    /// <summary>Whether the role's class cannot commit (<c>review</c> and <c>design</c>): only these may answer <c>read-only</c>.</summary>
    internal static bool IsReadOnlyClass(RosterContext c, string role) =>
        role.Length > 0 && c.Services.Config.RoleClass(role, c.Registry) is "review" or "design";

    /// <summary>The hold text for a member that answered <c>read-only</c> but can commit.</summary>
    internal const string CanCommitText = "your role can commit; answer with branch and sha";

    internal sealed record Answer(bool Ready, string Branch, string Sha, string Reason, string Form = BranchForm);

    /// <summary>The prefix of a checkpoint answer line.</summary>
    internal const string CheckpointPrefix = "Checkpoint";

    [GeneratedRegex("^(?:Release|Checkpoint): ready (?:branch[" + JsText.SpaceClass + "]*=[" + JsText.SpaceClass + "]*([^" + JsText.SpaceClass + "]+) sha[" + JsText.SpaceClass + "]*=[" + JsText.SpaceClass + "]*([0-9a-fA-F]{7,40})|(no-tree|read-only)) inflight[" + JsText.SpaceClass + "]*=[" + JsText.SpaceClass + "]*none\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReadyLine();

    [GeneratedRegex("^(?:Release|Checkpoint): not-ready reason=(.+)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex NotReadyLine();

    /// <summary>
    /// The member's answer: the first <c>Release:</c> or <c>Checkpoint:</c> line (the two asks take the same fields) under
    /// <c>## [1] status</c> of the response that is not inside a code fence, when it has one of the two forms. A leading
    /// <c>- </c>, surrounding backticks or quotes and whitespace around <c>=</c> are tolerated; the sha is not. Anything else
    /// under that heading, and any other section, is ignored.
    /// </summary>
    internal static Answer? ReadAnswer(RosterContext c, string responsePath)
    {
        var read = c.Services.Hierarchy.Core.ReadMsgFile(responsePath);
        return read.Member("text") is { IsText: true } text ? ParseAnswer(text.AsString()) : null;
    }

    /// <summary>The answer in the text of a response file; see <see cref="ReadAnswer"/>.</summary>
    internal static Answer? ParseAnswer(string text)
    {
        var inStatus = false;
        var fenced = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (fenced)
            {
                continue;
            }

            if (SectionHeading().Match(line) is { Success: true } heading)
            {
                inStatus = heading.Groups[1].Value == "status";
                continue;
            }

            if (!inStatus)
            {
                continue;
            }

            var body = line.Trim();
            if (body.StartsWith("- ", StringComparison.Ordinal))
            {
                body = body[2..].Trim();
            }

            body = body.Trim('`', '"', '\'').Trim();
            if (ReadyLine().Match(body) is { Success: true } ready)
            {
                return ready.Groups[3].Success
                    ? new Answer(true, string.Empty, string.Empty, string.Empty, ready.Groups[3].Value)
                    : new Answer(true, ready.Groups[1].Value, ready.Groups[2].Value, string.Empty);
            }

            if (NotReadyLine().Match(body) is { Success: true } not)
            {
                return new Answer(false, string.Empty, string.Empty, not.Groups[1].Value.Trim());
            }
        }

        return null;
    }

    [GeneratedRegex("^## \\[[0-9]+\\] ([a-z_]+)[" + JsText.SpaceClass + "]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SectionHeading();

    // ---------------------------------------------------------------- activity

    /// <summary>
    /// Whether the member's activity record says it is working or blocked now. A member that is live and busy has not stopped,
    /// whatever its answer said, and the record's own time is not compared with the answer: the activity writer throttles its
    /// writes, so a record can predate work started after the answer. Idle, no record, or an unreadable one is not busy.
    /// </summary>
    internal static bool WorkingNow(RosterContext c, string dir, JsonValue member, string name, JsonArray roster) =>
        MemberActivity(c, dir, member, name, () => roster, c.TeamFile) is { IsMap: true } record
        && HierarchyModule.RecordVerdict(record, c.Services.Clock.UtcNow.ToUnixTimeMilliseconds(), null, dispatchOpen: false) is "working" or "waiting" or "blocked";

    /// <summary>Working now, or stopped after a compaction: what a checkpoint and the clean-close token ask. A close does not hold on the second.</summary>
    internal static bool BusyNow(RosterContext c, string dir, JsonValue member, string name, JsonArray roster) =>
        MemberActivity(c, dir, member, name, () => roster, c.TeamFile) is { IsMap: true } record
        && HierarchyModule.RecordVerdict(record, c.Services.Clock.UtcNow.ToUnixTimeMilliseconds(), null, dispatchOpen: true) is "working" or "waiting" or "blocked" or "stalled-after-compact";

    /// <summary>The member's activity record: a pane member's by name, any other's by the row's session id, else its attributed one; a JSON null when none reads. The roster is read only for a row with no session id.</summary>
    internal static JsonValue MemberActivity(RosterContext c, string dir, JsonValue member, string name, Func<JsonArray> roster, string? team)
    {
        string? file;
        if (c.Services.Config.IsPaneMember(member))
        {
            file = $"pane-{name}.json";
        }
        else
        {
            var session = member.Member("session_id") is { IsText: true, IsTruthy: true } direct
                ? direct.AsString()
                : c.Services.Hierarchy.Roster.AttributedLiveness(roster(), name, team).Get("rec") is { IsMap: true } rec && rec.Member("session_id") is { IsText: true } sid ? sid.AsString() : null;
            file = session is null ? null : session + ".json";
        }

        return file is null ? JsonValue.Null : c.Services.Hierarchy.Status.ReadActivity(dir, file);
    }

    // ---------------------------------------------------------------- the work tree

    /// <summary>
    /// The work tree to read: a team member's recorded work root, else the work root of its launch directory; any other
    /// member's is the top level of the checkout holding the cwd on its peers row, found by the row's name or, for a row that
    /// has none, by its role@session name (the roster keeps the last row written per name, or per session for a nameless row, so
    /// the first match is the newest). Null when there is none.
    /// </summary>
    internal static string? WorkTree(RosterContext c, JsonValue member, string name, bool inTeam, JsonArray roster)
    {
        if (inTeam)
        {
            if (WorkTreeAssignment.RecordedRootOf(c, name) is { Length: > 0 } recorded)
            {
                return recorded;
            }

            return member.Member("expected_root") is { IsText: true, IsTruthy: true } expected ? WorkTreeAssignment.WorkRootOf(c, expected.AsString()) : null;
        }

        foreach (var row in roster)
        {
            var rowName = MemberPlanning.Text(row.Member("name")) ?? MemberPlanning.Text(c.Services.Hierarchy.Roster.SynthesizedPeerName(row));
            if (row.IsMap && rowName == name && row.Member("cwd") is { IsText: true, IsTruthy: true } cwd)
            {
                return WorkTreeAssignment.TreeOf(c, cwd.AsString())?.Root;
            }
        }

        return null;
    }

    internal sealed record TreeReading(string? Head, bool Dirty, string? Failure);

    /// <summary>The tree's HEAD and whether it is dirty; with <paramref name="member"/>, dirty only by <see cref="MembersDirt"/>.</summary>
    internal static TreeReading TreeState(RosterContext c, string tree, JsonValue? member = null)
    {
        var head = Git(c, tree, "rev-parse", "HEAD");
        if (!head.Ok)
        {
            return new TreeReading(null, false, $"git could not read the work tree {tree}: " + head.Detail);
        }

        var status = Git(c, tree, "status", "--porcelain", "--untracked-files=all", "--ignore-submodules=none");
        if (!status.Ok && !status.Truncated)
        {
            return new TreeReading(null, false, $"git could not read the work tree {tree}: " + status.Detail);
        }

        // the runner stops git once its output passes the cap; only whether anything is listed matters here, and more than the cap is listed
        var dirty = member is { } m ? MembersDirt(c, tree, m, status) : status.Truncated || status.Stdout.Trim().Length > 0;
        return new TreeReading(head.Stdout.Trim(), dirty, null);
    }

    /// <summary>
    /// Whether the dirt git lists in <paramref name="tree"/> is the member's: an untracked file whose mtime and ctime both predate the
    /// member's first launch in that tree (<c>first_spawned_at</c>, else <c>spawned_at</c>) is not. ctime counts because a copy or
    /// extract can set mtime back but not ctime.
    /// </summary>
    internal static bool MembersDirt(RosterContext c, string tree, JsonValue member, GitResult status)
    {
        if (status.Truncated)
        {
            return true;
        }

        var first = MemberPlanning.Text(member.Member("first_spawned_at")) ?? MemberPlanning.Text(member.Member("spawned_at"));
        var spawned = DateTimeOffset.TryParse(first, out var at) ? at.ToUnixTimeMilliseconds() * 1_000_000L : (long?)null;
        foreach (var line in status.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (spawned is not { } since || !line.StartsWith("?? ", StringComparison.Ordinal)
                || c.Services.Files.Stat(Path.Combine(tree, line[3..].Trim('"'))) is not { } stat || Math.Max(stat.MtimeNs, stat.CtimeNs) >= since)
            {
                return true;
            }
        }

        return false;
    }

    internal sealed record GitResult(bool Ok, string Stdout, string Detail, bool Truncated = false);

    internal static GitResult Git(RosterContext c, string tree, params string[] argv)
    {
        var result = c.Services.Launch!.Processes.Run(new ProcessRequest(["env", "-u", "GIT_DIR", "-u", "GIT_WORK_TREE", "-u", "GIT_INDEX_FILE", "-u", "GIT_COMMON_DIR", "-u", "GIT_CONFIG_PARAMETERS", "-u", "GIT_CONFIG_COUNT", "git", "-C", tree, .. argv], Timeout: TimeSpan.FromSeconds(30)));
        var stderr = Encoding.UTF8.GetString(result.Stderr.Span).Trim();
        var ok = result.ExitCode == 0 && !result.TimedOut;
        return new GitResult(ok, Encoding.UTF8.GetString(result.Stdout.Span), stderr.Length > 0 ? stderr : result.TimedOut ? "git timed out" : "git failed", result.Truncated && !result.TimedOut);
    }
}
