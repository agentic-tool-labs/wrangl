using System.Globalization;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Watch.Model;

/// <summary>
/// The progress lane: one key per (canonical hierarchy directory, team). The lane feeds no slice of its own; it folds every
/// <c>&lt;stream&gt;.plan.jsonl</c> of the team into one record (the items, the changes the events imply, and what each stream is), and the
/// composers make the plan summary and the two plan lists from that record. A re-read asks <c>progress.events</c> only for the rows
/// after the byte offset the lane remembers for each stream, so its cost follows the new rows; a stream whose log shrank is folded again
/// from the start. The record carries event times from the log, never the clock. A re-read whose replies show nothing the record was built
/// from has moved keeps the record the last full re-read built, the same object (ah2-52 1a; <paramref name="skipQuiet"/> false, for tests only, never keeps it).
/// </summary>
internal sealed class ProgressLane(IViewStateReader reader, Func<IPlanFold> newFold, Func<IReadOnlyList<string>> canonicalDirs, Func<IReadOnlyList<string>>? knownDirs = null, Action<string>? reread = null, bool skipQuiet = true) : IModelLane
{
    public const string LaneName = "progress";

    internal const char Separator = '\u001f';

    private sealed class StreamState(IPlanFold fold)
    {
        public long Offset { get; set; }

        public IPlanFold Fold { get; set; } = fold;

        public bool Truncated { get; set; }
    }

    /// <summary>A stream checklist as <c>progress.checklists</c> answered it: its file signature, the digest of its text and the parsed fields.</summary>
    private sealed record Sheet(string Sig, string Hash, JsonValue Data);

