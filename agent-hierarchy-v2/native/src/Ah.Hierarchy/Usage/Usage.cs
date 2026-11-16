using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Hierarchy;

/// <summary>The shared pieces of the usage tracker: reading transcripts and the usage rows (b397 group 2: the <c>usage_rows</c> table).</summary>
public interface IHierarchyUsage
{
    /// <summary>The reporter's incremental-scan cache for main-session transcripts.</summary>
    string CacheFile { get; }

    /// <summary>The row count past which the usage rows are compacted, and the count they are compacted to.</summary>
    int PruneAt { get; }

    int PruneTo { get; }

    /// <summary>The hierarchy role an <c>agent_type</c> reports as; <c>other</c> when nothing recognises it.</summary>
    JsonValue RoleFor(JsonValue? agentType, JsonValue? agentRoles = null);

    JsonObject ZeroUsage();

    /// <summary>Adds the counts of <paramref name="usage"/> to <paramref name="accumulator"/> as JavaScript's <c>+=</c> does and returns it.</summary>
    JsonObject AddUsage(JsonObject accumulator, JsonValue? usage);

    /// <summary>The summed assistant-turn usage of a transcript with its most-used model, or null when it cannot be read.</summary>
    JsonValue SumTranscript(string file);

    /// <summary>
    /// The usage of one parsed transcript line: set when the line is an <c>assistant</c> entry whose <c>message.usage</c> is present and truthy,
    /// else null. Every reader of transcript usage (the sums, the report scan, the live tail) takes the line's fields from here and adds them up its own way.
    /// </summary>
    AssistantUsage? AssistantUsageOf(JsonValue entry);

    /// <summary>Every usage row that parses, oldest first, a repeated <c>op_id</c> kept once.</summary>
    JsonArray ReadUsageRecords();
}

/// <summary>One assistant turn of a transcript: the raw <c>message.usage</c> object, the model and the entry's own timestamp when it has them.</summary>
/// <summary>The usage of one assistant transcript line. A message over several lines repeats its usage and its <paramref name="MessageId"/> on each, so a sum counts it once.</summary>
public sealed record AssistantUsage(JsonValue Usage, string? Model, string? Timestamp, string? MessageId = null);

internal sealed class UsageLib(IFileReader files, IAhEnvironment environment, IUsageRows? rows = null) : IHierarchyUsage
{
    public AssistantUsage? AssistantUsageOf(JsonValue entry)
    {
        var message = entry.Member("message");
        var u = message?.Member("usage");
        if (!(entry.Member("type") is { IsText: true } type && type.AsString() == "assistant") || u is not { IsTruthy: true } usage)
        {
            return null;
        }

        return new AssistantUsage(
            usage,
            message?.Member("model") is { IsText: true } m && m.AsString().Length > 0 ? m.AsString() : null,
            entry.Member("timestamp") is { IsText: true } stamp ? stamp.AsString() : null,
            message?.Member("id") is { IsText: true } id ? id.AsString() : null);
    }

    public string CacheFile => AhPaths.UserEntry(environment.HomeDirectory, AhPaths.UsageCacheFile);

    public int PruneAt => 50_000;

    public int PruneTo => 30_000;

    internal string StateDirectory => AhPaths.UserEntry(environment.HomeDirectory, AhPaths.UsageStateDir);

    public JsonValue RoleFor(JsonValue? agentType, JsonValue? agentRoles = null)
    {
        if (agentType is not { IsText: true } t || t.AsString().Length == 0)
        {
            return "other";
        }

        var type = t.AsString();
        if (environment.LocalName(type) is { } local)
        {
            return local;
        }

        if (type is "task-gopher:task-gopher" or "task-gopher")
        {
            return "task-runner";
        }

        if (agentRoles is { IsMap: true } roles && roles.AsObject().Get(type) is { } role)
        {
            return role;
        }

        return "other";
    }

    public JsonObject ZeroUsage() => new() { ["calls"] = 0, ["in"] = 0, ["out"] = 0, ["cache_read"] = 0, ["cache_create"] = 0 };

    public JsonObject AddUsage(JsonObject accumulator, JsonValue? usage)
    {
        ArgumentNullException.ThrowIfNull(accumulator);
        if (usage is null || usage.Value.IsNull)
        {
            throw new InvalidOperationException("Cannot read properties of " + (usage is null ? "undefined" : "null"));
        }

        var u = usage.Value;
        foreach (var key in new[] { "calls", "in", "out", "cache_read", "cache_create" })
        {
            accumulator[key] = JsOps.Add(accumulator.Get(key), JsOps.OrZero(u.Member(key)));
        }

        return accumulator;
    }

