using Ah.Abstractions;

namespace Ah.StateDb;

/// <summary>The <c>usage_rows</c> table (schema 41, b397 group 2) of the state database of <paramref name="ahHome"/>, read; the <c>usage</c> domain's DbBody is its writer.</summary>
internal sealed class StateDbUsageRows(string ahHome) : IUsageRows
{
    public IReadOnlyList<string> Bodies() =>
        StateDbStoreReads.ReadOrEmpty(ahHome, c => StateDbRows.Run(c, "SELECT body FROM usage_rows ORDER BY seq", []).Select(r => (string)r[0]!).ToList());
}