    /// <summary>What the lane holds for one (directory, team): the plan logs, the checklists, and when each item was first seen done.</summary>
    private sealed class KeyState
    {
        public Dictionary<string, StreamState> Plans { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Sheet> Sheets { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> ItemStates { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, long> DoneSeen { get; } = new(StringComparer.Ordinal);

        public bool Seeded { get; set; }

        /// <summary>The record the last full re-read built, and the <c>progress.streams</c> reply it was built from; null until one has, and while a re-read is under way.</summary>
        public LaneRecord? Held { get; set; }

        public byte[]? HeldStreams { get; set; }
    }

    private readonly Dictionary<string, KeyState> _keys = new(StringComparer.Ordinal);

    private long _quietSkips;

    /// <summary>How many re-reads kept the held record because nothing it shows had moved, for the tests of the skip.</summary>
    internal long QuietSkips => Interlocked.Read(ref _quietSkips);
    private readonly HashSet<string> _read = new(StringComparer.Ordinal);
    private HashSet<string>? _listed;

    // Every change of a team, newest first, as the last re-read built it. Each array is complete before it is stored and never changed after,
    // so the list pager reads it from a request thread while the projector replaces it.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, JsonArray> _changes = new(StringComparer.Ordinal);

    // The items body of each stream of a team (see PlanItemsLane), complete before it is stored and never changed after.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyDictionary<string, JsonValue>> _itemBodies = new(StringComparer.Ordinal);

    public string Name => LaneName;

    public static string KeyOf(string dir, string team) => dir + Separator + team;

    /// <summary>The part of a plan slice id after its head: <c>&lt;dirId&gt;/&lt;team&gt;</c>.</summary>
    public static string SuffixOf(string dir, string team) => ActivityLane.DirId(dir) + "/" + team;

    internal static (string Dir, string Team) Split(string key)
    {
        var at = key.IndexOf(Separator, StringComparison.Ordinal);
        return at < 0 ? throw new ArgumentException("not a progress key", nameof(key)) : (key[..at], key[(at + 1)..]);
    }

    public IReadOnlyList<string> SliceIds(string key) => [];

    /// <summary>The changes the lane holds for the plan slice suffix <c>&lt;dirId&gt;/&lt;team&gt;</c>, newest first; null when it holds none.</summary>
    public JsonArray? PlanChangesOf(string suffix) => _changes.GetValueOrDefault(suffix);

    /// <summary>The body of the items slice of <paramref name="stream"/> in the team with the plan slice suffix <paramref name="suffix"/>; null when the lane holds no such stream.</summary>
    public JsonValue? ItemsBodyOf(string suffix, string stream) =>
        _itemBodies.TryGetValue(suffix, out var bodies) && bodies.TryGetValue(stream, out var body) ? (JsonValue?)body : null;

    public LaneResult Reread(string key, long nowMs)
    {
        var (dir, team) = Split(key);
        _ = _read.Add(key);
        if (knownDirs is not null && !knownDirs().Contains(dir, StringComparer.Ordinal))
        {
            Forget(key, dir, team);
            return new LaneResult([]) { Record = LaneRecord.Absent };
        }

        if (!_keys.TryGetValue(key, out var held))
        {
            held = new KeyState();
            _keys[key] = held;
        }

        // The hold is taken off for the re-read and put back only by the skip or a full build: a re-read that throws part way may have
        // folded rows the held record does not show, so the next one must build in full.
        var prior = held.Held;
        var priorStreams = held.HeldStreams;
        held.Held = null;
        held.HeldStreams = null;
        var moved = prior is null;
        var states = held.Plans;
        var offsets = new JsonObject();
        foreach (var (stream, state) in states)
        {
            offsets[stream] = state.Offset;
        }

        var events = reader.Read("progress", "events", dir, new JsonObject { ["dir"] = dir, ["team"] = team, ["after_offsets"] = offsets });
        var present = new HashSet<string>(StringComparer.Ordinal);
        if (events.Member("streams") is { IsArray: true } listed)
        {
            foreach (var s in listed.AsArray())
            {
                if (s.Member("stream") is not { IsText: true } name)
                {
                    continue;
                }

                _ = present.Add(name.AsString());
                if (!states.TryGetValue(name.AsString(), out var state))
                {
                    state = new StreamState(newFold());
                    states[name.AsString()] = state;
                    moved = true;
                }

                if (s.Member("reset") is { IsBool: true } reset && reset.AsBool())
                {
                    state.Fold = newFold();
                    state.Truncated = false;
                    moved = true;
                }

                if (s.Member("rows") is { IsArray: true } rows)
                {
                    moved |= rows.AsArray().Count > 0;
                    foreach (var row in rows.AsArray())
                    {
                        state.Fold.Apply(row);
                    }
                }

                if (s.Member("offset") is { IsNumber: true } offset)
                {
                    moved |= (long)offset.AsNumber() != state.Offset;
                    state.Offset = (long)offset.AsNumber();
                }
                else
                {
                    moved = true;
                }

                var truncated = s.Member("truncated") is { IsBool: true } cut && cut.AsBool();
                moved |= truncated;
                state.Truncated |= truncated;
            }
        }

        foreach (var gone in states.Keys.Where(k => !present.Contains(k)).ToList())
        {
            _ = states.Remove(gone);
            moved = true;
        }

        moved |= ReadSheets(held, dir, team);
        var described = reader.Read("progress", "streams", dir, new JsonObject { ["dir"] = dir, ["team"] = team });
        var streamsText = JsonText.ToUtf8(described, 0);
        if (skipQuiet && !moved && prior is not null && priorStreams is not null && streamsText.AsSpan().SequenceEqual(priorStreams) && SameTeamExists(prior, dir, team))
        {
            held.Held = prior;
            held.HeldStreams = priorStreams;
            _ = Interlocked.Increment(ref _quietSkips);
            reread?.Invoke(key);
            return new LaneResult([]) { Record = prior };
        }

        var record = RecordOf(dir, team, held, described, nowMs, () => TeamExists(reader, dir, team), out var bodies);
        if (record is null)
        {
            Forget(key, dir, team);
            // a folder with no streams can still hold a ledger the Sitrep reads (ah-ui-ia §3.3a)
            reread?.Invoke(key);
            return new LaneResult([]) { Record = LaneRecord.Absent };
        }

        _changes[SuffixOf(dir, team)] = record.Value.Member("changes")!.Value.AsArray();
        _itemBodies[SuffixOf(dir, team)] = bodies;
        held.Held = record;
        held.HeldStreams = streamsText;
        reread?.Invoke(key);
        return new LaneResult([]) { Record = record };
    }

    /// <summary>Whether the team-exists fact the held record carries is still so: asked again only when the record holds an item waiting on the user, as the full build asks.</summary>
    private bool SameTeamExists(LaneRecord held, string dir, string team) =>
        held.Value.Member("work_items") is not { IsArray: true } items || !items.AsArray().Any(static i => LaneParts.Text(i, "state") == "waiting_user")
        || TeamExists(reader, dir, team) == (held.Value.Member("team_exists") is { IsBool: true } exists && exists.AsBool());

    private void Forget(string key, string dir, string team)
    {
        _ = _keys.Remove(key);
        _ = _changes.TryRemove(SuffixOf(dir, team), out _);
        _ = _itemBodies.TryRemove(SuffixOf(dir, team), out _);
    }

    /// <summary>
    /// Asks for the checklists whose file changed since the last read; a file whose text is the same as before keeps what was parsed from it.
    /// True when anything moved: a checklist not answered <c>unchanged</c>, or one added or gone.
    /// </summary>
    private bool ReadSheets(KeyState held, string dir, string team)
    {
        var moved = false;
        var known = new JsonObject();
        foreach (var (stream, sheet) in held.Sheets)
        {
            known[stream] = sheet.Sig;
        }

        var reply = reader.Read("progress", "checklists", dir, new JsonObject { ["dir"] = dir, ["team"] = team, ["known"] = known });
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (reply.Member("streams") is { IsArray: true } streams)
        {
            foreach (var s in streams.AsArray())
            {
                if (s.Member("stream") is not { IsText: true } name || s.Member("sig") is not { IsText: true } sig)
                {
                    continue;
                }

                var stream = name.AsString();
                _ = seen.Add(stream);
                // ponytail: sig is updated_ms:size and the writers stamp updated_ms with the clock, not max(now, old+1), so a rewrite in the
                // same millisecond at the same size reads unchanged. The parsed sheet already trusted that; the skip trusts it the same way.
                if (s.Member("unchanged") is { IsBool: true } same && same.AsBool())
                {
                    moved |= !held.Sheets.ContainsKey(stream);
                    continue;
                }

                moved = true;
                var hash = s.Member("hash") is { IsText: true } h ? h.AsString() : string.Empty;
                held.Sheets[stream] = held.Sheets.TryGetValue(stream, out var old) && old.Hash == hash ? old with { Sig = sig.AsString() } : new Sheet(sig.AsString(), hash, s);
            }
        }

        foreach (var gone in held.Sheets.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _ = held.Sheets.Remove(gone);
            moved = true;
        }

        return moved;
    }

    public IReadOnlyList<string> Keys()
    {
        var keys = new List<string>();
        foreach (var dir in canonicalDirs().Distinct(StringComparer.Ordinal))
        {
            JsonValue reply;
            try
            {
                reply = reader.Read("progress", "keys", dir, new JsonObject { ["dir"] = dir });
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (reply.Member("teams") is { IsArray: true } teams)
            {
                keys.AddRange(teams.AsArray().Where(static t => t.IsText).Select(t => KeyOf(dir, t.AsString())));
            }
        }

        _listed = [.. keys];
        return keys;
    }

    /// <summary>Whether the lane has yet to read the team's plan: the folders are not listed yet, or the team's is and has not been read. A zero from such a team says nothing.</summary>
    public bool PlanPending(string dir, string folder) => _listed is not { } listed || (listed.Contains(KeyOf(dir, folder)) && !_read.Contains(KeyOf(dir, folder)));

    internal static readonly string[] CountStateNames = ["done", "in_progress", "reported", "in_review", "approved", "next", "blocked", "waiting_user", "waiting_orch", "not_started", "dropped"];

    /// <summary>The name the teams lane gives the team whose progress folder is <paramref name="folder"/>: the default scope's folder is <c>_default</c>.</summary>
    internal static string TeamNameOf(string folder) => folder == "_default" ? TeamLane.DefaultName : folder;

    internal static string FolderOf(string teamName) => Ah.Hierarchy.HierarchyModule.ProgressFolderOf(teamName);

    internal const string PlanPrefix = "plan";

    /// <summary>The id of the plan slice of the team named <paramref name="teamName"/> in the hierarchy directory <paramref name="dir"/>; the slice may not exist.</summary>
    internal static string PlanSliceId(string dir, string teamName) => SliceIdOf(PlanPrefix, dir, FolderOf(teamName));

    /// <summary>The id of the slice with prefix <paramref name="prefix"/> for the progress record of <paramref name="folder"/> in <paramref name="dir"/>; every progress composer names its slices with this.</summary>
    internal static string SliceIdOf(string prefix, string dir, string folder) => prefix + ":" + SuffixOf(dir, folder);

    /// <summary>
    /// Whether a team still has a record of some kind: a row in the store, or a file, readable or not. Only a team with neither is gone. A read that fails says nothing, so the team counts as there.
    /// </summary>
    internal static bool TeamExists(IViewStateReader reader, string dir, string folder)
    {
        var name = TeamNameOf(folder);
        var isDefault = name == TeamLane.DefaultName;
        var path = isDefault ? dir + "/team.json" : dir + "/teams/" + name + ".json";
        try
        {
            return reader.Read("team", "read", dir, new JsonObject { ["path"] = path, ["dir"] = dir }).IsMap
                || (reader.Read("team", "keys", dir, new JsonObject { ["dir"] = dir }).Member("teams") is { IsArray: true } teams
                    && teams.AsArray().Any(t => t.Member("name") is { } listed && (isDefault ? listed.IsNull : listed.IsText && listed.AsString() == name)));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return true;
        }
    }

    private static LaneRecord? RecordOf(string dir, string team, KeyState held, JsonValue described, long nowMs, Func<bool> teamExists, out Dictionary<string, JsonValue> bodies)
    {
        bodies = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        var info = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        if (described.Member("streams") is { IsArray: true } infos)
        {
            foreach (var i in infos.AsArray())
            {
                if (i.Member("stream") is { IsText: true } name)
                {
                    info[name.AsString()] = i;
                }
            }
        }

        var names = held.Plans.Keys.Concat(held.Sheets.Keys).Concat(info.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (names.Count == 0)
        {
            return null;
        }

        var streams = new JsonArray();
        var items = new JsonArray();
        var workItems = new JsonArray();
        var changes = new List<(string At, string Stream, int Seq, JsonObject Row)>();
        var asOf = 0L;
        var stillThere = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stream in names)
        {
            var plan = held.Plans.GetValueOrDefault(stream);
            var sheet = held.Sheets.GetValueOrDefault(stream)?.Data;
            var facts = info.GetValueOrDefault(stream);
            var counts = new JsonObject();
            foreach (var state in CountStateNames)
            {
                counts[state] = 0;
            }

            var listed = new List<(string Key, string Title, string State, string? Holder, PlanItem? Plan)>();
            var streamOwner = Choose(Fact(facts, "owner"), Fact(facts, "owner"), SheetText(sheet, "owner"));
            long streamAsOf;
            if (plan is not null)
            {
                streamAsOf = TimeMs(plan.Fold.LastAt);
                var order = 0;
                foreach (var item in plan.Fold.State.Items)
                {
                    var title = HierarchyModule.ChecklistTitle(item.Title);
                    listed.Add((stream + ":" + item.Item, title, item.State, item.Holder, item));
                    if (item.State is "in_progress" or "waiting_user" or "blocked")
                    {
                        // what the team summary classifies; never served as it stands
                        workItems.Add(new JsonObject
                        {
                            ["key"] = stream + ":" + item.Item,
                            ["item"] = item.Item,
                            ["stream"] = stream,
                            ["title"] = title,
                            ["state"] = item.State,
                            ["owner"] = ItemOwner(item.Holder, streamOwner, item.State),
                            ["refs"] = StringArray(item.Refs ?? []),
                            ["note"] = item.Note is null ? JsonValue.Null : item.Note,
                            ["since"] = item.StateSince is null ? JsonValue.Null : item.StateSince,
                            ["last"] = (item.UpdatedAt ?? item.StateSince) is { } last ? last : JsonValue.Null,
                            ["options"] = StringArray(item.Options ?? []),
                            ["asker"] = item.AskedBy is null ? JsonValue.Null : item.AskedBy,
                            ["class"] = item.State == "waiting_user" ? HierarchyModule.ChecklistClassOf(item) : JsonValue.Null,
                        });
                    }

                    var row = new JsonObject
                    {
                        ["item"] = item.Item,
                        ["stream"] = stream,
                        ["seq"] = order++,
                        ["area"] = item.Area ?? stream,
                        ["title"] = title,
                        ["state"] = item.State,
                        ["holder"] = item.Holder is null ? JsonValue.Null : item.Holder,
                        ["rank"] = item.Rank is { } rank ? JsonValue.FromNumber(rank) : JsonValue.Null,
                        ["added_at"] = item.AddedAt,
                        ["started_at"] = item.StartedAt is null ? JsonValue.Null : item.StartedAt,
                        ["done_at"] = item.DoneAt is null ? JsonValue.Null : item.DoneAt,
                        ["note"] = item.Note is null ? JsonValue.Null : item.Note,
                        ["commits"] = StringArray(item.Commits),
                        ["branch"] = item.Branch is null ? JsonValue.Null : item.Branch,
                        ["issue"] = item.Issue is { } issue ? JsonValue.FromNumber(issue) : JsonValue.Null,
                    };
                    if (item.State == "reported" && item.Refs is { Count: > 0 } refs)
                    {
                        // ah2-268: the request the report answers, for the topology's edges.report
                        row["last_ref"] = refs[^1];
                    }

                    items.Add(row);
                }

                foreach (var c in plan.Fold.Changes)
                {
                    changes.Add((c.At, stream, c.Seq, new JsonObject
                    {
                        ["at"] = c.At,
                        ["seq"] = c.Seq,
                        ["stream"] = stream,
                        ["item"] = c.Item,
                        ["kind"] = c.Kind,
                        ["from"] = c.From is null ? JsonValue.Null : c.From,
                        ["to"] = c.To is null ? JsonValue.Null : c.To,
                        ["by"] = c.By,
                    }));
                }
            }
            else
            {
                streamAsOf = SheetNumber(sheet, "as_of");
                if (sheet?.Member("items") is { IsArray: true } rows)
                {
                    foreach (var row in rows.AsArray())
                    {
                        listed.Add((Text(row, "key") ?? string.Empty, Text(row, "title") ?? string.Empty, Text(row, "state") ?? "not_started", null, null));
                    }
                }
            }

            foreach (var item in listed)
            {
                if (counts.Get(item.State) is { IsNumber: true } n)
                {
                    counts[item.State] = (long)n.AsNumber() + 1;
                }
            }

            asOf = Math.Max(asOf, streamAsOf);
            var truncated = plan is not null ? plan.Truncated : sheet?.Member("truncated") is { IsBool: true } cut && cut.AsBool();
            var withSeen = new JsonArray();
            foreach (var item in listed)
            {
                _ = stillThere.Add(item.Key);
                var entry = new JsonObject { ["key"] = item.Key, ["title"] = item.Title, ["state"] = item.State, ["owner"] = ItemOwner(item.Holder, streamOwner, item.State) };
                if (item.Holder is not null)
                {
                    entry["holder"] = item.Holder;
                }

                if (item.Plan is { } planned)
                {
                    PlanFields(entry, planned);
                }

                var seen = DoneSeenAt(held, item.Key, item.State, nowMs);
                entry["done_seen_at"] = item.State == "done" && item.Plan?.StateSince is { } since && TimeMs(since) is > 0 and var doneAt ? JsonValue.FromNumber(doneAt) : seen;
                withSeen.Add(entry);
            }

            bodies[stream] = JsonValue.FromObject(new JsonObject { ["stream"] = stream, ["items"] = withSeen, ["truncated"] = truncated });
            streams.Add(new JsonObject
            {
                ["stream"] = stream,
                ["owner"] = streamOwner,
                ["branch"] = Choose(Fact(facts, "branch"), Fact(facts, "branch"), SheetText(sheet, "branch")),
                ["work_tree"] = Choose(Fact(facts, "work_tree"), Fact(facts, "work_tree_path"), SheetText(sheet, "work_tree"), Fact(facts, "work_tree")),
                ["goal"] = Choose(null, Fact(facts, "goal"), SheetText(sheet, "goal")),
                ["state"] = Choose(null, Fact(facts, "state"), SheetText(sheet, "md_state"), "open"),
                ["source"] = plan is not null ? "jsonl" : "checklist",
                ["counts"] = counts,
                ["as_of"] = streamAsOf,
                ["blockers"] = SheetNumber(sheet, "blockers"),
                ["warnings"] = plan is not null ? 0 : SheetNumber(sheet, "warnings"),
                ["truncated"] = truncated,
            });
        }

        foreach (var gone in held.ItemStates.Keys.Where(k => !stillThere.Contains(k)).ToList())
        {
            _ = held.ItemStates.Remove(gone);
            _ = held.DoneSeen.Remove(gone);
        }

        held.Seeded = true;
        var ordered = new JsonArray();
        foreach (var c in changes.OrderByDescending(static c => c.At, StringComparer.Ordinal).ThenByDescending(static c => c.Stream, StringComparer.Ordinal).ThenByDescending(static c => c.Seq))
        {
            ordered.Add(c.Row);
        }

        var value = new JsonObject
        {
            ["dir_id"] = ActivityLane.DirId(dir),
            ["team"] = team,
            ["streams"] = streams,
            ["items"] = items,
            ["work_items"] = workItems,
            ["changes"] = ordered,
        };

        // Read only when something waits on the user: TreeData.Teams uses it to keep that question on show while the team's record does not read.
        if (workItems.Any(static i => LaneParts.Text(i, "state") == "waiting_user") && teamExists())
        {
            value["team_exists"] = true;
        }

        return new LaneRecord(JsonValue.FromObject(value), asOf);
    }

    /// <summary>The checklist fields of a plan item, each present only when the item has it: role, todo, refs, when it entered its state, when it last changed, its kind and its parent.</summary>
    internal static void PlanFields(JsonObject entry, PlanItem plan)
    {
        if (plan.Role is not null)
        {
            entry["role"] = plan.Role;
        }

        if (plan.Todo is not null)
        {
            entry["todo"] = plan.Todo;
        }

        if (plan.Refs is { Count: > 0 } refs)
        {
            entry["refs"] = StringArray(refs);
        }

        if (plan.Answer is not null)
        {
            entry["answer"] = plan.Answer;
        }

        if (plan.StateSince is not null)
        {
            entry["state_since"] = plan.StateSince;
        }

        if (plan.UpdatedAt is not null)
        {
            entry["updated_at"] = plan.UpdatedAt;
        }

        if (plan.Kind is not null)
        {
            entry["kind"] = plan.Kind;
        }

        if (plan.Parent is not null)
        {
            entry["parent"] = plan.Parent;
        }
    }

    /// <summary>
    /// When the lane first saw an item done that has no record of when it got there, a sheet's: null for one that was done when the lane first read the team, else the time of the read
    /// that found it done after it was seen not done (or that found it new and done). Nothing but a read changes it, so a touch of a file keeps it. A plan item is dated by its own record.
    /// </summary>
    private static JsonValue DoneSeenAt(KeyState held, string key, string state, long nowMs)
    {
        var known = held.ItemStates.TryGetValue(key, out var was);
        held.ItemStates[key] = state;
        if (state != "done")
        {
            _ = held.DoneSeen.Remove(key);
            return JsonValue.Null;
        }

        if ((!known || was != "done") && held.Seeded)
        {
            held.DoneSeen[key] = nowMs;
        }

        return held.DoneSeen.TryGetValue(key, out var at) ? JsonValue.FromNumber(at) : JsonValue.Null;
    }

    private static long TimeMs(string at) =>
        DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when.ToUnixTimeMilliseconds() : 0;

    private static string? Text(JsonValue row, string name) => row.Member(name) is { IsText: true } t ? t.AsString() : null;

    private static string? SheetText(JsonValue? sheet, string name) => sheet?.Member(name) is { IsText: true } t ? t.AsString() : null;

    private static long SheetNumber(JsonValue? sheet, string name) => sheet?.Member(name) is { IsNumber: true } n ? (long)n.AsNumber() : 0;

    private static string? Fact(JsonValue? facts, string name) => facts?.Member(name) is { IsText: true } t ? t.AsString() : null;

    /// <summary>
    /// Who an item is with: its own holder, else its stream's owner, else nobody. An item nobody has claimed (<c>next</c>, <c>not_started</c>) is with nobody whoever owns its stream:
    /// the queue calls it unowned, and a card must not name a member for it. A sheet writes <c>—</c> for no holder, which says nothing, as in <see cref="Choose"/>.
    /// </summary>
    internal static string ItemOwner(string? holder, JsonValue streamOwner, string state) =>
        holder is { Length: > 0 } and not "—" ? holder
        : state is "next" or "not_started" ? string.Empty
        : streamOwner.NonEmptyText is { } owner and not "—" ? owner : string.Empty;

    /// <summary>The first of <paramref name="order"/> that says something (not null, not <c>—</c>); else <paramref name="legacy"/> as the team file gave it; else null.</summary>
    private static JsonValue Choose(string? legacy, params string?[] order)
    {
        foreach (var candidate in order)
        {
            if (candidate is not null and not "—")
            {
                return candidate;
            }
        }

        return legacy is not null ? legacy : JsonValue.Null;
    }

    private static JsonArray StringArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var v in values)
        {
            array.Add(v);
        }

        return array;
    }
}

/// <summary>
/// A composer over the progress lane's record of one (directory, team): the id after the prefix is <c>&lt;dirId&gt;/&lt;team&gt;</c>. A
/// team the lane keeps no record for has no slice.
/// </summary>
internal abstract class ProgressComposer(string slicePrefix) : ISliceComposer
{
    public string SlicePrefix { get; } = slicePrefix;

