namespace Ah.Hooks.Lifecycle;

/// <summary>Where a team's progress records live for a hook running in <c>cwd</c>.</summary>
internal static class ProgressPool
{
    /// <summary>The main checkout's hierarchy directory when <paramref name="cwd"/> is in a linked work tree, else the directory of <paramref name="cwd"/> itself.</summary>
    internal static string Of(GateServices s, string cwd) =>
        Ah.Hierarchy.HierarchyModule.ProgressPoolOf(s.Config.MainHierarchyDir(cwd), s.Config.HierarchyDir(cwd));
}
