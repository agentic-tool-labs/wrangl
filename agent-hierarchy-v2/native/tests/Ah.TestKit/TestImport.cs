using System.Text.RegularExpressions;
using Ah.StateDb;

namespace Ah.TestKit;

/// <summary>
/// Old state files a test planted, taken into the store by the real import pass (files-off-disk §3), as an upgrade's ahd start would:
/// since F2a/F2b a read no longer takes them. One helper, so no rig keeps its own list of the groups' file shapes.
/// </summary>
public static partial class TestImport
{
    // the F2b groups' file shapes (checkpoints, holds, grants, links, teams, progress), which only the pass takes; the F2a groups' rigs
    // plant through their stores' put, so taking those here too would double their rows. Group 1 is the hierarchy dir
    [GeneratedRegex(@"\A(.+)/(?:(?:links|teams|grants)/[^/]+\.json|team\.json|(?:holds|checkpoints|progress)/[^/]+/[^/]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex StateFile();

    /// <summary>The hierarchy dir whose import pass takes <paramref name="path"/>, or null when the pass takes no such file.</summary>
    public static string? HierDirOf(string path) => StateFile().Match(path) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>Runs the pass (forced) over <paramref name="hierDir"/>, for a rig that plants its files with plain writes before a read.</summary>
    public static void Dir(string ahHome, string hierDir) => Planted(ahHome, Directory.Exists(hierDir) ? Directory.EnumerateFiles(hierDir, "*", SearchOption.AllDirectories) : []);

    /// <summary>Runs the pass (forced) over each dir that holds one of <paramref name="paths"/>, creating the database first when one is planted.</summary>
    public static void Planted(string ahHome, IEnumerable<string> paths)
    {
        var dirs = paths.Select(HierDirOf).OfType<string>().Where(Directory.Exists).Distinct(StringComparer.Ordinal).ToList();
        if (dirs.Count == 0)
        {
            return;
        }

        if (StateDbModule.EnsureDatabase(ahHome) is { } why)
        {
            throw new InvalidOperationException(why);
        }

        foreach (var dir in dirs)
        {
            _ = StateDbModule.ImportHierDirFiles(ahHome, dir, force: true);
        }
    }
}