    public virtual IReadOnlyList<string> Lanes { get; } = [ProgressLane.LaneName];

    public IReadOnlyList<string> Affected(LaneKey changed, LaneRecord? previous, LaneRecord? current, IRecordView records)
    {
        if (changed.Lane is TeamLane.LaneName or MessagesLane.LaneName or WorktreeLane.LaneName)
        {
            var wanted = SummaryData.TeamsAffected(changed, previous, current, records).Select(static t => (t.DirId, Folder: ProgressLane.FolderOf(t.Team))).ToHashSet();
            return [.. records.Keys(ProgressLane.LaneName).Select(k => ProgressLane.Split(k)).Where(p => wanted.Contains((ActivityLane.DirId(p.Dir), p.Team))).Select(p => IdOf(p.Dir, p.Team))];
        }

        if (changed.Lane == DecisionsLane.LaneName)
        {
            var changedDir = LaneParts.Split(changed.Key).First;
            return [.. records.Keys(ProgressLane.LaneName).Select(k => ProgressLane.Split(k)).Where(p => p.Dir == changedDir).Select(p => IdOf(p.Dir, p.Team))];
        }

        var (dir, team) = ProgressLane.Split(changed.Key);
        return [IdOf(dir, team)];
    }

    public IReadOnlyList<string> AllIds(IRecordView records) =>
        [.. records.Keys(ProgressLane.LaneName).Select(k => ProgressLane.Split(k)).Select(p => IdOf(p.Dir, p.Team))];

