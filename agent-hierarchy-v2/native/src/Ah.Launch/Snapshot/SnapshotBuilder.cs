using System.Globalization;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Hierarchy.StateCalls;
using Ah.Mesh;
using Ah.Roster;

namespace Ah.Launch;

/// <summary>The snapshot document and the questions <c>resume</c> asks of a session: is it alive, which session does a member hold.</summary>
public interface ISnapshotBuilder
{
    /// <summary>
    /// The session id of <paramref name="member"/> of team <paramref name="teamName"/> (null: the default team) in pool <paramref name="dir"/>: the
    /// newest live registration that lists the member's address and was made for this team file, else the newest peer row of that name
    /// that carries one. <see cref="JsonValue.Null"/> when neither knows it; null when the registration holds none (absent from the document).
    /// </summary>
    JsonValue? SessionIdOf(string dir, string? teamName, JsonValue member);

    /// <summary>
    /// The snapshot document of the team, or null when there is no such team: per member the launch fields of its team-file row, its
    /// <c>session_id</c> and <c>thread_id</c> (null: not recorded yet); at team level the transport and the layout mode.
    /// </summary>
    JsonObject? BuildSnapshot(string dir, string? teamName, DateTimeOffset now);

    /// <summary>Whether a running <c>claude</c> process has <paramref name="sessionId"/> on its command line; null when <c>ps</c> could not be read.</summary>
    bool? ClaudeProcessHas(string sessionId);

    /// <summary>The command line of a running <c>claude</c> process that has <paramref name="sessionId"/> on it (null when none), read from one <c>ps</c>; not readable when <c>ps</c> failed.</summary>
    ClaudeLine ClaudeProcessLine(string sessionId);

    /// <summary>
    /// Why <paramref name="sessionId"/> counts as alive (<c>registration pid &lt;n&gt;</c>, <c>claude process</c>, or <c>ps failed</c> when the process
    /// list could not be read: doubt resolves to not launching), or null when nothing alive holds it.
    /// </summary>
    string? AliveReason(JsonValue? sessionId);
}

/// <summary>What a process listing says of one session: whether it could be read, and the matching <c>claude</c> command line if there is one.</summary>
/// <param name="Readable">False when <c>ps</c> failed or was cut off.</param>
/// <param name="Line">The command line, or null.</param>
public sealed record ClaudeLine(bool Readable, string? Line);

