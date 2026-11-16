using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Watch;

/// <summary>The progress records the plan document is built from.</summary>
public interface IViewProgress
{
    /// <summary>The team's progress folder as it stands.</summary>
    TeamProgress Read(string pool, string team);

    /// <summary>The stream's folded plan; null when it has none.</summary>
    PlanState? ReadPlan(string pool, string team, string stream);

    /// <summary>A stream as the team file records it.</summary>
    ProgressStreamInfo Describe(string pool, string team, string stream);

    /// <summary>The team's queued items as the queue lane serves them; null when the reader has no such op.</summary>
    JsonArray? ReadQueue(string pool, string team) => null;

    /// <summary>The project's backlog (the team-less stream <c>backlog</c>); null when it has none.</summary>
    PlanState? ReadBacklog(string pool) => null;
}

/// <summary>The progress records read through the progress read ops of the state reader.</summary>
internal sealed class StateViewProgress(IViewStateReader state) : IViewProgress
{
    private JsonValue Op(string op, string pool, string team, string? stream = null)
    {
        var args = new JsonObject { ["dir"] = pool, ["team"] = team };
        if (stream is not null)
        {
            args["stream"] = stream;
        }

        return state.Read("progress", op, pool, args);
    }

    public TeamProgress Read(string pool, string team) => HierarchyModule.ReadProgressTeam(Op("read", pool, team));

    public PlanState? ReadPlan(string pool, string team, string stream) => HierarchyModule.ReadProgressPlan(Op("plan", pool, team, stream));

    public ProgressStreamInfo Describe(string pool, string team, string stream) => HierarchyModule.ReadProgressInfo(Op("describe", pool, team, stream));

    public PlanState? ReadBacklog(string pool) => HierarchyModule.ReadProgressPlan(Op("plan", pool, string.Empty, "backlog"));

    public JsonArray? ReadQueue(string pool, string team)
    {
        try
        {
            var result = state.Read("queue", "read", pool, new JsonObject { ["dir"] = pool, ["team"] = team });
            return result.Member("rows") is { IsArray: true } rows ? rows.AsArray() : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