    public SliceChange Compose(string sliceId, IRecordView records)
    {
        var head = SlicePrefix + ":";
        var suffix = sliceId.StartsWith(head, StringComparison.Ordinal) ? sliceId[head.Length..] : throw new ArgumentException("not a slice of " + SlicePrefix, nameof(sliceId));
        foreach (var key in records.Keys(ProgressLane.LaneName))
        {
            var (dir, team) = ProgressLane.Split(key);
            if (ProgressLane.SuffixOf(dir, team) == suffix && records.Get(ProgressLane.LaneName, key) is { } record)
            {
                return new SliceChange(sliceId, JsonText.ToUtf8(JsonValue.FromObject(BodyOf(dir, team, record, records)), 0), record.AsOfMs);
            }
        }

        return new SliceChange(sliceId, null);
    }

    protected abstract JsonObject BodyOf(string team, LaneRecord record);

    protected virtual JsonObject BodyOf(string dir, string team, LaneRecord record, IRecordView records) => BodyOf(team, record);

    private string IdOf(string dir, string team) => ProgressLane.SliceIdOf(SlicePrefix, dir, team);
}

/// <summary>
/// <c>plan:&lt;dirId&gt;/&lt;team&gt;</c>: the team's streams (what each is, its state, and how many items are in each state) and the team's totals. It carries no
/// items. A stream is under review when the team has an open exchange whose slug is the stream and whose receiver is a reviewer.
/// </summary>
internal sealed class PlanSummaryComposer() : ProgressComposer(ProgressLane.PlanPrefix)
{
    public override IReadOnlyList<string> Lanes { get; } = [ProgressLane.LaneName, DecisionsLane.LaneName, TeamLane.LaneName, MessagesLane.LaneName, WorktreeLane.LaneName];

