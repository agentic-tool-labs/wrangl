using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Watch;

namespace Ah.Cli.Composition;

/// <summary>
/// The <c>dispatches</c> state domain: one read op, <c>open {session_id, now_ms}</c>, answering with the dispatch candidates the
/// watch host reads for an Orchestrator session (<c>{candidates: [...]}</c>, the form <see cref="WatchModule.DispatchCandidateToJson"/> writes).
/// With no watch host yet (before the daemon starts, or in the CLI composition) the answer is no candidates. It is read-only,
/// <c>Daemon=false</c> and declares no path argument; it is scoped to the ah home, not to a hierarchy dir.
/// </summary>
internal sealed class DispatchesStateDomain : IStateDomain
{
    private readonly IAhEnvironment _environment;
    private readonly Func<WatchHostAdapter?> _host;

    internal DispatchesStateDomain(IAhEnvironment environment, Func<WatchHostAdapter?> host)
    {
        _environment = environment;
        _host = host;
        Ops = [new StateOp("open", OpKind.Read, null, false, null, false, Open)];
    }

    public string Name => "dispatches";

    public IReadOnlyList<string> StoreNames { get; } = ["dispatches"];

    public bool HomeScoped => true;

    public IReadOnlyList<StateOp> Ops { get; }

    public string StateDirectory(RequestContext context) => NodePath.Join(_environment.AhHome, "registrations");

    public void AfterWrite(OpCall operation, OpResult result)
    {
    }

    private OpResult Open(OpCall call, IStoreWriteContext? context)
    {
        try
        {
            var args = call.ArgsJson.IsEmpty ? new JsonObject() : JsonText.Parse(call.ArgsJson.Span);
            if (args.Member("session_id") is not { IsText: true } session || session.AsString().Length == 0)
            {
                throw new InvalidOperationException("The \"session_id\" argument must be a non-empty string");
            }

            if (args.Member("now_ms") is not { IsNumber: true } now)
            {
                throw new InvalidOperationException("The \"now_ms\" argument must be a number");
            }

            var candidates = new JsonArray();
            foreach (var candidate in _host()?.Dispatches(session.AsString(), now.AsNumber()) ?? [])
            {
                candidates.Add(WatchModule.DispatchCandidateToJson(candidate));
            }

            return new OpResult(true, JsonText.ToUtf8(new JsonObject { ["candidates"] = candidates }, 0));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new OpResult(false, ReadOnlyMemory<byte>.Empty, e.Message);
        }
    }
}
