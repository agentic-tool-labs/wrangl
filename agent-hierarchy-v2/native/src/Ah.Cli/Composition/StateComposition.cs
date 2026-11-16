using Ah.Abstractions;
using Ah.Core.Json;
using Ah.State;

namespace Ah.Cli.Composition;

/// <summary>
/// Hands the state request handler to the one-shot <c>ahd state</c> verb, and to the daemon's <c>/v1/state</c> route only
/// when every composed domain declares its path arguments (the route's refusal of a path outside the request's hierarchy
/// directories works only for declared arguments). The service graph behind the handler is built on first use, so a
/// process that never writes state builds nothing.
/// </summary>
internal static class StateComposition
{
    /// <summary>
    /// Composed domains whose ops do not yet declare <c>StateOp.PathArgs</c> to match the Node registry. The daemon route is
    /// left unregistered while this is not empty; an owner removes its domain here when its declarations land.
    /// </summary>
    internal static IReadOnlyList<string> AwaitingPathArgs { get; } = [];

    internal static IStateService Compose(Lazy<ServiceGraph> graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var service = new LazyStateService(graph);
        StateModule.Compose(service);
        return service;
    }

    /// <summary>The handler the daemon's route is given, or null while a composed domain still awaits its path arguments.</summary>
    internal static IStateService? ForRoute(IStateService service) => AwaitingPathArgs.Count == 0 ? service : null;

    private sealed class LazyStateService(Lazy<ServiceGraph> graph) : IStateService
    {
        public Task<StateReply> HandleAsync(
            string method,
            string domain,
            string op,
            IReadOnlyDictionary<string, string> query,
            ReadOnlyMemory<byte> body,
            Func<string, Func<StateResult>, Task<StateResult>> scheduleWrite) =>
            graph.Value.State.HandleAsync(method, domain, op, query, body, scheduleWrite);

        public Task<StateReply> InvokeAsync(
            string domain,
            string op,
            bool viaPost,
            JsonValue? args,
            JsonValue? context,
            string? opId,
            Func<string, Func<StateResult>, Task<StateResult>> scheduleWrite,
            TimeSpan? lockWait = null) =>
            graph.Value.State.InvokeAsync(domain, op, viaPost, args, context, opId, scheduleWrite, lockWait);

        public bool HasOp(string domain, string op) => graph.Value.State.HasOp(domain, op);
    }
}
