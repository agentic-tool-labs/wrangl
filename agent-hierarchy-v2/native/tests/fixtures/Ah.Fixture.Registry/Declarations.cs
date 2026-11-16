using Ah.Abstractions;

namespace Ah.Fixture.Registry;

public static class FixtureModule
{
    public static ModuleRegistry Registry => GeneratedRegistry.Instance;
}

internal static class Declarations
{
    [Verb("fixture-verb")]
    internal static int Echo(string[] rest, TextWriter stdout, TextWriter stderr)
    {
        stdout.Write(string.Join(' ', rest));
        return 7;
    }

    // Same signature as a verb, no attribute: must not be registered.
    internal static int NotAVerb(string[] rest, TextWriter stdout, TextWriter stderr) => 0;

    [Hook("fixture-hook")]
    internal static HookOutcome OnHook(HookInvocation invocation) => new(default, 0);

    [Route("POST /fixture")]
    internal static Task<RouteResponse> OnRoute(RouteRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new RouteResponse(204, new Dictionary<string, string>(), default));

    [Job("fixture-job", 5000)]
    internal static Task OnJob(CancellationToken cancellationToken) => Task.CompletedTask;
}

[StateDomain("fixture", "fixture/a.json", "fixture/b.json")]
internal sealed class FixtureDomain : IStateDomain
{
    public string Name => "fixture";

    public IReadOnlyList<string> StoreNames => ["fixture/a.json", "fixture/b.json"];

    public IReadOnlyList<StateOp> Ops => [];

    public string StateDirectory(RequestContext context) => context.HierarchyDirectory;

    public void AfterWrite(OpCall operation, OpResult result)
    {
    }
}