    protected override JsonObject BodyOf(string dir, string team, LaneRecord record, IRecordView records)
    {
        var body = BodyOf(team, record);
        var dirId = ActivityLane.DirId(dir);
        var name = ProgressLane.TeamNameOf(team);
        body["dir_id"] = dirId;
        body["work_root"] = WorkRoot(records, dirId, name);
        var underReview = ReviewStreams(records, dirId, name);
        var teamBranch = GrantField(records, dirId, name, "stream");
        foreach (var s in body["streams"].AsArray())
        {
            if (s.Member("work_tree") is { } tree)
            {
                s.AsObject()["work_tree"] = RelativeTo(tree, body["work_root"]);
            }

            s.AsObject()["review_open"] = s.Member("stream") is { IsText: true } stream && underReview.Contains(stream.AsString());
            if (s.Member("branch") is not { IsText: true })
            {
                s.AsObject()["branch"] = teamBranch.IsNull ? string.Empty : teamBranch;
            }

            foreach (var field in (string[])["owner", "goal"])
            {
                if (s.Member(field) is not { IsText: true })
                {
                    s.AsObject()[field] = string.Empty;
                }
            }

            s.AsObject()["status"] = StatusOf(s);
        }

        long byUser = 0;
        long byTeam = 0;
        long waiting = 0;
        foreach (var key in records.Keys(DecisionsLane.LaneName))
        {
            if (LaneParts.Split(key).First == dir && records.Get(DecisionsLane.LaneName, key) is { } run)
            {
                byUser += Count(run.Value, "by_user");
                byTeam += Count(run.Value, "by_team");
                waiting += Count(run.Value, "waiting");
            }
        }

        body["decisions"] = new JsonObject { ["by_user"] = byUser, ["by_team"] = byTeam, ["waiting"] = waiting, ["scope"] = "dir" };
        return body;
    }

