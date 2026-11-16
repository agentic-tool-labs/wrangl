using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Roster;

namespace Ah.Watch;

/// <summary>The multiplexer probe a view build asks: where a member's pane is placed. It reads no store.</summary>
public interface IViewPlacement
{
    /// <summary><c>placementOf(row, transport)</c>.</summary>
    JsonObject Placement(JsonObject memberRow, string? transport);
}

/// <summary>The services a view build reads through.</summary>
/// <param name="Files">The file reader.</param>
/// <param name="Streams">The stream rules.</param>
/// <param name="Placement">The multiplexer probe for a member's placement.</param>
/// <param name="Processes">The process table, for a session row's process start time; null skips that check.</param>
/// <param name="Git">Runs the read-only git commands of the orchestrator detail; null serves those fields as unavailable.</param>
/// <param name="Home">The user's home directory, where the statusline tap writes; null serves the tap as off.</param>
/// <param name="State">The read-only state reader; null reads through the library members above.</param>
/// <param name="Layout">The path computations that read no store.</param>
/// <param name="Config">The configuration question a view build asks.</param>
/// <param name="Verdict">The member verdict of an activity record, or null to show the record's own activity.</param>
/// <param name="StoredBytes">A sealed message's stored body from the msgs store, null when it has none; null to read files only.</param>
/// <param name="PidRunning">The hierarchy core's process seam (one-liveness.md §3), or null to keep the table's own check.</param>
public sealed record ViewSourceServices(
    IFileReader Files,
    StreamDerivations Streams,
    IViewPlacement Placement,
    IProcessTable? Processes = null,
    IProcessRunner? Git = null,
    string? Home = null,
    IViewStateReader? State = null,
    IViewLayout? Layout = null,
    IViewConfig? Config = null,
    Func<JsonValue, string>? Verdict = null,
    Func<string, byte[]?>? StoredBytes = null,
    Func<JsonValue, JsonValue?, Ah.Core.Time.LivenessPolicy, bool>? PidRunning = null)
{
    /// <summary>Narrows the hierarchy dirs the source lists; null lists them all.</summary>
    public Func<IReadOnlyList<string>, IReadOnlyList<string>>? AdmitDirs { get; init; }

    /// <summary>The read model's manifest of the <c>summary</c> view, or null when there is none yet.</summary>
    public Func<byte[]?>? PublishedSummary { get; init; }

    /// <summary>What says a team changed: the change token once the database holds the teams, else the file's stamp; null stamps the file.</summary>
    public Func<string, string>? TeamStamp { get; init; }

    /// <summary>The sessions change token (any registration written or ended moves it); null holds no registered-dir list.</summary>
    public Func<long?>? SessionsToken { get; init; }
}

