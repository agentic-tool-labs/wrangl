using Ah.Core.Files;
using Ah.Core.Json;
using Ah.State;
using Ah.Watch;

namespace Ah.Cli.Composition;

/// <summary>
/// <see cref="IViewStateReader"/> over the in-process <see cref="IInternalStateRead"/>: a read is dispatched by
/// <see cref="IInternalStateRead.ReadInternalAsync"/>, the route's own read path less the claim check, and a write op is refused there.
/// Inside a view build's <see cref="ListingScope"/> a read op is dispatched once per (domain, op, hierarchy directory, arguments); every
/// other caller of the same read in that build is answered from the first reply, parsed anew so no caller shares a document.
/// </summary>
internal sealed class StateServiceReader(IInternalStateRead state) : IViewStateReader
{
    public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var key = string.Join('\u001f', "state-read", domain, op, hierarchyDir, JsonText.Stringify(JsonValue.FromObject(args), 0));
        // ah2-1: the call that runs the op takes the value the op handed out, when it did; a later call in the same scope parses the
        // held bytes, so no two callers share one value
        JsonValue? handed = null;
        object? beside = null;
        var body = ListingScope.Once(key, () => Dispatch(domain, op, hierarchyDir, args, out handed, out beside));
        var result = handed ?? JsonText.Parse(body).Member("result") ?? JsonValue.Null;
        if (beside is not null && result.IsMap)
        {
            _beside.AddOrUpdate(result.AsObject(), beside);
        }

        return result;
    }

    // b459 C1: what an op handed out beside its result (status.read's witness), kept for exactly the document this reader parsed from
    // that reply. Only the call that ran the op has it; a later call in the same scope parses its own copy and has none.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<JsonObject, object> _beside = [];

    /// <summary>What the op that produced <paramref name="result"/> handed out beside it, or null: for a document this reader returned, and only for that one.</summary>
    internal object? Beside(JsonValue result) => result.IsMap && _beside.TryGetValue(result.AsObject(), out var beside) ? beside : null;

    private byte[] Dispatch(string domain, string op, string hierarchyDir, JsonObject args, out JsonValue? handed, out object? beside)
    {
        handed = null;
        beside = null;
        var reply = state.ReadInternalAsync(domain, op, JsonValue.FromObject(args), hierarchyDir).GetAwaiter().GetResult();
        if (reply.Status != 200)
        {
            throw new InvalidOperationException(
                $"state read {domain}.{op} for {hierarchyDir}: {reply.Status} {reply.Code} {JsonText.Parse(reply.Body.Span).Member("error")?.Member("message")?.AsString()}");
        }

        handed = reply.Value as JsonValue?;
        beside = handed is null ? reply.Value : null;
        return System.Runtime.InteropServices.MemoryMarshal.TryGetArray(reply.Body, out var held) && held.Offset == 0 && held.Count == held.Array!.Length
            ? held.Array
            : reply.Body.ToArray();
    }
}
