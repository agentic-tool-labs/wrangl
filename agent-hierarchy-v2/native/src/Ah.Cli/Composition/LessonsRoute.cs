using Ah.Abstractions;
using Ah.Cli.Verbs;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.StateDb;

namespace Ah.Cli.Composition;

/// <summary>One stored lesson (ah2-54). <see cref="Paths"/> are repo-relative prefixes; empty when the lesson names none.</summary>
internal sealed record LessonRow(long Id, string Scope, string? Team, string? Role, IReadOnlyList<string> Paths, string? Happened, string Lesson, string Applies, string Pointer,
    string? UntilItem, string By, string State, string? RetiredBy, string? RetiredWhy, long CreatedMs, long UpdatedMs, string? PromotedBy = null, long? PromotedMs = null);

/// <summary>What <c>ah lessons propose</c> asks the daemon to write.</summary>
internal sealed record LessonAsk(string Dir, string? Team, string? Role, IReadOnlyList<string> Paths, string? Happened, string Lesson, string Applies, string Pointer, string? UntilItem = null);

/// <summary>The lessons table: lower-authority notes, apart from the rules (adr-30). Guidance, never a gate.</summary>
internal static class Lessons
{
    public const int Ok = 0;
    public const int NotFound = 1;
    public const int Refused = 2;

    public const string TeamScope = "team";

    public static readonly string[] Scopes = [TeamScope, "role", "repo", "global"];

    private const string Columns = "id, scope, team, role, paths, happened, lesson, applies, pointer, until_item, \"by\", state, retired_by, retired_why, created_ms, updated_ms, promoted_by, promoted_ms";

    /// <summary>Why the ask is refused (D5 field caps; the role must be one <paramref name="roles"/> holds), or null when it may be written.</summary>
    internal static string? BadInput(LessonAsk ask, IReadOnlyCollection<string> roles)
    {
        foreach (var (name, value, cap, required) in new[]
        {
            ("--lesson", ask.Lesson, 200, true), ("--when", ask.Applies, 120, true), ("--happened", ask.Happened, 200, false), ("--pointer", ask.Pointer, 120, true),
        })
        {
            if (value is null ? required : value.Trim().Length == 0)
            {
                return $"{name} must not be empty";
            }

            if (value is not null && value.Length > cap)
            {
                return $"{name} is {value.Length} characters; at most {cap}";
            }
        }

        if (ask.Paths.Count > 3)
        {
            return $"at most 3 --path prefixes; {ask.Paths.Count} given";
        }

        foreach (var path in ask.Paths)
        {
            if (path.Trim().Length == 0 || path.Length > 120 || path.StartsWith('/') || path.Split('/').Contains(".."))
            {
                return $"--path {path} must be a repo-relative prefix of at most 120 characters, with no leading / and no ..";
            }
        }

        return ask.Role is { } role && !roles.Contains(role) ? $"--role {role} is not a role the registry knows; roles: {string.Join(", ", roles)}" : null;
    }

    /// <summary>Writes a new <c>team</c>-scope lesson; the answer's row carries its id.</summary>
    internal static (int Code, string Message, LessonRow? Row) Propose(IStateDb db, LessonAsk ask, string by, DateTimeOffset now)
    {
        var ms = now.ToUnixTimeMilliseconds();
        var paths = ask.Paths.Count == 0 ? null : string.Join('\n', ask.Paths);
        long id = 0;
        var outcome = db.WriteOnce(Guid.NewGuid().ToString("N"), now.AddSeconds(10), tx =>
        {
            tx.Execute(
                "INSERT INTO lessons(hier_dir, scope, team, role, paths, happened, lesson, applies, pointer, until_item, \"by\", created_ms, updated_ms) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?12)",
                HierarchyModule.WorkDirKey(ask.Dir), TeamScope, ask.Team, ask.Role, paths, ask.Happened, ask.Lesson, ask.Applies, ask.Pointer, ask.UntilItem, by, ms);
            id = Convert.ToInt64(tx.Query("SELECT last_insert_rowid()")[0][0], System.Globalization.CultureInfo.InvariantCulture);
            return string.Empty;
        });
        if (outcome.Status != OnceStatus.Applied)
        {
            return (Refused, $"the state database did not take the write: {outcome.Message}", null);
        }

        var row = new LessonRow(id, TeamScope, ask.Team, ask.Role, ask.Paths, ask.Happened, ask.Lesson, ask.Applies, ask.Pointer, ask.UntilItem, by, "open", null, null, ms, ms);
        return (Ok, $"L{id}", row);
    }