    /// <summary>How a stream reads as a status. A count the stream leaves out is 0.</summary>
    internal static string StatusOf(JsonValue stream)
    {
        long N(string state) => stream.Member("counts") is { IsMap: true } counts ? Count(counts, state) : 0;
        return stream.Member("state") is { IsText: true } state && state.AsString() == "done" ? "done"
            : N("blocked") > 0 ? "blocked"
            : N("waiting_user") > 0 ? "waiting"
            : stream.Member("review_open") is { IsBool: true } review && review.AsBool() ? "review"
            : N("in_progress") > 0 || N("waiting_orch") > 0 || N("done") > 0 ? "in_progress"
            : "not_started";
    }

    /// <summary>The team's work root as its orchestrator's worktree grant gives it; null when there is none.</summary>
    private static JsonValue WorkRoot(IRecordView records, string dirId, string name) => GrantField(records, dirId, name, "work_root");

    /// <summary>A field of the orchestrator's worktree grant; null when there is none.</summary>
    private static JsonValue GrantField(IRecordView records, string dirId, string name, string field)
    {
        if (TreeData.Grants(records, dirId, name) is not { } grants || grants.Value.Member("members") is not { IsMap: true } rows)
        {
            return JsonValue.Null;
        }

        var row = rows.Member(TreeIds.ReservedName) is { IsMap: true } named ? named : rows.Member(TreeIds.Orchestrator) is { IsMap: true } at ? at : (JsonValue?)null;
        return row is { } r ? LaneParts.TextOrNull(r, field) : JsonValue.Null;
    }

