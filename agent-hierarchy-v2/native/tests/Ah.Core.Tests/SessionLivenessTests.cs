using Ah.Abstractions;
using Ah.Core.Time;

namespace Ah.Core.Tests;

public sealed class SessionLivenessTests
{
    private const string Recorded = "Mon Oct 19 00:27:41 2026";
    private const string Later = "Tue Oct 20 10:00:00 2026";
    private static readonly DateTimeOffset Written = new(2026, 10, 19, 0, 27, 41, TimeSpan.Zero);

    private sealed class Inspector(bool alive) : IProcessInspector
    {
        public bool PidAlive(int pid) => alive;

        public bool PidOwned(int pid) => alive;

        public string? CommandLine(int pid) => null;
    }

    private sealed class Table(string? start, string? local = null) : IProcessTable
    {
        public IReadOnlyList<int>? Ancestors(int pid) => null;

        public bool? HasAncestor(int pid, int candidate) => null;

        public string? StartTime(long pid) => start;

        public string? LocalStartTime(long pid) => local;
    }

    private static SessionLiveness Judge(bool alive, string? start, long? pid, string? pidStart, DateTimeOffset? written, string? local = null) =>
        SessionLivenessJudge.Judge(new Inspector(alive), new Table(start, local), pid, pidStart, written);

    [Fact]
    public void ADeadPidIsGone() => Assert.Equal(SessionLife.Gone, Judge(false, null, 4242, Recorded, Written).Life);

    [Fact]
    public void ALivePidWithTheRecordedStartInTheStoredFormatIsCounted() =>
        Assert.Equal(SessionLife.Counted, Judge(true, Recorded, 4242, Recorded, Written).Life);

    [Fact]
    public void ALivePidWithTheRecordedStartInTheLocalZoneFormOlderRecordsHoldIsCounted() =>
        Assert.Equal(SessionLife.Counted, Judge(true, Later, 4242, "Sun Oct 18 20:27:41 2026", Written, local: "Sun Oct 18 20:27:41 2026").Life);

    [Fact]
    public void ALivePidWhoseStartDiffersFromTheRecordedOneIsReused() =>
        Assert.Equal(SessionLife.Reused, Judge(true, Later, 4242, Recorded, Written).Life);

    [Theory]
    [InlineData("garbage")]
    [InlineData("Mon Oct 19 25:61:99 2026")]
    [InlineData("1792369661")]
    public void ARecordedStartThatCannotBeParsedIsCountedNotReused(string recorded) =>
        Assert.Equal(SessionLife.Counted, Judge(true, Recorded, 4242, recorded, Written).Life);

    [Fact]
    public void ASingleSpacedRecordedDayAgainstADoubleSpacedTableStartIsCounted() =>
        Assert.Equal(SessionLife.Counted, Judge(true, "Mon Oct  5 00:27:41 2026", 4242, "Mon Oct 5 00:27:41 2026", Written).Life);

    [Theory]
    [InlineData("Mon Oct 19 00:27:42 2026")]
    [InlineData("Mon Oct 19 00:27:40 2026")]
    public void ARecordedStartOneSecondOffTheProcessesIsCounted(string recorded) =>
        Assert.Equal(SessionLife.Counted, Judge(true, Recorded, 4242, recorded, Written).Life);

    [Theory]
    [InlineData("Sun Oct 18 19:27:41 2026")]
    [InlineData("Mon Oct 19 05:57:41 2026")]
    public void ARecordedStartThatDiffersByAWholeZoneStepMayBeTheLocalFormOfAnotherZoneAndIsCounted(string recorded) =>
        Assert.Equal(SessionLife.Counted, Judge(true, Recorded, 4242, recorded, Written).Life);

    [Theory]
    [InlineData("Mon Oct 19 00:34:41 2026")]
    [InlineData("Mon Oct 19 00:20:41 2026")]
    [InlineData("Mon Oct 19 06:03:41 2026")]
    public void ARecordedStartThatParsesAndIsNotNearAZoneStepFromTheProcessesIsReused(string recorded) =>
        Assert.Equal(SessionLife.Reused, Judge(true, Recorded, 4242, recorded, Written).Life);

