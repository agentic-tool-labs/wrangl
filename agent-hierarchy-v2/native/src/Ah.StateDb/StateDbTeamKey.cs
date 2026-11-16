using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>
/// A team is told apart by the hierarchy directory its file lives in and by its name, so the same name in two directories is two
/// teams. The key is derived from the team file's path: the directory two levels up (resolved through symbolic links) and the file
/// name without <c>.json</c>.
/// </summary>
internal static class StateDbTeamKey
{
    /// <summary>The name a legacy <c>&lt;hier&gt;/team.json</c> is held under: such a file holds no team name, and no valid team name has a dot, so it cannot meet a named team.</summary>
    internal const string LegacyName = "team.json";

    internal static (string HierDir, string Name)? Of(string? teamFilePath)
    {
        if (string.IsNullOrEmpty(teamFilePath) || teamFilePath[0] != '/') // Windows drive and UNC paths are out of scope for the state database
        {
            return null;
        }

        var teams = Path.GetDirectoryName(teamFilePath);
        if (teams is not null && Path.GetFileName(teamFilePath) == LegacyName && Path.GetFileName(teams) != "teams")
        {
            return (RealPath(teams), LegacyName);
        }

        var home = teams is null ? null : Path.GetDirectoryName(teams);
        var name = Path.GetFileNameWithoutExtension(teamFilePath);
        return home is null || name.Length == 0 ? null : (RealPath(home), name);
    }

    /// <summary>The path with every symbolic link followed and each part in its on-disk case, so every spelling of one directory gives one key; links that lead back to themselves keep the path as given.</summary>
    internal static string RealPath(string path) => MsgRows.PoolKey(path);
}
