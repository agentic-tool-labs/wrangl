using Ah.Abstractions;
using Ah.Core.Digests;
using Ah.Core.Json;

namespace Ah.State;

/// <summary>The checks and hooks every entry point to the state engine shares: the client, the daemon's route and the one-shot verb.</summary>
internal static class StateRules
{
    internal static StateResult Fail(string code, string message) => new(false, ReadOnlyMemory<byte>.Empty, message, code);

    /// <summary>
    /// A write to a digest-precondition op needs a usable <c>path</c> and <c>expect</c> (a 64-digit hex digest or
    /// <c>absent</c>); every other op passes. Returns the <c>bad_request</c> answer, or null.
    /// </summary>
    internal static StateResult? Preflight(string domain, string op, StateOp spec, JsonValue args)
    {
        if (!spec.DigestPrecondition)
        {
            return null;
        }

        if (args.Member("path") is not { IsText: true } p || p.AsString().Length == 0)
        {
            return Fail("bad_request", $"{domain}.{op} needs the target file in path");
        }

        if (!(args.Member("expect") is { IsText: true } e && ContentDigest.IsValidExpect(e.AsString())))
        {
            return Fail("bad_request", $"{domain}.{op} needs expect: the digest of the file as the caller read it, or \"absent\"");
        }

        return null;
    }

    /// <summary>Whether a write's result is the digest precondition's <c>{"conflict": true}</c>: nothing was written, so no after-write hook runs.</summary>
    internal static bool IsConflict(ReadOnlyMemory<byte> resultJson)
    {
        if (resultJson.IsEmpty)
        {
            return false;
        }

        try
        {
            return JsonText.Parse(resultJson.Span).Member("conflict") is { IsTruthy: true };
        }
        catch (JsonParseException)
        {
            return false;
        }
    }

    /// <summary>
    /// The domain's after-write hook, run once by the entry point that made the write, after the lock was released. Never
    /// for a read, a refused write or a conflict.
    /// </summary>
    internal static void AfterWrite(
        IReadOnlyList<IStateDomain> domains,
        string domain,
        string op,
        ReadOnlyMemory<byte> argsJson,
        ReadOnlyMemory<byte> resultJson,
        RequestContext context)
    {
        if (IsConflict(resultJson))
        {
            return;
        }

        domains.FirstOrDefault(d => d.Name == domain)?.AfterWrite(
            new OpCall(op, argsJson, context),
            new OpResult(true, resultJson));
    }
}