    [Fact]
    public void WithNoRecordedStartAProcessThatStartedMoreThanTwoSecondsAfterTheRowWasWrittenIsReused() =>
        Assert.Equal(SessionLife.Reused, Judge(true, "Mon Oct 19 00:27:44 2026", 4242, null, Written).Life);

    [Theory]
    [InlineData("Mon Oct 19 00:27:43 2026")]
    [InlineData("Mon Oct 19 00:27:41 2026")]
    [InlineData("Sun Oct 18 12:00:00 2026")]
    public void WithNoRecordedStartAProcessThatStartedWithinTwoSecondsOfTheRowOrBeforeItIsCounted(string start) =>
        Assert.Equal(SessionLife.Counted, Judge(true, start, 4242, null, Written).Life);

    [Fact]
    public void AStartTimeThatCannotBeReadIsCountedWithOrWithoutARecordedStart()
    {
        Assert.Equal(SessionLife.Counted, Judge(true, null, 4242, Recorded, Written).Life);
        Assert.Equal(SessionLife.Counted, Judge(true, null, 4242, null, Written).Life);
        Assert.Equal(SessionLife.Counted, Judge(true, "not a date", 4242, null, Written).Life);
    }

    [Fact]
    public void WithNoRecordedStartAndNoWriteTimeAnAliveProcessIsCounted() =>
        Assert.Equal(SessionLife.Counted, Judge(true, Later, 4242, null, null).Life);

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(5_000_000_000L)]
    public void ARowWithNoUsablePidIsCountedWhateverTheTableSays(long? pid) =>
        Assert.Equal(SessionLife.Counted, Judge(false, Later, pid, Recorded, Written).Life);

    private static readonly DateTimeOffset Boot = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    private static SessionLiveness JudgeAfterBoot(string? start, string? pidStart, DateTimeOffset? written, DateTimeOffset? boot) =>
        SessionLivenessJudge.Judge(new Inspector(true), new Table(start), 4242, pidStart, written, boot);

    [Fact]
    public void ARowWithNoStartRecordedBeforeTheBootIsNotCountedEvenWhenItsPidIsAliveAgain()
    {
        var verdict = JudgeAfterBoot(Later, null, Written, Boot);

        Assert.Equal(SessionLife.Reused, verdict.Life);
        Assert.Contains("recorded before the boot", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowWhoseRecordedStartIsBeforeTheBootIsNotCountedWhateverThePidProbeSays()
    {
        var verdict = JudgeAfterBoot(Recorded, Recorded, Written, Boot);

        Assert.Equal(SessionLife.Reused, verdict.Life);
        Assert.Contains("recorded before the boot", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeARowWhoseStartIsAfterTheBootAndMatchesTheProcessIsCounted() =>
        Assert.Equal(SessionLife.Counted, JudgeAfterBoot(Later, Later, new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero), Boot).Life);

    [Fact]
    public void NegativeARowWithNoStartWrittenAfterTheBootIsJudgedAsWithoutABoot() =>
        Assert.Equal(
            JudgeAfterBoot(Recorded, null, new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero), null).Life,
            JudgeAfterBoot(Recorded, null, new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero), Boot).Life);

    [Fact]
    public void WithTheBootTimeUnreadableTheJudgmentIsExactlyToday()
    {
        Assert.Equal(SessionLife.Counted, JudgeAfterBoot(Recorded, null, Written, null).Life);
        Assert.Equal(SessionLife.Counted, JudgeAfterBoot(Recorded, Recorded, Written, null).Life);
    }

    [Fact]
    public void OnlyGoneAndReusedAreIgnored()
    {
        Assert.True(new SessionLiveness(SessionLife.Gone, "x").Ignored);
        Assert.True(new SessionLiveness(SessionLife.Reused, "x").Ignored);
        Assert.False(new SessionLiveness(SessionLife.Counted, "x").Ignored);
    }
}