    internal static HashSet<string> ReviewStreams(IRecordView records, string dirId, string name)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (TreeData.FindTeam(records, dirId, name) is not { } team)
        {
            return found;
        }

        foreach (var (_, exchange) in ExchangeFiling.OfTeam(records, dirId, name))
        {
            if (ExchangeFiling.IsOpen(exchange) && LaneParts.Text(exchange.Value, "slug") is { } slug
                && ExchangeFiling.Resolve(exchange, team).To is { } to && team.Row(to) is { } row && LaneParts.Text(row, "role") == "reviewer")
            {
                _ = found.Add(slug);
            }
        }

        return found;
    }

    private static long Count(JsonValue run, string name) => run.Member(name) is { IsNumber: true } n ? (long)n.AsNumber() : 0;

    protected override JsonObject BodyOf(string team, LaneRecord record)
    {
        var counts = new JsonObject();
        foreach (var state in ProgressLane.CountStateNames)
        {
            counts[state] = 0;
        }

        var streams = new JsonArray();
        foreach (var s in record.Value.Member("streams")!.Value.AsArray())
        {
            streams.Add(JsonValue.FromObject(CopyOf(s.AsObject())));
            if (s.Member("counts") is { IsMap: true } per)
            {
                foreach (var state in ProgressLane.CountStateNames)
                {
                    counts[state] = (long)counts.Get(state)!.Value.AsNumber() + (per.Member(state) is { IsNumber: true } n ? (long)n.AsNumber() : 0);
                }
            }
        }

        return new JsonObject { ["team"] = team, ["streams"] = streams, ["counts"] = counts, ["as_of"] = record.AsOfMs };
    }

