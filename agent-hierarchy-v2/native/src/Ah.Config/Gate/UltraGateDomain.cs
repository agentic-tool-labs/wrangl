using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Config;

/// <summary>
/// The <c>ultra-gate</c> state domain: the user's per-session answer to "may the Ultra-Advisor be dispatched", kept as the
/// <see cref="SessionMapFile.GateKey"/> <c>meta</c> row (b397 group 2). Session-scoped on purpose: a standing yes would turn a consent gate into a
/// formality. Written only through <c>write</c>; <c>read</c> is a read.
/// </summary>
[StateDomain("ultra-gate", "ultra-gate")]
internal sealed class UltraGateDomain(IHomeDocs? docs, IAhEnvironment environment, IClock clock) : IStateDomain
{
    internal const int Version = 1;

    internal static readonly string[] Choices = ["session", "each", "off"];

    private string Home => environment.HomeDirectory;

    public string Name => "ultra-gate";

    public IReadOnlyList<string> StoreNames => ["ultra-gate"];

    public bool HomeScoped => true;

    public bool DbOnly => true;

    public string StateDirectory(RequestContext context) => AhPaths.UserEntry(Home, AhPaths.UltraGateStateDir);

    public IReadOnlyList<StateOp> Ops =>
    [
        new StateOp("write", OpKind.Write, OpStyle.Replace, false, null, true, DbOnlyBody) { DbBody = Write },
        new StateOp("read", OpKind.Read, null, false, null, true, Read),
    ];

    public void AfterWrite(OpCall operation, OpResult result)
    {
    }

    private static OpResult DbOnlyBody(OpCall call, IStoreWriteContext? writer) => new(false, default, $"ultra-gate.{call.Op} is db-only", "op_failed");

    /// <summary>The stored document as it reads: <c>{version, sessions}</c>, empty whenever it cannot be trusted.</summary>
    internal JsonObject ReadState() => SessionMapFile.Parse(docs?.Read(SessionMapFile.GateKey), Version);

    /// <summary>The recorded choice for a session, or null when the user has not been asked yet.</summary>
    internal string? DecisionOf(JsonValue? sessionId)
    {
        var entry = ReadState()["sessions"].AsObject().Get(SessionMapFile.KeyOf(sessionId));
        return entry is { IsMap: true } e && e.AsObject().Get("choice") is { IsText: true } c && Choices.Contains(c.AsString()) ? c.AsString() : null;
    }

    private static JsonObject ArgsOf(OpCall call) =>
        JsonText.Parse(call.ArgsJson.Span) is { IsMap: true } v ? v.AsObject() : throw new InvalidOperationException("args must be an object");

    private static OpResult Ok(JsonValue value) => new(true, JsonText.ToUtf8(value, 0));

    private OpResult Write(OpCall call, IDbOpTransaction tx)
    {
        try
        {
            var args = ArgsOf(call);
            var sessionId = args.Get("session_id");
            var key = SessionMapFile.KeyOf(sessionId is { IsNull: true } ? null : sessionId);
            var state = SessionMapFile.Read(tx, SessionMapFile.GateKey, Version);
            var sessions = state["sessions"].AsObject();

            if (args.Get("clear") is { IsBool: true } clear && clear.AsBool())
            {
                if (sessions.Get(key) is not { IsTruthy: true })
                {
                    return Ok(new JsonObject { ["removed"] = false });
                }

                sessions.Remove(key);
                SessionMapFile.Put(tx, SessionMapFile.GateKey, state);
                return Ok(new JsonObject { ["removed"] = true });
            }

            var choice = args.Get("choice");
            if (choice is not { IsText: true } c || !Choices.Contains(c.AsString()))
            {
                var shown = choice is null ? "undefined" : JsonText.Stringify(choice.Value, 0);
                throw new InvalidOperationException($"unknown choice {shown} (expected: {string.Join(", ", Choices)})");
            }

            var entry = new JsonObject { ["choice"] = c.AsString(), ["at"] = Timestamps.ToIsoString(clock.UtcNow) };
            if (args.Get("cwd") is { IsTruthy: true } cwd)
            {
                entry["cwd"] = cwd;
            }

            sessions[key] = entry;
            state["sessions"] = SessionMapFile.Prune(sessions);
            SessionMapFile.Put(tx, SessionMapFile.GateKey, state);
            return Ok(new JsonObject { ["ok"] = true });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new OpResult(false, default, e.Message);
        }
    }

    private OpResult Read(OpCall call, IStoreWriteContext? context)
    {
        try
        {
            return Ok(ReadState());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new OpResult(false, default, e.Message);
        }
    }
}
