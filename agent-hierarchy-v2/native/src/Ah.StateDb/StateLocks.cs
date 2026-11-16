using System.Security.Cryptography;
using System.Text;

namespace Ah.StateDb;

/// <summary>
/// Where a files-path state op takes its lock when the engine has a database (files-off-disk §13, adr-20): one file under
/// <c>&lt;AH_HOME&gt;/state/locks/</c> per (domain, state directory, lock name). The state directory is the lock's identity and is never created.
/// </summary>
internal static class StateLocks
{
    internal static string Directory(string ahHome) => Path.Join(ahHome, "state", "locks");

    /// <summary>The one function every taker of a state lock calls (the engine and the msgs cutover import), so they contend on one file.</summary>
    internal static string PathFor(string ahHome, string domain, string stateDirectory, string lockName) =>
        Path.Join(Directory(ahHome), domain + "-" + DirKey(stateDirectory) + "-" + lockName);

    /// <summary>
    /// The <c>applied_writes</c> key of an op id, scoped to its state directory (op ids such as <c>park:q1</c> repeat across directories);
    /// a null directory gives the old unscoped key, <c>&lt;mode key&gt;:&lt;op id&gt;</c>.
    /// </summary>
    internal static string WriteId(string modeKey, string? stateDirectory, string operationId) =>
        stateDirectory is null ? modeKey + ":" + operationId : modeKey + ":" + DirKey(stateDirectory) + ":" + operationId;

    private static string DirKey(string stateDirectory) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(StateDbTeamKey.RealPath(stateDirectory))))[..16];
}
