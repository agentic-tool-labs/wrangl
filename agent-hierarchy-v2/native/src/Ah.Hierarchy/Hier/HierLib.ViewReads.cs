using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>The read operations that run a whole library computation of the hierarchy and return its result: the status of a pool, its work trees, and the live pipeline run.</summary>
internal sealed partial class HierLib
{
    private static double NowMsOf(JsonValue args) =>
        args.Member("now_ms") is { IsNumber: true } n ? n.AsNumber() : throw new InvalidOperationException("The \"now_ms\" argument must be a number");

    /// <summary><c>status.compute</c>: <c>{dir, now_ms}</c> is the status document <see cref="ReadStatus"/> gives for the pool, as if the hierarchy were enabled there.</summary>
    internal JsonValue StatusComputeOp(JsonValue args)
    {
        var dir = ArgText(args, "dir");
        return ReadStatus(dir, NowMsOf(args), dir, true);
    }

    /// <summary><c>msgs.version</c>: <c>{version}</c>, the message store's write counter; the view compares it to notice a change no file event reports.</summary>
    internal JsonValue MsgsVersionOp()
    {
        using var db = _core.DbMsgs();
        return new JsonObject { ["version"] = db.Version() };
    }

    /// <summary><c>msgs.changed</c>: <c>{dir, since}</c> is <c>{ids: [id]}</c>, the messages of the pool a write changed after the counter was <c>since</c>.</summary>
    internal JsonValue MsgsChangedOp(JsonValue args)
    {
        var since = args.Member("since") is { IsNumber: true } n ? (long)n.AsNumber() : throw new InvalidOperationException("The \"since\" argument must be a number");
        using var db = _core.DbMsgs();
        var ids = new JsonArray();
        foreach (var id in db.ChangedIds(ArgText(args, "dir"), since))
        {
            ids.Add(id);
        }

        return new JsonObject { ["ids"] = ids };
    }

    /// <summary><c>msgs.trees</c>: <c>{dir, teams: [name]}</c> is <c>{trees: [path]}</c>, the git work trees <see cref="Trees"/> lists for the pools of those teams.</summary>
    internal JsonValue MsgsTreesOp(JsonValue args)
    {
        var teams = args.Member("teams") is { IsArray: true } list
            ? list.AsArray().Where(static t => t.IsText).Select(static t => t.AsString()).ToList()
            : throw new InvalidOperationException("The \"teams\" argument must be an array of names");
        var trees = new JsonArray();
        foreach (var tree in Trees(ArgText(args, "dir"), teams))
        {
            trees.Add(tree);
        }

        return new JsonObject { ["trees"] = trees };
    }

    /// <summary>
    /// <c>decisions.pipeline-run</c>: <c>{dir, team?}</c> is <c>{run_id, started_at, rows}</c> for the pipeline run open in the pool
    /// (<c>rows</c> its decision-log lines, <c>started_at</c> the <c>created</c> time of the run's request or null), or null when no run is open.
    /// </summary>
    internal JsonValue DecisionsPipelineRunOp(JsonValue args)
    {
        var dir = ArgText(args, "dir");
        var team = args.Member("team") is { IsText: true } t ? t : JsonValue.Null;
        var anchor = OpenRunAnchor(dir, team);
        if (!anchor.TryGetValue("id", out var anchorId) || anchorId.Kind != JsonKind.Text || anchorId.AsString() is not { Length: > 0 } id)
        {
            return JsonValue.Null;
        }

        string? started = null;
        // the anchor's exchange alone: the full listing asked the store once per exchange of the pool, on every re-read of a held team
        var exchange = _core.ListExchangesWithIds([dir], [id]).FirstOrDefault(e => e.Member("id") is { IsText: true } x && x.AsString() == id);
        if (exchange.Member("request")?.Member("path") is { IsText: true } requestPath
            && _core.ReadMsgFile(requestPath.AsString()).Member("fm")?.Member("created") is { IsText: true } created)
        {
            started = created.AsString();
        }

        var rows = ReadDecisions(DecisionLogPath(dir, id))["decisions"];
        return new JsonObject { ["run_id"] = id, ["started_at"] = started is null ? JsonValue.Null : started, ["rows"] = rows };
    }
}