/// <summary>An <see cref="IViewSource"/> over the services that own each store.</summary>
internal sealed partial class ProductionViewSource(ViewSourceServices s) : IViewSource, IDetailReads
{
    private readonly GitRunner? _git = s.Git is null ? null : new GitRunner(s.Git, static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private readonly StatuslineTap? _tap = s.Home is null ? null : StatuslineTap.OnDisk(s.Home, static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public GitRunner? Git => _git;

    public StatuslineTap? Tap => _tap;

    private readonly IViewProgress? _progress = s.State is null ? null : new StateViewProgress(s.State);

    public IViewProgress? Progress => _progress;

    public string? ReadText(string path) => GitHead.TextOf(s.Files, path);

    public IReadOnlyList<string> Trees(string dir, IReadOnlyList<string> teams)
    {
        var names = new JsonArray();
        foreach (var team in teams)
        {
            names.Add(team);
        }

        return [.. ReadState("msgs", "trees", dir, new JsonObject { ["dir"] = dir, ["teams"] = names }).Member("trees")!.Value.AsArray().Select(static t => t.AsString())];
    }

    public IReadOnlyList<Ah.Hierarchy.ParkedQuestion> ParkedQuestions(string dir)
    {
        // per-dir-files-to-db §6c G5: one read; a store that cannot be read shows none
        return s.Files.ReadAskLog(Ah.Hierarchy.ParkedQuestions.PathIn(dir)).TryLines(out var lines) ? Ah.Hierarchy.ParkedQuestions.Fold(string.Join('\n', lines)) : [];
    }

    public JsonObject? PipelineRun(string dir, string? team)
    {
        var args = new JsonObject { ["dir"] = dir };
        if (team is not null)
        {
            args["team"] = team;
        }

        var run = ReadState("decisions", "pipeline-run", dir, args);
        return run.IsMap ? run.AsObject() : null;
    }

    public IReadOnlyList<DispatchCandidate> OpenDispatches(string sessionId, double nowMs) =>
        [.. ReadState("dispatches", "open", HomeDir, new JsonObject { ["session_id"] = sessionId, ["now_ms"] = nowMs }).Member("candidates")!.Value.AsArray().Select(DispatchCandidateJson.Read)];

    public IReadOnlyList<JsonObject> PeerRows(IReadOnlySet<string> types)
    {
        var rows = new List<JsonObject>();
        foreach (var record in ReadState("peer-records", "read", HomeDir, new JsonObject()).AsArray())
        {
            if (record.IsMap && record.AsObject() is { } row && row.Str("type") is { } type && types.Contains(type))
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    private IStreamRules? _streams;

    public IStreamRules Streams => _streams ??= new ViewStreamRules(s.Streams, s.Files, s.State);

    public IReadOnlyList<string> RegisteredHierarchyDirs()
    {
        var known = ReadState("known-dirs", "read", HomeDir, new JsonObject()).Member("dirs")!.Value.AsArray().Select(static d => d.AsString()).ToList();

        // b459 step 14: the registrations' dirs are held while the sessions token and the known dirs read the same; doctor's rebuild drops them
        var key = s.SessionsToken?.Invoke() is { } token ? token.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + string.Join('\n', known) : null;
        IReadOnlyList<string>? listed;
        lock (_dirsGate)
        {
            listed = key is not null && _heldDirs is { } held && held.Key == key ? held.Dirs : null;
        }

        if (listed is null)
        {
            // b432: one entry per registration's team file repeats a dir hundreds of times; every per-dir job (memory-forward) then reran per copy
            listed = [.. HierarchyDirsOf(Registrations().Select(static r => r.TeamFiles)).Concat(known).Distinct(StringComparer.Ordinal)];
            if (key is not null)
            {
                lock (_dirsGate)
                {
                    _heldDirs = (key, listed);
                }
            }
        }

        return s.AdmitDirs is { } admit ? admit(listed) : listed;
    }

    private readonly object _dirsGate = new();
    private (string Key, IReadOnlyList<string> Dirs)? _heldDirs;

    public void ForgetHeldDirs()
    {
        lock (_dirsGate)
        {
            _heldDirs = null;
        }
    }

    /// <summary>The hierarchy dir of each team file a registration names (<c>&lt;dir&gt;/teams/&lt;team&gt;.json</c>); a registration naming none has no dir.</summary>
    internal static IReadOnlyList<string> HierarchyDirsOf(IEnumerable<IReadOnlyList<string>> teamFilesOfRegistrations) =>
        [.. teamFilesOfRegistrations.SelectMany(static files => files).Select(static f => NodePath.Dirname(NodePath.Dirname(f)))];

    private string HomeDir => (s.Layout ?? throw new InvalidOperationException("the view source has no layout")).AhHome;

    private sealed record RegistrationRead(JsonObject Record, bool Live, IReadOnlyList<string> TeamFiles);

    private IEnumerable<RegistrationRead> Registrations() =>
        ReadState("mailbox", "registrations", HomeDir, new JsonObject()).Member("registrations")!.Value.AsArray().Select(static row => new RegistrationRead(
            row.Member("registration")!.Value.AsObject(),
            row.Member("live")!.Value.AsBool(),
            [.. row.Member("team_files")!.Value.AsArray().Select(static f => f.AsString())]));

    public bool DirExists(string path) => s.Files.Exists(path);

    public string RealPath(string path)
    {
        try
        {
            return s.Files.RealPath(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    public IReadOnlyList<string> TeamNames(string dir) =>
        [.. TeamFiles(dir).Where(static t => t.Name is not null).Select(static t => t.Name!)];

    public JsonObject? ReadTeam(string dir, string? name)
    {
        foreach (var team in ReadState("team", "read", dir, new JsonObject { ["dir"] = dir }).Member("teams")!.Value.AsArray())
        {
            var held = team.Member("name")!.Value;
            if ((held.IsNull ? null : held.AsString()) == name)
            {
                return team.Member("team")!.Value is { Kind: JsonKind.Map } record ? record.AsObject() : null;
            }
        }

        return null;
    }

    public string TeamPath(string dir, string? name) => TeamFiles(dir).First(t => t.Name == name).Path;

    private IEnumerable<(string? Name, string Path)> TeamFiles(string dir) =>
        ReadState("team", "names", dir, new JsonObject { ["dir"] = dir, ["include_default"] = true }).Member("teams")!.Value.AsArray()
            .Select(static t => (t.Member("name")!.Value is { IsNull: false } n ? n.AsString() : null, t.Member("path")!.Value.AsString()));

    public string BoxDirFor(string teamFile, string member) =>
        (s.Layout ?? throw new InvalidOperationException("the view source has no layout")).BoxDirFor(teamFile, member);

    public bool PidAlive(long pid) => s.Processes is not { } table || table.StartTime(pid) is not null;

    public Ah.Core.Time.StartMatch ProcessStartMatch(long pid, string recordedStart) =>
        s.Processes is { } table ? Ah.Core.Time.ProcessStart.Compare(table, pid, recordedStart) : Ah.Core.Time.StartMatch.Unknown;

    public bool OrchestratorAlive(long pid, string? recordedStart) =>
        s.PidRunning is { } running
            ? running(JsonValue.FromNumber(pid), recordedStart is null ? (JsonValue?)null : JsonValue.FromString(recordedStart), Ah.Core.Time.LivenessPolicy.RunningOrUnknown)
            : PidAlive(pid) && (s.Processes is not { } table || !Ah.Core.Time.ProcessIdentity.Reused(table, pid, recordedStart));

    public Func<string, (long? Pid, string? CheckoutRoot, string? Cwd)> RegistrationIndex()
    {
        var chosen = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var liveOf = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var read in Registrations())
        {
            if (read.Record.Str("session_id") is { } id
                && (!chosen.ContainsKey(id) || read.Live || !liveOf[id]))
            {
                chosen[id] = read.Record;
                liveOf[id] = read.Live;
            }
        }

        return id => chosen.TryGetValue(id, out var registration) ? PlaceOfRegistration(registration) : (null, null, null);
    }

    private (long? Pid, string? CheckoutRoot, string? Cwd) PlaceOfRegistration(JsonObject registration)
    {
        long? pid = registration.Int("pid") is > 0 and var p ? p : null;
        var cwd = registration.Str("cwd") is { Length: > 0 } recorded ? recorded : null;
        return (pid, HierarchyModule.CheckoutOfCwd(cwd, s.Files.Exists), cwd);
    }

    public bool IsEnabled(string dir) => (s.Config ?? throw new InvalidOperationException("the view source has no configuration seam")).EnabledFor(dir);

    /// <summary>
    /// The latest row of each session whose pid is alive and whose row was written at or after that process started: a pid the
    /// system has since given to another process holds no session. With no process table the start time is not checked.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, long>> LiveSessions(string dir) =>
        [.. SessionPidsOf(dir).Where(e => PidAlive(e.Value) && WrittenByThisProcess(dir, e.Key, e.Value))];

    private IEnumerable<KeyValuePair<string, long>> SessionPidsOf(string dir)
    {
        foreach (var row in ReadState("session-pids", "pids", dir, new JsonObject { ["dir"] = dir }).Member("sessions")!.Value.AsArray())
        {
            yield return new KeyValuePair<string, long>(row.Member("session_id")!.Value.AsString(), (long)row.Member("pid")!.Value.AsNumber());
        }
    }

    private JsonValue ReadState(string domain, string op, string dir, JsonObject args) =>
        (s.State ?? throw new InvalidOperationException("the view source has no state reader")).Read(domain, op, dir, args);

    private bool WrittenByThisProcess(string dir, string sessionId, long pid)
    {
        if (s.Processes is not { } table)
        {
            return true;
        }

        return Ah.Core.Time.ProcessStart.TryParseUtc(table.StartTime(pid), out var started)
               && ReadState("session-pids", "of-pid", dir, new JsonObject { ["dir"] = dir, ["pid"] = pid, ["not_before"] = started.ToString("O", System.Globalization.CultureInfo.InvariantCulture) })
                   .Member("rows")!.Value.AsArray().Any(r => r.Member("session_id")!.Value.AsString() == sessionId);
    }

    public string TeamState(JsonObject doc) =>
        RosterModule.TeamLifeOf(JsonValue.FromObject(doc), LiveRegistrationOf, s.Processes, RegisteredProcesses()) switch
        {
            TeamLife.OwnedLive => "live",
            TeamLife.Orphaned => "orphaned",
            _ => "unknown",
        };

    private List<(long Pid, string? Start)> RegisteredProcesses() =>
        [.. Registrations().Select(static r => (r.Record.Int("pid") is > 0 and var pid ? pid : 0L, r.Record.Str("pid_start") is { Length: > 0 } start ? start : (string?)null))];

    private (long Pid, string Start)? LiveRegistrationOf(string sessionId)
    {
        foreach (var read in Registrations())
        {
            if (read.Live && read.Record.Str("session_id") == sessionId && read.Record.Int("pid") is > 0 and var pid
                && read.Record.Str("pid_start") is { Length: > 0 } start)
            {
                return (pid, start);
            }
        }

        return null;
    }

    public JsonObject ComputeStatus(string dir, double nowMs) =>
        ReadState("status", "compute", dir, new JsonObject { ["dir"] = dir, ["now_ms"] = nowMs }).AsObject();

    public IReadOnlyDictionary<string, JsonObject>? PublishedTeamSummaries()
    {
        if (s.PublishedSummary?.Invoke() is not { } manifest || JsonText.Parse(manifest).Member("slices") is not { IsArray: true } slices)
        {
            return null;
        }

        var summaries = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var slice in slices.AsArray())
        {
            if (slice.Member("id") is { IsText: true } id && slice.Member("body") is { IsMap: true } body)
            {
                summaries[id.AsString()] = body.AsObject();
            }
        }

        return summaries;
    }

    public string Clean(string text, int cap) => s.Streams.Clean(text, cap);

    public (int Unacked, int Overdue) BoxCounts(string boxDir, double nowMs)
    {
        var counts = ReadState("mailbox", "counts", boxDir, new JsonObject { ["dir"] = boxDir, ["now_ms"] = nowMs });
        return ((int)counts.Member("unacked")!.Value.AsNumber(), (int)counts.Member("overdue")!.Value.AsNumber());
    }

    public IReadOnlyList<JsonObject> PendingJoins(string dir, string? team, double nowMs) => [];

    public JsonObject JoinDecisionCommands(string id) => new();

    public IReadOnlyList<JsonObject> Links(string dir, string team, double nowMs) =>
        WatchJson.Objects(ReadState("links", "read", dir, new JsonObject { ["dir"] = dir, ["team"] = team, ["now"] = nowMs }).Member("links")!.Value.AsArray());

    public double LinkLapseMs(JsonObject link)
    {
        var lapse = ReadState("links", "lapse", HomeDir, new JsonObject { ["link"] = link }).Member("lapse_ms")!.Value;
        return lapse.IsNumber ? lapse.AsNumber() : double.NaN;
    }

    public ActivityHistory? ActivityOf(string dir, string sessionId)
    {
        var record = ReadState("activity", "read", dir, new JsonObject { ["dir"] = dir, ["subject"] = sessionId });
        if (!record.IsMap)
        {
            return null;
        }

        var minutes = new List<long>();
        if (record.Member("minutes") is { IsArray: true } list)
        {
            foreach (var entry in list.AsArray())
            {
                if (entry.IsMap && entry.Member("busy") is { IsBool: true } busy && busy.AsBool() && entry.Member("m") is { IsNumber: true } m)
                {
                    minutes.Add((long)m.AsNumber());
                }
            }
        }

        minutes.Sort();
        long? used = record.Member("context")?.Member("used") is { IsNumber: true } fill && double.IsFinite(fill.AsNumber()) && fill.AsNumber() >= 0 ? (long)fill.AsNumber() : null;
        return new ActivityHistory(
            record.Member("state_since") is { IsText: true } since ? since.AsString() : null,
            minutes,
            used,
            record.Member("activity") is { IsText: true } activity ? (s.Verdict is { } verdict ? verdict(record) : activity.AsString()) : null,
            record.Member("context")?.Member("model") is { IsText: true } model ? model.AsString() : null,
            record.Member("context")?.Member("at") is { IsText: true } at ? at.AsString() : null);
    }

    private const int EntryTextCap = 200;
    private const int RequestTextBytes = 64 * 1024;

    [GeneratedRegex(@"\.claude/ah2/specs/[A-Za-z0-9._/-]+\.md", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SpecPath();

    public IReadOnlyList<JsonObject> BoxEntries(string boxDir, int limit, bool index = false)
    {
        IReadOnlyList<(JsonObject Entry, bool Acked)> read;
        try
        {
            read = BoxEntryReads(boxDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or JsonParseException)
        {
            return [];
        }

        return [.. read.TakeLast(Math.Max(0, limit)).Select(r => EntryRow(r.Entry, r.Acked, index))];
    }

    private IReadOnlyList<(JsonObject Entry, bool Acked)> BoxEntryReads(string boxDir) =>
        [.. ReadState("mailbox", "entries", boxDir, new JsonObject { ["dir"] = boxDir }).Member("entries")!.Value.AsArray()
            .Select(static row => (row.Member("entry")!.Value.AsObject(), row.Member("acked")!.Value.AsBool()))];

    public JsonObject? BoxEntry(string boxDir, string id, bool index = false)
    {
        IReadOnlyList<(JsonObject Entry, bool Acked)> read;
        try
        {
            read = BoxEntryReads(boxDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or JsonParseException)
        {
            return null;
        }

        var match = read.FirstOrDefault(r => r.Entry.Str("id") == id);
        return match.Entry is null ? null : EntryRow(match.Entry, match.Acked, index);
    }

    /// <summary>A ref's bytes: the stored body of a sealed message, else null.</summary>
    private byte[]? RefBytes(string path)
    {
        return s.StoredBytes?.Invoke(path);
    }

    private JsonObject EntryRow(JsonObject entry, bool acked, bool index)
    {
            var id = entry.Str("id");
            var refPath = entry.Str("ref");
            var digest = entry.Str("ref_sha256");
            var changed = false;
            string? spec = null;
            if (!index && !string.IsNullOrEmpty(refPath))
            {
                try
                {
                    if (RefBytes(refPath) is { } bytes)
                    {
                        changed = digest is not null && Convert.ToHexStringLower(SHA256.HashData(bytes)) != digest;
                        var match = SpecPath().Match(Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, RequestTextBytes)));
                        spec = match.Success ? match.Value : null;
                    }
                    else
                    {
                        changed = digest is not null;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or RegexMatchTimeoutException or PlatformNotSupportedException)
                {
                    changed = digest is not null;
                }
            }

            var row = new JsonObject
            {
                ["id"] = id is null ? JsonValue.Null : Clean(id, EntryTextCap),
                ["kind"] = entry.Str("kind") is { } kind ? Clean(kind, EntryTextCap) : JsonValue.Null,
                ["from"] = entry.Str("from") is { } from ? Clean(from, EntryTextCap) : JsonValue.Null,
                ["to"] = entry.Str("to") is { } to ? Clean(to, EntryTextCap) : JsonValue.Null,
                ["created"] = entry.Str("created") is { } created ? Clean(created, EntryTextCap) : JsonValue.Null,
                ["acked"] = acked,
                ["ref"] = refPath is null ? JsonValue.Null : Clean(refPath, 400),
            };
            if (!index)
            {
                row["changed"] = changed;
                row["spec"] = spec is null ? JsonValue.Null : Clean(spec, 400);
            }

        return row;
    }

    public IReadOnlyList<SubagentRun> SubagentRuns(double nowMs)
    {
        var runs = new Dictionary<string, (string Session, string Agent, string? Kind, string Started, string? Ended, string Status, string? AgentType)>(StringComparer.Ordinal);
        foreach (var record in ReadState("peer-records", "read", HomeDir, new JsonObject()).AsArray())
        {
            if (!record.IsMap || record.AsObject() is not { } row || row.Str("type") != "subagent" || row.Str("session_id") is not { Length: > 0 } session || row.Str("agent_id") is not { Length: > 0 } agent || row.Str("ts") is not { } ts)
            {
                continue;
            }

            var key = session + "|" + agent;
            _ = runs.TryGetValue(key, out var held);
            var kind = row.Str("kind") ?? held.Kind;
            var agentType = row.Str("agent_type") is { Length: > 0 } type ? type : held.AgentType;
            runs[key] = row.Str("status") == "started"
                ? (session, agent, kind, ts, null, "started", agentType)
                : (session, agent, kind, held.Started ?? ts, ts, "stopped", agentType);
        }

        return [.. runs.Values
            .Where(static r => r.Kind == "subagent")
            .OrderBy(static r => r.Started, StringComparer.Ordinal)
            .Select(r => new SubagentRun(
                Clean(r.Session, EntryTextCap),
                Clean(r.Agent, EntryTextCap),
                r.Status == "started" && Timestamps.TryParseMs(r.Started, out var startedMs) && nowMs - startedMs > HierarchyModule.SubagentInflightTtlMs ? "lapsed" : r.Status,
                r.Started,
                r.Ended,
                r.AgentType is null ? null : Clean(r.AgentType, EntryTextCap)))];
    }

    /// <summary>The path with its directory resolved through links, or null when that directory is not a hierarchy <c>msgs</c> directory or its <c>archive</c>.</summary>
    private static string? InMessageStore(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
        {
            return null;
        }

        try
        {
            // a stored message's directory may not exist, so the key names it (ah-msgs-db-only §6)
            var real = Path.GetDirectoryName(MsgRows.PathKey(path))!;
            var name = Path.GetFileName(real);
            var inStore = name == "msgs" || (name == "archive" && Path.GetFileName(Path.GetDirectoryName(real)) == "msgs");
            return inStore ? Path.Combine(real, Path.GetFileName(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool IsMessageFile(string path) => InMessageStore(path) is not null;

    public FileText? RequestText(string path, int maxBytes)
    {
        if (InMessageStore(path) is not { } inside)
        {
            return null;
        }

        try
        {
            if (s.StoredBytes?.Invoke(inside) is { } stored)
            {
                var take = Math.Min(stored.Length, maxBytes);
                return new FileText(Encoding.UTF8.GetString(stored, 0, take), stored.Length > take);
            }

            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    public string BoxStamp(string boxDir) => ReadState("mailbox", "stamp", boxDir, new JsonObject { ["dir"] = boxDir }).Member("stamp")!.Value.AsString();

    public string FileStamp(string path) => s.TeamStamp?.Invoke(path) ?? Ah.Core.Files.FileStamps.Of(s.Files, path);

    public string? MeshPolicy(JsonObject teamDoc) => null;

    public JsonObject Placement(JsonObject memberRow, string? transport) => s.Placement.Placement(memberRow, transport);
}
