using Ah.Abstractions;

namespace Ah.Daemon;

public static class DaemonModule
{
    private static DaemonDependencies? s_dependencies;
    private static IReadOnlyList<ModuleRegistry> s_registries = [];
    private static Action<IJobRegistry>? s_registerJobs;
    private static Action<IRouteRegistry>? s_registerRoutes;
    private static IHealthExtras? s_healthExtras;
    private static Action? s_onStop;

    public static ModuleRegistry Registry => GeneratedRegistry.Instance;

    /// <summary>
    /// Makes the dependencies and every module's registrations known to <c>serve</c>. The composition root calls it before
    /// running a verb: the daemon registers routes and jobs from every module, and its own registry is among them.
    /// </summary>
    /// <param name="dependencies">What the host runs on.</param>
    /// <param name="registries">Every module's generated registrations.</param>
    /// <param name="registerJobs">
    /// Called once when <c>serve</c> starts, with the registry modules add collaborator-bound jobs to
    /// (<c>Register(IJobRegistry, Dependencies)</c> of each module); null when no module has such a job.
    /// </param>
    /// <param name="registerRoutes">
    /// Called once when <c>serve</c> starts, with the registry modules add collaborator-bound routes to; null when no module has one.
    /// </param>
    /// <param name="healthExtras">Where the health body reads the fields other modules own (the watch tick's timers and ladder); null reports none.</param>
    /// <param name="onStop">Called once while the daemon shuts down, after the drain and before its files are removed; null does nothing.</param>
    public static void Compose(
        DaemonDependencies dependencies,
        IReadOnlyList<ModuleRegistry> registries,
        Action<IJobRegistry>? registerJobs = null,
        Action<IRouteRegistry>? registerRoutes = null,
        IHealthExtras? healthExtras = null,
        Action? onStop = null)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(registries);
        Volatile.Write(ref s_registries, registries);
        Volatile.Write(ref s_registerJobs, registerJobs);
        Volatile.Write(ref s_registerRoutes, registerRoutes);
        Volatile.Write(ref s_healthExtras, healthExtras);
        Volatile.Write(ref s_onStop, onStop);
        Volatile.Write(ref s_dependencies, dependencies);
    }

    internal static DaemonDependencies? Dependencies => Volatile.Read(ref s_dependencies);

    internal static IReadOnlyList<ModuleRegistry> Registries => Volatile.Read(ref s_registries);

    internal static Action<IJobRegistry>? RegisterJobs => Volatile.Read(ref s_registerJobs);

    internal static Action<IRouteRegistry>? RegisterRoutes => Volatile.Read(ref s_registerRoutes);

    internal static IHealthExtras? HealthExtrasSource => Volatile.Read(ref s_healthExtras);

    internal static Action? OnStop => Volatile.Read(ref s_onStop);

    /// <summary>The outcome of one <c>POST /ui/v1/answer</c> body, the route's own request table, for callers outside the daemon (<c>ah msg decision ui-answer</c>).</summary>
    public static UiAnswerResult RunUiAnswer(UiAnswerSupport support, ReadOnlySpan<byte> body) => UiAnswerRoute.Run(support, body);

    /// <summary>
    /// Registers <c>GET /v1/peers/pending</c>: the rows pending for a session over the peer log. <paramref name="served"/> says whether the daemon
    /// serves a hierarchy dir; any other dir is a 404 and nothing is read for it. <paramref name="mayRead"/> says whether the caller process may read a
    /// session's rows; when it may not the answer is a 403.
    /// </summary>
    public static void RegisterPeerPending(IRouteRegistry routes, Func<string, bool> served, Func<int?, string, bool> mayRead, Func<Ah.Core.Json.JsonValue, IReadOnlyList<Ah.Core.Json.JsonValue>> pendingFor)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Add(PeerPendingRoute.Key, PeerPendingRoute.Handler(served, mayRead, pendingFor));
    }

    /// <summary>Registers <c>GET /v1/view</c> and <c>GET /v1/status</c> (and the inspect routes it was given). The composition root calls it from its <c>registerRoutes</c>.</summary>
    public static void RegisterViews(IRouteRegistry routes, ViewServiceDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(dependencies);
        var service = new ViewService(dependencies);
        routes.Add("GET /v1/view", service.ViewAsync);
        if (dependencies.Model is not null)
        {
            routes.Add("GET /v1/view/{name}", service.ManifestAsync);
            routes.Add("GET /v1/list/{*id}", service.ListAsync);
        }

        routes.Add("GET /v1/actions", service.ActionsAsync);
        routes.Add("GET /v1/actions/{*id}", service.ActionsAsync);
        routes.Add("GET /v1/read-model", service.ReadModelAsync);
        if (dependencies.Rebuild is not null)
        {
            routes.Add("POST /v1/read-model/rebuild", service.RebuildAsync);
        }
        routes.Add("GET /v1/status", service.StatusAsync);
        if (dependencies.InspectTimers is { } timers)
        {
            routes.Add("GET /v1/inspect/timers", ViewService.Inspection("session", timers));
        }

        if (dependencies.InspectEvents is { } events)
        {
            routes.Add("GET /v1/inspect/events", ViewService.Inspection("address", events));
        }
    }
}