    /// <summary>
    /// A stream as the summary carries it: every field spelled out, so no front end holds a default, and its <c>total</c>: the items of every state but dropped.
    /// </summary>
    private static JsonObject CopyOf(JsonObject source)
    {
        var copy = new JsonObject();
        foreach (var key in source.Keys)
        {
            copy[key] = source[key];
        }

        long total = 0;
        if (source.Get("counts") is { IsMap: true } counts)
        {
            total = ProgressLane.CountStateNames.Where(static s => s != "dropped").Sum(s => Count(counts, s));
        }

        copy["total"] = total;

        return copy;
    }

    /// <summary>The work tree relative to the team's work root when it lies under it (<c>.</c> for the root itself); any other path stays whole.</summary>
    internal static JsonValue RelativeTo(JsonValue workTree, JsonValue workRoot)
    {
        if (!workTree.IsText || !workRoot.IsText)
        {
            return workTree;
        }

        var tree = workTree.AsString();
        var root = workRoot.AsString().TrimEnd('/');
        return root.Length == 0 ? workTree : tree == root ? "." : tree.StartsWith(root + "/", StringComparison.Ordinal) ? tree[(root.Length + 1)..] : workTree;
    }
}

/// <summary><c>list:plan:&lt;dirId&gt;/&lt;team&gt;</c>: the team's items ordered by area, then rank (an item with none after the ranked ones), then stream and add order.</summary>
internal sealed class PlanItemsComposer() : ProgressComposer("list:plan")
{
    protected override JsonObject BodyOf(string team, LaneRecord record)
    {
        var items = new JsonArray();
        var ordered = record.Value.Member("items")!.Value.AsArray()
            .OrderBy(static i => i.Member("area")!.Value.AsString(), StringComparer.Ordinal)
            .ThenBy(static i => i.Member("rank") is { IsNumber: true } r ? r.AsNumber() : double.MaxValue)
            .ThenBy(static i => i.Member("stream")!.Value.AsString(), StringComparer.Ordinal)
            .ThenBy(static i => i.Member("seq")!.Value.AsNumber());
        foreach (var item in ordered)
        {
            items.Add(item);
        }

        return new JsonObject { ["team"] = team, ["items"] = items };
    }
}

/// <summary>
/// <c>list:plan-changes:&lt;dirId&gt;/&lt;team&gt;</c>: the newest <see cref="SliceCap"/> changes, newest first, as <c>{rows, total, tail_cursor}</c>. <c>total</c> counts every
/// change the lane holds that a cursor can name (it has <c>at</c>, <c>stream</c> and <c>seq</c>, the rows the pager pages) and <c>tail_cursor</c> is the list cursor of the oldest row, so the slice is cut exactly when <c>total</c> exceeds the rows.
/// </summary>
internal sealed class PlanChangesComposer() : ProgressComposer("list:plan-changes")
{
    public const int SliceCap = 200;

    protected override JsonObject BodyOf(string team, LaneRecord record)
    {
        var keyable = record.Value.Member("changes")!.Value.AsArray().Where(static c => PlanChangesPager.KeyOf(c) is not null).ToList();
        var rows = new JsonArray();
        foreach (var c in keyable.Take(SliceCap))
        {
            rows.Add(c);
        }

        var tail = rows.Count > 0 && PlanChangesPager.KeyOf(rows[^1]) is { } key ? ListCursor.Encode(key) : null;
        return new JsonObject
        {
            ["rows"] = rows,
            ["total"] = keyable.Count,
            ["tail_cursor"] = tail is null ? JsonValue.Null : tail,
        };
    }
}