    public JsonValue SumTranscript(string file)
    {
        string raw;
        try
        {
            raw = files.ReadText(file);
        }
        catch (Exception)
        {
            return JsonValue.Null;
        }

        double calls = 0;
        var tokens = new JsonValue[4] { 0, 0, 0, 0 };
        var models = new JsonObject();
        string? lastMessage = null;
        foreach (var line in raw.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            JsonValue o;
            try
            {
                o = JsonText.Parse(line);
            }
            catch (JsonParseException)
            {
                continue;
            }

            if (AssistantUsageOf(o) is not { } turn || (turn.MessageId is not null && turn.MessageId == lastMessage))
            {
                continue;
            }

            lastMessage = turn.MessageId;
            var usage = turn.Usage;
            calls += 1;
            tokens[0] = JsOps.Add(tokens[0], JsOps.OrZero(usage.Member("input_tokens")));
            tokens[1] = JsOps.Add(tokens[1], JsOps.OrZero(usage.Member("output_tokens")));
            tokens[2] = JsOps.Add(tokens[2], JsOps.OrZero(usage.Member("cache_read_input_tokens")));
            tokens[3] = JsOps.Add(tokens[3], JsOps.OrZero(usage.Member("cache_creation_input_tokens")));
            if (turn.Model is { } name)
            {
                models[name] = (models.Get(name) is { IsNumber: true } n ? n.AsNumber() : 0) + 1;
            }
        }

        var best = string.Empty;
        double bestCount = double.NegativeInfinity;
        foreach (var key in models.Keys)
        {
            var count = models[key].AsNumber();
            if (count > bestCount)
            {
                best = key;
                bestCount = count;
            }
        }

        return new JsonObject
        {
            ["calls"] = calls,
            ["in"] = tokens[0],
            ["out"] = tokens[1],
            ["cache_read"] = tokens[2],
            ["cache_create"] = tokens[3],
            ["model"] = best,
        };
    }

    /// <summary>
    /// Inside the append's transaction: stores one row, carrying <paramref name="operationId"/> as data when there is one, then, when
    /// <paramref name="prune"/>, keeps only the newest <see cref="PruneTo"/> rows once there are more than <see cref="PruneAt"/>.
    /// </summary>
    internal void AppendUsageRow(IDbOpTransaction tx, JsonValue? record, string? operationId, bool prune)
    {
        JsonValue? row = record;
        if (operationId is { Length: > 0 })
        {
            var spread = JsOps.Spread(record);
            spread["op_id"] = operationId;
            row = spread;
        }

        tx.Execute("INSERT INTO usage_rows(body) VALUES (?1)", row is null ? "undefined" : JsonText.Stringify(row.Value, 0));
        if (prune && tx.Query("SELECT COUNT(*) FROM usage_rows") is [[long count]] && count > PruneAt)
        {
            tx.Execute("DELETE FROM usage_rows WHERE seq <= (SELECT MAX(seq) FROM usage_rows) - ?1", (long)PruneTo);
        }
    }

    public JsonArray ReadUsageRecords()
    {
        var records = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in rows?.Bodies() ?? [])
        {
            JsonValue rec;
            try
            {
                rec = JsonText.Parse(line);
            }
            catch (JsonParseException)
            {
                continue;
            }

            if (!rec.IsMap && !rec.IsArray)
            {
                continue;
            }

            if (rec.Member("op_id") is { IsTruthy: true } id && JsOps.SetKey(id) is { } key && !seen.Add(key))
            {
                continue;
            }

            records.Add(rec);
        }

        return records;
    }
}

/// <summary>The <c>usage</c> state domain: the usage rows' append and read, db-only (b397 group 2).</summary>
[StateDomain("usage", "usage")]
internal sealed class UsageDomain(UsageLib usage) : IStateDomain
{
    public string Name => "usage";

    public IReadOnlyList<string> StoreNames => ["usage"];

    public bool HomeScoped => true;

    public bool DbOnly => true;

    public string StateDirectory(RequestContext context) => usage.StateDirectory;

    public IReadOnlyList<StateOp> Ops =>
    [
        new StateOp("append", OpKind.Write, OpStyle.Append, true, null, true, DbOnlyBody) { DbBody = Append },
        new StateOp("read", OpKind.Read, null, false, null, true, (call, _) => OpArgs.Run(call, _ => usage.ReadUsageRecords())),
    ];

    public void AfterWrite(OpCall operation, OpResult result)
    {
    }

    private static OpResult DbOnlyBody(OpCall call, IStoreWriteContext? writer) => new(false, default, $"usage.{call.Op} is db-only", "op_failed");

    private OpResult Append(OpCall call, IDbOpTransaction tx)
    {
        return OpArgs.Run(call, args =>
        {
            usage.AppendUsageRow(tx, args.Member("rec"), call.OperationId, prune: true);
            return new JsonObject { ["appended"] = true };
        });
    }
}