    /// <summary>
    /// Changes a lesson of this repo (or a <c>global</c> one): <paramref name="change"/> gets the stored row and answers the code, the message and the
    /// <c>SET</c> clause with its arguments from <c>?2</c> on, or a null clause for no write.
    /// </summary>
    internal static (int Code, string Message, LessonRow? Row) Change(IStateDb db, string dir, long id, DateTimeOffset now, Func<LessonRow, (int Code, string Message, string? Set, object?[] Args)> change)
    {
        (int, string, LessonRow?) result = (NotFound, $"no lesson L{id}", null);
        var outcome = db.WriteOnce(Guid.NewGuid().ToString("N"), now.AddSeconds(10), tx =>
        {
            var found = tx.Query($"SELECT {Columns} FROM lessons WHERE id = ?1 AND (hier_dir = ?2 OR scope = 'global')", id, HierarchyModule.WorkDirKey(dir)).Select(RowOf).FirstOrDefault();
            if (found is null)
            {
                return string.Empty;
            }

            var (code, message, set, args) = change(found);
            if (set is not null)
            {
                tx.Execute($"UPDATE lessons SET {set}, updated_ms = ?{args.Length + 2} WHERE id = ?1", [id, .. args, now.ToUnixTimeMilliseconds()]);
            }

            result = (code, message, tx.Query($"SELECT {Columns} FROM lessons WHERE id = ?1", id).Select(RowOf).First());
            return string.Empty;
        });
        return outcome.Status == OnceStatus.Applied ? result : (Refused, $"the state database did not take the write: {outcome.Message}", null);
    }

