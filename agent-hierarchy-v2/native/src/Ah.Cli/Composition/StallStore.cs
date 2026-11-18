using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.StateDb;

namespace Ah.Cli.Composition;

/// <summary>
/// The stall, overdue and wake state in the state database (b397 group 3): the stall journal as <c>stall_journal</c> rows, and the
/// small documents (reported episodes, tick stamps, overdue memory and health, the waiter's printed ids) as <c>meta</c> values under
/// <see cref="Keys"/>. Writes go through the db-only <c>stall</c> state domain; reads open a reader and see no rows when the database
/// is missing, busy or older.
/// </summary>
internal static class StallStore
{
    internal const string Reported = "stall.reported";
    internal const string ReportedUnreadable = "stall.reported.bak";
    internal const string Ticks = "stall.ticks";
    internal const string AhdHealth = "overdue.ahd-health";

    internal static string Overdue(string session) => "overdue." + session;

    internal static string Printed(string session) => "wake.printed." + session;

    /// <summary>The meta key prefixes the <c>stall</c> domain may write; any other key is refused.</summary>
    internal static readonly string[] Keys = ["stall.", "overdue.", "wake.printed."];

    /// <summary>How many journal rows are kept; an append deletes the older ones.</summary>
    internal const int JournalKeep = 5000;

    public static string? Read(string ahHome, string key) =>
        Query(ahHome, "SELECT value FROM meta WHERE key = ?1", key) is [[string value]] ? value : null;

