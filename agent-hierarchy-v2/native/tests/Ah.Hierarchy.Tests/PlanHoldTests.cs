using Ah.Abstractions;

namespace Ah.Hierarchy.Tests;

/// <summary>b459 step 27: a stream's folded plan is held, and a read folds only the rows appended to that stream since.</summary>
public sealed class PlanHoldTests
{
    private sealed class PlanLogs : IProgressLogSource
    {
        internal Dictionary<string, List<ProgressLogRow>> ByLog { get; } = [];

        internal void Append(string log, string body)
        {
            if (!ByLog.TryGetValue(log, out var rows))
            {
                ByLog[log] = rows = [];
            }

            rows.Add(new ProgressLogRow(rows.Count + 1, body));
        }

        public string Key(string hierDir) => hierDir;

        public IReadOnlyList<ProgressLogRow> Rows(string hierDir, string team, string log, long after = 0) =>
            ByLog.TryGetValue(log, out var rows) ? [.. rows.Where(r => r.Seq > after)] : [];

        public IReadOnlyList<string> Logs(string hierDir, string team) => [.. ByLog.Keys];

        public IReadOnlyList<string> Teams(string hierDir, string? log) => [];

        public long LedgerCursor(string hierDir, string team) => 0;

        public LedgerSince? LedgerAfterCursor(string hierDir, string team) => null;

        public void Take(IDbOpTransaction tx, string hierDir, string team, string log)
        {
        }

        public void SetLedgerCursor(IDbOpTransaction tx, string hierDir, string team, long seq)
        {
        }

        public string? ReleaseSeen(string hierDir) => null;

        public void SetReleaseSeen(IDbOpTransaction tx, string hierDir, string text)
        {
        }
    }

    private static string Event(string item, string ev) => $$"""{"ts":"2026-11-01T10:00:00Z","item":"{{item}}","event":"{{ev}}","by":"o","title":"T-{{item}}"}""";

    private static string[] States(PlanState? plan) => [.. plan!.Items.Select(static i => i.Item + "=" + i.State)];

    [Fact]
    public void AnAppendRefoldsOnlyItsStreamAndNegativeTheOtherStreamIsReusedAndBothEqualAFreshFold()
    {
        var logs = new PlanLogs();
        logs.Append("a.plan.jsonl", Event("x", "add"));
        logs.Append("b.plan.jsonl", Event("y", "add"));
        var reader = new ProgressReader(null!, null!, logs);
        _ = reader.ReadPlan("/p", "t", "a");
        var b = reader.ReadPlan("/p", "t", "b");
        var folds = reader.PlanFolds;

        logs.Append("a.plan.jsonl", Event("x", "start"));
        var a = reader.ReadPlan("/p", "t", "a");
        Assert.Same(b, reader.ReadPlan("/p", "t", "b"));
        Assert.Equal(folds + 1, reader.PlanFolds);
        Assert.Equal(States(new ProgressReader(null!, null!, logs).ReadPlan("/p", "t", "a")), States(a));
        Assert.NotEqual(States(b), States(a));
    }

    /// <summary>b459 step 27c: a dropped log reads no plan, and a log dropped and written anew folds from scratch; negative: an untouched log keeps its hold.</summary>
    [Fact]
    public void ADroppedOrRewrittenLogFoldsFromScratchAndNegativeAnUntouchedLogKeepsItsHold()
    {
        var logs = new PlanLogs();
        logs.Append("a.plan.jsonl", Event("x", "add"));
        logs.Append("a.plan.jsonl", Event("x", "start"));
        var reader = new ProgressReader(null!, null!, logs);
        var held = reader.ReadPlan("/p", "t", "a");
        Assert.Same(held, reader.ReadPlan("/p", "t", "a"));

        _ = logs.ByLog.Remove("a.plan.jsonl");
        Assert.Null(reader.ReadPlan("/p", "t", "a"));

        logs.Append("a.plan.jsonl", Event("y", "add"));
        logs.Append("a.plan.jsonl", Event("y", "start"));
        logs.Append("a.plan.jsonl", Event("z", "add"));
        Assert.Equal(["y=in_progress", "z=not_started"], States(reader.ReadPlan("/p", "t", "a")));
    }
}