    /// <summary>
    /// D6: whether the lesson's <c>until</c> item is done, read now through the registry's read-only lookup (no reconcile). A dropped, unknown or
    /// unreadable item leaves it open (Amendment 2 F3), so one bad row never hides the others.
    /// </summary>
    internal static bool UntilDone(ServiceGraph g, string ahHome, string dir, LessonRow row)
    {
        if (row.UntilItem is not { } until)
        {
            return false;
        }

        try
        {
            if (StateDbModule.OpenReader(ahHome).Reader is not { } reader)
            {
                return false;
            }

            using (reader)
            {
                return HierarchyModule.WorkResolve(reader.Query, dir, until, null).Row is { } item
                    && HierarchyModule.WorkItemOf(reader.Query, g.Hierarchy.Progress, dir, item).Plan?.State == "done";
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Amendment 2 F2: who wrote, as <c>by</c>, <c>retired_by</c> and <c>promoted_by</c> record it: the caller's <c>name@team</c> address when it has one, else its role.</summary>
    internal static string ByOf(CutoverCaller caller)
    {
        var head = caller.Detail.Split(',', ':')[0].Trim();
        return head.Split('@') is [{ Length: > 0 } name, { Length: > 0 } team] && !head.Contains(' ', StringComparison.Ordinal) && !name.Contains('/', StringComparison.Ordinal) && !team.Contains('/', StringComparison.Ordinal)
            ? head
            : caller.Name;
    }

    /// <summary>The row as readers see it: open, unless retired or its <c>until</c> item is done.</summary>
    internal static LessonRow AsRead(ServiceGraph g, string ahHome, string dir, LessonRow row) =>
        row.State == "open" && UntilDone(g, ahHome, dir, row) ? row with { State = "retired", RetiredWhy = $"{row.UntilItem} is done" } : row;

    /// <summary>Every lesson of the repo at <paramref name="dir"/>, and every <c>global</c> one from any repo, oldest first.</summary>
    internal static IReadOnlyList<LessonRow> Rows(Func<string, object?[], IReadOnlyList<object?[]>> query, string dir) =>
        [.. query($"SELECT {Columns} FROM lessons WHERE hier_dir = ?1 OR scope = 'global' ORDER BY id", [HierarchyModule.WorkDirKey(dir)]).Select(RowOf)];

    /// <summary>The lessons a reader sees: none when there is no database or no table yet; null with <paramref name="error"/> set when it cannot be read.</summary>
    internal static IReadOnlyList<LessonRow>? Read(string ahHome, string dir, out string? error)
    {
        error = null;
        var opened = StateDbModule.OpenReader(ahHome);
        if (opened.Reader is not { } reader)
        {
            if (File.Exists(Path.Join(ahHome, "state", "ah.db")))
            {
                error = opened.Message;
                return null;
            }

            return [];
        }

        using (reader)
        {
            try
            {
                return Rows(reader.Query, dir);
            }
            catch (Exception e) when (e.Message.Contains("no such table: lessons", StringComparison.Ordinal))
            {
                return [];
            }
            catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
            {
                error = e.Message;
                return null;
            }
        }
    }

    private static string? Text(object? value) => value as string;

    private static LessonRow RowOf(object?[] r) => new(
        Convert.ToInt64(r[0], System.Globalization.CultureInfo.InvariantCulture), (string)r[1]!, Text(r[2]), Text(r[3]),
        Text(r[4]) is { Length: > 0 } paths ? paths.Split('\n') : [], Text(r[5]), (string)r[6]!, (string)r[7]!, (string)r[8]!, Text(r[9]), (string)r[10]!, (string)r[11]!,
        Text(r[12]), Text(r[13]), Convert.ToInt64(r[14], System.Globalization.CultureInfo.InvariantCulture), Convert.ToInt64(r[15], System.Globalization.CultureInfo.InvariantCulture),
        Text(r[16]), r[17] is null ? null : Convert.ToInt64(r[17], System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>A lesson as <c>--json</c> prints it.</summary>
    internal static JsonObject Json(LessonRow row)
    {
        var paths = new JsonArray();
        foreach (var path in row.Paths)
        {
            paths.Add(path);
        }

        static JsonValue Or(string? s) => s is null ? JsonValue.Null : s;
        return new JsonObject
        {
            ["id"] = $"L{row.Id}",
            ["scope"] = row.Scope,
            ["team"] = Or(row.Team),
            ["role"] = Or(row.Role),
            ["paths"] = paths,
            ["happened"] = Or(row.Happened),
            ["lesson"] = row.Lesson,
            ["when"] = row.Applies,
            ["pointer"] = row.Pointer,
            ["until"] = Or(row.UntilItem),
            ["by"] = row.By,
            ["state"] = row.State,
            ["retired_by"] = Or(row.RetiredBy),
            ["retired_why"] = Or(row.RetiredWhy),
            ["promoted_by"] = Or(row.PromotedBy),
            ["promoted_ms"] = row.PromotedMs is { } promoted ? promoted : JsonValue.Null,
            ["created_ms"] = row.CreatedMs,
            ["updated_ms"] = row.UpdatedMs,
        };
    }
}

/// <summary>
/// <c>POST /v1/lessons</c>: writes a lesson in the daemon, which proves the caller from the peer process as <see cref="RulesRoute"/> does. Anyone may propose (adr-30);
/// the caller's kind is recorded as <c>by</c>. Answers <c>{code, message[, lesson]}</c>.
/// </summary>
internal sealed class LessonsRoute(ServiceGraph g, string ahHome, Func<IStateDb>? stateDb, Func<int?, CutoverCaller> callerOf, Func<DateTimeOffset> now)
{
    public const string Path = "POST /v1/lessons";

    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    private static RouteResponse Json(int status, JsonObject body) => new(status, NoHeaders, JsonText.ToUtf8(body, 0));

    private static RouteResponse Answer(int code, string message) => Json(200, new JsonObject { ["code"] = code, ["message"] = message });

    private static string? Text(JsonObject o, string key) => o.Get(key) is { IsText: true } t ? t.AsString() : null;

    public Task<RouteResponse> HandleAsync(RouteRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        JsonObject body;
        try
        {
            body = JsonText.Parse(request.Body.ToArray()) is { IsMap: true } o ? o.AsObject() : throw new JsonParseException("not an object");
        }
        catch (JsonParseException)
        {
            return Task.FromResult(Json(400, new JsonObject { ["error"] = "body must be a JSON object" }));
        }

        if (Text(body, "op") is not ("propose" or "promote" or "retire") || Text(body, "dir") is not { Length: > 0 } dir)
        {
            return Task.FromResult(Json(400, new JsonObject { ["error"] = "op (propose, promote or retire) and dir are required" }));
        }

        var op = Text(body, "op")!;
        var caller = callerOf(request.PeerPid);
        if (op != "propose" && !caller.MayChangeAuthority)
        {
            return Task.FromResult(Answer(Lessons.Refused, $"only the user or the top-level Orchestrator may {op} a lesson; the caller is a {caller.Name} ({caller.Detail}); ask your Orchestrator"));
        }

        if (stateDb is null)
        {
            return Task.FromResult(Json(503, new JsonObject { ["error"] = "the daemon has no state database" }));
        }

        var (code, message, row) = op switch
        {
            "propose" => Propose(body, dir, caller),
            "promote" => Promote(body, dir, caller),
            _ => Retire(body, dir, caller),
        };
        var answer = new JsonObject { ["code"] = code, ["message"] = message };
        if (row is not null)
        {
            answer["lesson"] = Lessons.Json(row);
        }

        return Task.FromResult(Json(200, answer));
    }

    private (int Code, string Message, LessonRow? Row) Propose(JsonObject body, string dir, CutoverCaller caller)
    {
        if (Text(body, "team") is not { Length: > 0 } team)
        {
            return (Lessons.Refused, "the caller has no team: name one with --team <name>", null);
        }

        var paths = body.Get("paths") is { IsArray: true } list ? list.AsArray().Where(p => p.IsText).Select(p => p.AsString()).ToList() : [];
        var until = Text(body, "until");
        var ask = new LessonAsk(dir, team, Text(body, "role"), paths, Text(body, "happened"), Text(body, "lesson") ?? string.Empty, Text(body, "when") ?? string.Empty, Text(body, "pointer") ?? string.Empty, until);
        if (Lessons.BadInput(ask, RolesOf(dir)) is { } bad)
        {
            return (Lessons.Refused, bad, null);
        }

        if (until is not null && WorkVerb.Resolve(g, ahHome, dir, until, null).Row is null)
        {
            return (Lessons.NotFound, $"no work item {until}", null);
        }

        return Lessons.Propose(stateDb!(), ask, Lessons.ByOf(caller), now());
    }

    private (int Code, string Message, LessonRow? Row) Promote(JsonObject body, string dir, CutoverCaller caller)
    {
        if (Id(body) is not { } id || Text(body, "to") is not ("role" or "repo" or "global") || Text(body, "to") is not { } to)
        {
            return (Lessons.Refused, "promote needs L<n> and --to role, repo or global", null);
        }

        var role = Text(body, "role");
        if (role is not null && !RolesOf(dir).Contains(role))
        {
            return (Lessons.Refused, $"--role {role} is not a role the registry knows; roles: {string.Join(", ", RolesOf(dir))}", null);
        }

        return Lessons.Change(stateDb!(), dir, id, now(), row =>
        {
            var kept = role ?? row.Role;
            return to == "role" && kept is null
                ? (Lessons.Refused, $"--to role needs a role: L{id} has none; pass --role <role>", null, [])
                : (Lessons.Ok, $"L{id} is now {to}{(kept is null ? string.Empty : " for " + kept)}", "scope = ?2, role = ?3, promoted_by = ?4, promoted_ms = ?5", [to, kept, Lessons.ByOf(caller), now().ToUnixTimeMilliseconds()]);
        });
    }

    private (int Code, string Message, LessonRow? Row) Retire(JsonObject body, string dir, CutoverCaller caller)
    {
        if (Id(body) is not { } id || Text(body, "why") is not { } why || why.Trim().Length == 0)
        {
            return (Lessons.Refused, "retire needs L<n> and a non-empty --why", null);
        }

        return Lessons.Change(stateDb!(), dir, id, now(), row => row.State == "retired"
            ? (Lessons.Ok, $"L{id} is already retired", null, [])
            : (Lessons.Ok, $"L{id} is retired", "state = 'retired', retired_by = ?2, retired_why = ?3", [Lessons.ByOf(caller), why]));
    }

    private static long? Id(JsonObject body) => body.Get("id") is { IsNumber: true } n ? (long)n.AsNumber() : null;

    private IReadOnlyCollection<string> RolesOf(string dir)
    {
        try
        {
            return [.. g.Config.RegistryRoles(g.Config.ResolveConfig(dir))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or JsonParseException)
        {
            return [.. Ah.Config.ConfigModule.BuiltinRoles];
        }
    }
}
