using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.State;

/// <summary>Exposes <see cref="StateRequestChecks.CheckContext"/> to the modules that act on a caller context outside a state call.</summary>
internal sealed class RequestContextChecker(StateRequestChecks checks) : IRequestContextChecker
{
    public RequestContextProblem? Check(RequestContext context)
    {
        var fields = new JsonObject
        {
            ["cwd"] = context.Cwd,
            ["hierarchy_dir"] = context.HierarchyDirectory,
        };
        if (context.MainHierarchyDirectory.Length > 0)
        {
            fields["main_hierarchy_dir"] = context.MainHierarchyDirectory;
        }

        if (context.SocketDirectory.Length > 0)
        {
            fields["messaging_socket_dir"] = context.SocketDirectory;
        }

        var problem = checks.CheckContext(fields);
        return problem is null ? null : new RequestContextProblem(problem.Message, problem.Field);
    }
}
