namespace Ah.StateDb;

/// <summary>
/// Keeps the query planner's statistics for the state database. Every refresh is an <c>ANALYZE</c> bounded by <c>analysis_limit</c>:
/// <c>PRAGMA optimize</c> on a fresh connection only looks at the tables that connection has queried, and an older library does not
/// create <c>sqlite_stat1</c> with it, so it would never refresh the statistics of a table the refreshing connection has not used.
/// </summary>
internal static class StateDbStats
{
    /// <summary>The most index entries one analysis reads per index.</summary>
    internal const int AnalysisLimit = 1000;

    /// <summary>Refreshes the statistics inside <paramref name="tx"/>; returns <c>analyze</c>.</summary>
    internal static string Refresh(IStateDbTransaction tx)
    {
        _ = tx.Query("PRAGMA analysis_limit = " + AnalysisLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        tx.Execute("ANALYZE");
        return "analyze";
    }

    /// <summary>Refreshes the statistics through <paramref name="direct"/>; a failure comes back as <c>failed: reason</c> and is never thrown.</summary>
    internal static string TryRefresh(IStateDbDirect direct)
    {
        try
        {
            var result = direct.Write(Refresh);
            return result.Ok ? result.Value! : "failed: " + result.Message;
        }
        catch (Exception e) when (e is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return "failed: " + e.Message;
        }
    }
}