/// <summary>The snapshot builder.</summary>
public sealed record SnapshotBuilder(
    IFileReader Files,
    IProcessRunner Processes,
    IClock Clock,
    IConfigCore Config,
    IRosterCore Roster,
    IHierarchyRoster HierarchyRoster,
    IHierarchyCore Hierarchy,
    IMailbox Mailbox,
    IHierarchyStateCalls StateCalls,
    IHookErrorLog? HookErrors = null) : ISnapshotBuilder
{
    /// <summary>A registration's start-time tolerance: <c>ps</c> prints seconds, and the file is written just after its process starts.</summary>
    private const double StartSlackMs = 2000;

    private static readonly string[] LstartFormats = ["ddd MMM d HH:mm:ss yyyy", "ddd MMM dd HH:mm:ss yyyy"];

    public JsonValue? SessionIdOf(string dir, string? teamName, JsonValue member)
    {
        if (member.Member("name") is not { IsTruthy: true } nameValue)
        {
            return JsonValue.Null;
        }

        var name = JsConvert.ToJsString(nameValue);
        var file = NodePath.Resolve(dir, Roster.TeamPath(dir, teamName));
        var matching = new List<JsonValue>();
        foreach (JsonValue r in Mailbox.LiveRegistrations())
        {
            if (!ListsMember(r, name))
            {
                continue;
            }

            if (TeamFileOf(r, name) is { IsTruthy: true } teamFile && NodePath.Resolve(dir, JsConvert.ToJsString(teamFile)) != file)
            {
                continue;
            }

            matching.Add(r);
        }

        if (matching.Count > 0)
        {
            var newest = matching.OrderByDescending(r => AtOf(r), StringComparer.Ordinal).First();
            return newest.Member("session_id");
        }

        foreach (var row in HierarchyRoster.AttributedRoster(dir))
        {
            if (row.Member("name") is { } n && JsConvert.ToJsString(n) == name && row.Member("session_id") is { IsTruthy: true } sid)
            {
                return sid;
            }
        }

        return JsonValue.Null;
    }

    public JsonObject? BuildSnapshot(string dir, string? teamName, DateTimeOffset now)
    {
        var team = Roster.ReadTeamRecord(dir, teamName);
        if (team.Member("members") is not { IsArray: true } members)
        {
            return null;
        }

        var rows = new JsonArray();
        foreach (var m in members.AsArray())
        {
            var row = new JsonObject { ["name"] = OrNull(m.Member("name")) };
            if (m.Member("role") is { } role)
            {
                row["role"] = role;
            }

            row["kind"] = Config.ResolveKind(m);
            row["route"] = OrNull(m.Member("route"));
            row["model"] = OrNull(m.Member("model"));
            row["effort"] = OrNull(m.Member("effort"));
            row["autoMode"] = OrNull(m.Member("autoMode"));
            if (m.Member("args") is { IsArray: true } args)
            {
                var copy = new JsonArray();
                copy.AddRange(args.AsArray());
                row["args"] = copy;
            }
            else
            {
                row["args"] = JsonValue.Null;
            }

            row["stream"] = OrNull(m.Member("stream"));
            row["expected_root"] = OrNull(m.Member("expected_root"));
            var sid = Config.RouteHasPane(m.Member("route")) ? SessionIdOf(dir, teamName, m) : JsonValue.Null;
            if (sid is { } s)
            {
                row["session_id"] = s;
            }

            row["thread_id"] = JsonValue.Null;
            rows.Add(row);
        }

        var doc = new JsonObject
        {
            ["v"] = 1,
            ["kind"] = "ah-team-snapshot",
            ["team"] = teamName is not null ? teamName : JsonValue.Null,
            ["team_id"] = OrNull(team.Member("team_id")),
            ["taken_at"] = Timestamps.ToIsoString(now),
            ["transport"] = OrNull(team.Member("transport")),
            ["layout"] = OrNull(team.Member("layout")),
            ["members"] = rows,
        };
        if (team.Member("streams") is { IsMap: true } streams && streams.AsObject().Count > 0)
        {
            doc["streams"] = streams;
        }

        AddWorkTrees(dir, teamName, doc);
        return doc;
    }

    /// <summary>
    /// Adds to a snapshot document the team's gate mode and each member's recorded work tree, so a resume can put the member
    /// back where it worked. A team with no grants document, and a member with no record, add nothing.
    /// </summary>
    private void AddWorkTrees(string dir, string? teamName, JsonObject doc)
    {
        var name = teamName ?? "team";
        var home = Roster.TeamHome(dir);
        try
        {
            var status = StateCalls.Grants("status", new JsonObject { ["dir"] = home, ["team"] = name });
            if (!status.IsMap || status.Member("present") is not { Kind: JsonKind.Bool, IsTruthy: true } || status.Member("mode") is not { IsText: true } mode)
            {
                return;
            }

            doc["gate_mode"] = mode;
            if (doc.Get("members") is not { IsArray: true } list)
            {
                return;
            }

            var recorded = status.Member("members") is { IsMap: true } byName ? byName : (JsonValue?)null;
            foreach (var m in list.AsArray())
            {
                if (!m.IsMap || m.Member("name") is not { IsText: true } member)
                {
                    continue;
                }

                var one = recorded is { } rows
                    ? rows.Member(member.AsString())
                    : StateCalls.Grants("status", new JsonObject { ["dir"] = home, ["team"] = name, ["member"] = member });
                if (one is { IsMap: true } row && row.Member("work_root") is { IsText: true } root)
                {
                    m.AsObject()["work_root"] = root;
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // a snapshot without the work-tree fields still resumes, in each member's expected root
            HookErrors?.Log(new HookErrorRecord("snapshot", $"the work trees of team {name} were not added to its snapshot: {e.Message}"));
        }
    }

    public bool? ClaudeProcessHas(string sessionId) => ClaudeProcessLine(sessionId) is { Readable: true } read ? read.Line is not null : null;

    public ClaudeLine ClaudeProcessLine(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        // -ww: ps cuts a command line to the terminal width otherwise, and a session id sits late in `claude --resume <id> …`.
        var result = Processes.Run(new ProcessRequest(["ps", "-axww", "-o", "command="], null, null, default, null, 16 * 1024 * 1024));
        if (result is not { ExitCode: 0, TimedOut: false, Truncated: false })
        {
            return new ClaudeLine(false, null);
        }

        var text = Utf8Text.Decode(result.Stdout.Span);
        foreach (var line in text.Split('\n'))
        {
            if (line.Contains(sessionId, StringComparison.Ordinal) && HasClaudeWord(line))
            {
                return new ClaudeLine(true, line);
            }
        }

        return new ClaudeLine(true, null);
    }

    /// <summary>
    /// Whether <paramref name="line"/> holds <c>claude</c> as a word: after the start, a <c>/</c> or JavaScript white space, and
    /// before the end or JavaScript white space.
    /// </summary>
    internal static bool HasClaudeWord(string line)
    {
        const string Word = "claude";
        for (var at = line.IndexOf(Word, StringComparison.Ordinal); at >= 0; at = line.IndexOf(Word, at + 1, StringComparison.Ordinal))
        {
            var end = at + Word.Length;
            var openEdge = at == 0 || line[at - 1] == '/' || JsText.IsSpace(line[at - 1]);
            var closeEdge = end == line.Length || JsText.IsSpace(line[end]);
            if (openEdge && closeEdge)
            {
                return true;
            }
        }

        return false;
    }

    public string? AliveReason(JsonValue? sessionId)
    {
        if (sessionId is not { IsTruthy: true } sid)
        {
            return null;
        }

        var id = JsConvert.ToJsString(sid);
        var reg = Mailbox.ReadRegistration(id) is { } found ? (JsonValue)found : JsonValue.Null;
        if (RegistrantAlive(reg, id))
        {
            return $"registration pid {JsConvert.ToJsString(reg.Member("pid"))}";
        }

        return ClaudeProcessHas(id) switch
        {
            null => "ps failed",
            true => "claude process",
            _ => null,
        };
    }

    /// <summary>
    /// Whether the pid a registration recorded is still the process that registered: alive, and started no later than the registration
    /// file's last write (plus the slack). When <c>ps</c> fails or prints nothing for a live pid it is taken to be the registrant.
    /// </summary>
    private bool RegistrantAlive(JsonValue reg, string sessionId)
    {
        if (!reg.IsTruthy)
        {
            return false;
        }

        var pid = reg.Member("pid") ?? JsonValue.Null;
        if (!Hierarchy.PidAlive(pid))
        {
            return false;
        }

        double started;
        double wrote;
        try
        {
            // LC_ALL=C: lstart is printed in the locale's own words otherwise, which a date parse cannot read.
            var ps = Processes.Run(new ProcessRequest(
                ["ps", "-o", "lstart=", "-p", JsConvert.ToJsString(pid)],
                null,
                new Dictionary<string, string?> { ["LC_ALL"] = "C" },
                default,
                null,
                1 << 20));
            if (ps.ExitCode != 0)
            {
                return true;
            }

            started = ParseLstart(Utf8Text.Decode(ps.Stdout.Span));
            if ((Mailbox.RegistrationWrittenMs(sessionId) ?? Files.Stat(Mailbox.RegistrationFile(sessionId))?.MtimeMs) is not { } written)
            {
                return true;
            }

            wrote = written;
        }
        catch (Exception)
        {
            return true;
        }

        if (double.IsNaN(started) || double.IsNaN(wrote))
        {
            return true;
        }

        return started <= wrote + StartSlackMs;
    }

    /// <summary>Each run of JavaScript white space as one space: <c>text.replace(/\s+/g, " ")</c>.</summary>
    internal static string CollapseSpace(string text)
    {
        var collapsed = new System.Text.StringBuilder(text.Length);
        var inRun = false;
        foreach (var c in text)
        {
            if (!JsText.IsSpace(c))
            {
                collapsed.Append(c);
                inRun = false;
            }
            else if (!inRun)
            {
                collapsed.Append(' ');
                inRun = true;
            }
        }

        return collapsed.ToString();
    }

    /// <summary><c>Date.parse</c> of a <c>ps lstart</c> text (local time, seconds resolution): epoch milliseconds, or NaN.</summary>
    private double ParseLstart(string text)
    {
        var collapsed = CollapseSpace(JsText.Trim(text));
        if (!DateTime.TryParseExact(collapsed, LstartFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
        {
            return double.NaN;
        }

        var guess = new DateTimeOffset(DateTime.SpecifyKind(wall, DateTimeKind.Utc));
        var offset = Clock.LocalOffset(guess);
        var instant = guess - offset;
        offset = Clock.LocalOffset(instant);
        return (guess - offset).ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// The team file of the address <paramref name="name"/> holds in the registration. A session with two identities (a
    /// sub-orchestrator is a member of its manager's team and the orchestrator of its own) lists each address's file in
    /// <c>team_files</c>; the single <c>team_file</c> is only the primary address's.
    /// </summary>
    private static JsonValue? TeamFileOf(JsonValue registration, string name)
    {
        if (registration.Member("team_files") is { IsMap: true } files)
        {
            var map = files.AsObject();
            foreach (var address in map.Keys)
            {
                var at = address.IndexOf('@', StringComparison.Ordinal);
                if ((at < 0 ? address : address[..at]) == name && map.Get(address) is { IsText: true } file)
                {
                    return file;
                }
            }
        }

        return registration.Member("team_file");
    }

    private static bool ListsMember(JsonValue registration, string name)
    {
        JsonValue? addresses = registration.Member("addresses") is { IsTruthy: true } list ? list : (JsonValue?)null;
        IEnumerable<JsonValue> items;
        if (addresses is { IsArray: true } array)
        {
            items = array.AsArray();
        }
        else
        {
            items = [registration.Member("address") ?? JsonValue.Null];
        }

        foreach (var a in items)
        {
            var text = JsConvert.ToJsString(a);
            var at = text.IndexOf('@', StringComparison.Ordinal);
            if ((at < 0 ? text : text[..at]) == name)
            {
                return true;
            }
        }

        return false;
    }

    private static string AtOf(JsonValue registration) =>
        registration.Member("at") is { IsTruthy: true } at ? JsConvert.ToJsString(at) : string.Empty;

    private static JsonValue OrNull(JsonValue? value) => value is { IsNull: false } v ? v : JsonValue.Null;
}
