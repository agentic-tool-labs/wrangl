using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Hierarchy;

/// <summary>
/// The checkpoint records of a team, written once and never edited. A record names the snapshot, the members and what each was asked
/// to do for a restart. Per-dir-files-to-db S11a-ckpt: the records live in the <c>checkpoints</c> table, each keyed by the path its
/// file had or would have had (<c>&lt;pool&gt;/checkpoints/&lt;team&gt;/&lt;ts&gt;.json</c>), so a path a hold stores still names it.
/// </summary>
[StateDomain("checkpoint", "checkpoint")]
internal sealed class CheckpointDomain(HierLib lib) : HierDomain(lib, "checkpoint", "checkpoint")
{
    public override bool DbOnly => true;

    private static OpResult DbOnlyBody(OpCall call, IStoreWriteContext? writer) => new(false, default, $"checkpoint.{call.Op} is db-only", "op_failed");

    public override IReadOnlyList<StateOp> Ops =>
    [
        Write("write", OpStyle.Replace, false, DbOnlyBody, "dir") with { DbBody = (call, tx) => OpArgs.Run(call, args => Lib.CheckpointWriteDb(tx, args)) },
        Read("read", (call, _) => OpArgs.Run(call, args => Lib.CheckpointReadOp(args)), "dir"),
        Read("get", (call, _) => OpArgs.Run(call, args => Lib.CheckpointGetOp(args)), "dir"),
    ];
}

internal sealed partial class HierLib
{
    /// <summary>A team name or a record's timestamp, as a file name: no path separator, no leading dot.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*\z")]
    private static partial Regex CheckpointNamePattern();

    private static string CheckpointName(JsonValue args, string field)
    {
        var value = ArgText(args, field);
        return CheckpointNamePattern().IsMatch(value) || (field == "team" && value == "_default")
            ? value
            : throw new InvalidOperationException($"checkpoint: {field} \"{value}\" is not a plain name");
    }

    private static string CheckpointFolder(JsonValue args) => NodePath.Join(ArgText(args, "dir"), "checkpoints", CheckpointName(args, "team"));

    private ICheckpointSource Checkpoints => _checkpoints ?? throw new InvalidOperationException("checkpoint: the records need the state database, and there is none");

    /// <summary>Writes <c>record</c> as the team's checkpoint <c>ts</c>. A record is never replaced: the same <c>ts</c> twice fails.</summary>
    internal JsonValue CheckpointWriteDb(IDbOpTransaction tx, JsonValue args)
    {
        var dir = ArgText(args, "dir");
        var team = CheckpointName(args, "team");
        var ts = CheckpointName(args, "ts");
        if (args.Member("record") is not { IsMap: true } record)
        {
            throw new InvalidOperationException("checkpoint: record must be an object");
        }

        var key = Checkpoints.Key(NodePath.Join(CheckpointFolder(args), ts + ".json"));
        if (tx.Query("SELECT 1 FROM checkpoints WHERE key = ?1 OR (hier_dir = ?2 AND team = ?3 AND ts = ?4)", key, MsgRows.PoolKey(dir), team, ts).Count > 0)
        {
            throw new InvalidOperationException($"checkpoint: {ts} already exists for the team");
        }

        tx.Execute(
            "INSERT INTO checkpoints(key, hier_dir, team, ts, body, created_at) VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
            key, MsgRows.PoolKey(dir), team, ts, JsonText.Stringify(record, 2) + "\n", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return new JsonObject { ["path"] = key, ["ts"] = ts };
    }

    /// <summary>The team's newest record (or the one named by <c>ts</c>): <c>{found, path, ts, record, body}</c>, or <c>{found: false}</c>.</summary>
    internal JsonValue CheckpointReadOp(JsonValue args)
    {
        var team = CheckpointName(args, "team");
        var ts = args.Member("ts") is { IsText: true } ? CheckpointName(args, "ts") : null;
        return CheckpointFound(_checkpoints?.Latest(ArgText(args, "dir"), team, ts));
    }

    /// <summary><c>checkpoint.get</c>: the record whose key is <c>key</c> (a path a caller or a hold kept): as <c>read</c> answers.</summary>
    internal JsonValue CheckpointGetOp(JsonValue args) => CheckpointFound(_checkpoints?.ByKey(ArgText(args, "key")));

    private static JsonValue CheckpointFound(CheckpointRow? row) => row is null
        ? new JsonObject { ["found"] = false }
        : new JsonObject { ["found"] = true, ["path"] = row.Key, ["ts"] = row.Ts, ["record"] = JsonText.Parse(row.Body), ["body"] = row.Body };
}