    /// <summary>
    /// The value stored at <paramref name="key"/>, telling a failed read from a missing row (ah2-180 Amendment 4): false when the store cannot be
    /// opened or queried, true with a null <paramref name="value"/> when the row is not there.
    /// </summary>
    public static bool TryRead(string ahHome, string key, out string? value)
    {
        value = null;
        try
        {
            using var reader = StateDbModule.OpenReader(ahHome).Reader;
            if (reader is null)
            {
                return false;
            }

            value = reader.Query("SELECT value FROM meta WHERE key = ?1", key) is [[string text]] ? text : null;
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>The journal rows, oldest first, each the JSON text of one row.</summary>
    public static IReadOnlyList<string> JournalRows(string ahHome) =>
        [.. Query(ahHome, "SELECT row FROM stall_journal ORDER BY seq").Select(static r => (string)r[0]!)];

    private static IReadOnlyList<object?[]> Query(string ahHome, string sql, params object?[] args)
    {
        try
        {
            using var reader = StateDbModule.OpenReader(ahHome).Reader;
            return reader?.Query(sql, args) ?? [];
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return [];
        }
    }

    /// <summary>Sets <paramref name="key"/> to <paramref name="value"/>; null deletes it.</summary>
    public static void Put(ServiceGraph g, string key, string? value) =>
        Invoke(g, "put", new JsonObject { ["key"] = key, ["value"] = value is null ? JsonValue.Null : JsonValue.FromString(value) });

    /// <summary>Sets field <paramref name="field"/> of the JSON object stored at <paramref name="key"/>, in one transaction.</summary>
    public static void Merge(ServiceGraph g, string key, string field, JsonObject value) =>
        Invoke(g, "merge", new JsonObject { ["key"] = key, ["field"] = field, ["value"] = value });

    public static void Journal(ServiceGraph g, JsonObject row) => Invoke(g, "journal", new JsonObject { ["row"] = row });

    private static void Invoke(ServiceGraph g, string op, JsonObject args)
    {
        var result = g.Writer.Invoke("stall", op, JsonText.ToUtf8(args, 0), g.Callers.RequestContext(Path.GetFullPath(Directory.GetCurrentDirectory())), new StateCallOptions());
        if (!result.Ok)
        {
            throw new IOException(result.Error ?? result.ErrorCode ?? "stall state write failed");
        }
    }
}

/// <summary>The db-only <c>stall</c> state domain: <c>put {key, value}</c>, <c>merge {key, field, value}</c> and <c>journal {row}</c>, scoped to the ah home.</summary>
internal sealed class StallStateDomain : IStateDomain
{
    private readonly IAhEnvironment _environment;
    private readonly IClock _clock;

    internal StallStateDomain(IAhEnvironment environment, IClock clock)
    {
        _environment = environment;
        _clock = clock;
        Ops =
        [
            Write("put", Put),
            Write("merge", Merge),
            Write("journal", Journal),
        ];
    }

    public string Name => "stall";

    public IReadOnlyList<string> StoreNames { get; } = ["stall"];

    public bool HomeScoped => true;

    public bool DbOnly => true;

    public IReadOnlyList<StateOp> Ops { get; }

    public string StateDirectory(RequestContext context) => NodePath.Join(_environment.AhHome, "stall");

    public void AfterWrite(OpCall operation, OpResult result)
    {
    }

    private static StateOp Write(string name, Func<JsonValue, IDbOpTransaction, JsonObject> body) =>
        new(name, OpKind.Write, OpStyle.Replace, false, null, false, (call, _) => new OpResult(false, default, $"stall.{call.Op} is db-only", "op_failed"))
        {
            DbBody = (call, tx) =>
            {
                try
                {
                    var args = call.ArgsJson.IsEmpty ? new JsonObject() : JsonText.Parse(call.ArgsJson.Span);
                    return new OpResult(true, JsonText.ToUtf8(body(args, tx), 0));
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    return new OpResult(false, default, e.Message, "op_failed");
                }
            },
        };

    private static string KeyOf(JsonValue args) =>
        args.Member("key") is { IsText: true } k && StallStore.Keys.Any(p => k.AsString().StartsWith(p, StringComparison.Ordinal) && k.AsString().Length > p.Length)
            ? k.AsString()
            : throw new InvalidOperationException("the key is not a stall, overdue or wake key");

    private static JsonObject Put(JsonValue args, IDbOpTransaction tx)
    {
        var key = KeyOf(args);
        if (args.Member("value") is { IsText: true } value)
        {
            tx.Execute("INSERT INTO meta(key, value) VALUES (?1, ?2) ON CONFLICT(key) DO UPDATE SET value = excluded.value", key, value.AsString());
        }
        else
        {
            tx.Execute("DELETE FROM meta WHERE key = ?1", key);
        }

        return new JsonObject { ["written"] = true };
    }

    private static JsonObject Merge(JsonValue args, IDbOpTransaction tx)
    {
        var key = KeyOf(args);
        var field = args.Member("field") is { IsText: true } f && f.AsString().Length > 0 ? f.AsString() : throw new InvalidOperationException("the field must be a non-empty string");
        var doc = tx.Query("SELECT value FROM meta WHERE key = ?1", key) is [[string stored]] && JsonText.TryParse(Utf8Text.Encode(stored), out var current) && current.IsMap
            ? current.AsObject().Clone()
            : new JsonObject();
        doc[field] = args.Member("value") ?? JsonValue.Null;
        tx.Execute("INSERT INTO meta(key, value) VALUES (?1, ?2) ON CONFLICT(key) DO UPDATE SET value = excluded.value", key, JsonText.Stringify(JsonValue.FromObject(doc), 0));
        return new JsonObject { ["written"] = true };
    }

    private JsonObject Journal(JsonValue args, IDbOpTransaction tx)
    {
        if (args.Member("row") is not { IsMap: true } row)
        {
            throw new InvalidOperationException("the journal row must be an object");
        }

        tx.Execute("INSERT INTO stall_journal(at_ms, row) VALUES (?1, ?2)", _clock.UtcNow.ToUnixTimeMilliseconds(), JsonText.Stringify(row, 0));
        tx.Execute("DELETE FROM stall_journal WHERE seq <= (SELECT MAX(seq) FROM stall_journal) - ?1", (long)StallStore.JournalKeep);
        return new JsonObject { ["written"] = true };
    }
}
