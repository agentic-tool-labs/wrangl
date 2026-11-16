using Ah.Hierarchy;

namespace Ah.Watch.Tests;

/// <summary>The plan-fold factory the tests hand to the progress lane, as the composition root does.</summary>
internal static class TestFolds
{
    public static readonly Func<IPlanFold> New = HierarchyModule.CreatePlanFold;

    /// <summary>Options that switch the progress lane on.</summary>
    public static ReadModelOptions Options => new() { NewPlanFold = New };
}
