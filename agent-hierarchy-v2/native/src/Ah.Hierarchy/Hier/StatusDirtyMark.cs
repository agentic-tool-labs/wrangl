using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>
/// Wraps a database op body so that, in the write's own transaction, it leaves the <see cref="StatusDirty"/> mark its post-commit status
/// delta clears (ah-msgs-fast-2 §3.8 H1, ruling B). A crash between the commit and the delta leaves the mark for recovery.
/// </summary>
internal static class StatusDirtyMark
{
    internal static Func<OpCall, IDbOpTransaction, OpResult> Body(string domain, Func<OpCall, IDbOpTransaction, OpResult> body) =>
        (call, tx) => Mark(domain, call, tx, body(call, tx));

    internal static Func<OpCall, IDbOpTransaction, IStoreWriteContext, OpResult> Body(string domain, Func<OpCall, IDbOpTransaction, IStoreWriteContext, OpResult> body) =>
        (call, tx, writer) => Mark(domain, call, tx, body(call, tx, writer));

    private static OpResult Mark(string domain, OpCall call, IDbOpTransaction tx, OpResult result)
    {
        if (result.Ok)
        {
            var dir = JsonText.Parse(System.Text.Encoding.UTF8.GetString(call.ArgsJson.Span)).Member("dir") is { IsText: true } d ? d.AsString() : string.Empty;
            tx.Execute(StatusDirty.MarkSql, StatusDirty.KeyOf(domain, call.Op, call.ArgsJson), dir);
        }

        return result;
    }
}
