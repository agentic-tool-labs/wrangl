using Ah.Core.Json;

namespace Ah.Watch.Tests;

/// <summary>The user's <c>alarm.command</c>: an Orchestrator down or waiting on a question runs it once per episode.</summary>
public sealed class UserAlarmTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private sealed class Host : IStallHost
    {
        public string? Address { get; set; } = "orchestrator@tm";

        public DateTimeOffset? Asked { get; set; }

        public bool CommandSet { get; set; } = true;

        public bool Listed { get; set; } = true;

        public List<string> Lines { get; } = [];

        public List<JsonObject> Rows { get; } = [];

        private readonly Dictionary<string, string> _told = new(StringComparer.Ordinal);

        public bool Owner() => true;

        public int TeamReads { get; private set; }

        public IReadOnlyList<StallTeam> Teams()
        {
            TeamReads++;
            return Listed ? [new StallTeam("tm", "/h", "/cwd", Address, "orch") { QueuedOnly = QueuedOnly }] : [];
        }

        public bool QueuedOnly { get; set; }

        public int MemberReads { get; private set; }

        public IReadOnlyList<StallMember> Members(StallTeam team)
        {
            MemberReads++;
            return [];
        }

        public StallJudgement Judge(StallTeam team, StallMember member) => throw new InvalidOperationException();

        public IReadOnlyList<string> OpenWork(StallTeam team, StallMember member) => [];

        public void Mail(WatchMail mail)
        {
        }

        void IStallHost.Journal(JsonObject row) => Rows.Add(row);

        public string? ReportedSession(string key) => _told.GetValueOrDefault(key);

        public void SetReportedSession(string key, string? session)
        {
            if (session is null)
            {
                _ = _told.Remove(key);
            }
            else
            {
                _told[key] = session;
            }
        }

        private readonly Dictionary<string, UserAlarm> _alarms = new(StringComparer.Ordinal);

        public UserAlarm? Alarm(string key) => _alarms.GetValueOrDefault(key);

        public void SetAlarm(string key, UserAlarm? alarm)
        {
            if (alarm is null)
            {
                _ = _alarms.Remove(key);
            }
            else
            {
                _alarms[key] = alarm;
            }
        }

        public Dictionary<string, int> Waiting { get; } = [];

        public IReadOnlyDictionary<string, int> WaitingEvents(StallTeam team) => Waiting;

        public DateTimeOffset? AskingSince(string hierarchyDir, string session) => Asked;

        public LeadUncarried? Stalled { get; set; }

        public LeadUncarried? Uncarried(StallTeam team) => Stalled;

        public bool HeldTeam { get; set; }

        public IReadOnlyList<string> AlarmKeys() => [.. _alarms.Keys.Where(static k => k.StartsWith("alarm/", StringComparison.Ordinal))];

        public bool TeamHeld(string team) => HeldTeam;

        public bool StartAlarmCommand(string line, string team, string kind, Action<int> exited)
        {
            if (!CommandSet)
            {
                return false;
            }

            if (Throw)
            {
                throw new InvalidOperationException("the start failed");
            }

            Lines.Add(line);
            if (Hold)
            {
                Held.Add(exited);
            }
            else
            {
                exited(0);
            }

            return true;
        }

        /// <summary>When set, a started command keeps running until the test calls what <see cref="Held"/> holds.</summary>
        public bool Hold { get; set; }

        public List<Action<int>> Held { get; } = [];

        /// <summary>When set, the start throws before it calls anything back.</summary>
        public bool Throw { get; set; }

        public void BeginSweep()
        {
        }

        public void ForgetStaleReported(TimeSpan unseenFor)
        {
        }
    }

    private static Action<int> Sweeper(Host host, int warmUpMinutes = 0)
    {
        var minutes = 0;
        var watch = new StallWatch(host, StallSettings.Defaults with { Down = TimeSpan.FromMinutes(2), WarmUp = TimeSpan.FromMinutes(warmUpMinutes) }, () => T0.AddMinutes(minutes));
        return at =>
        {
            minutes = at;
            watch.Sweep();
        };
    }

    [Fact]
    public void AQuestionPastItsTimeRunsTheCommandOnceAndANewQuestionRunsItAgain()
    {
        var host = new Host { Asked = T0 };
        var sweep = Sweeper(host);

        sweep(14);
        Assert.Empty(host.Lines);
        sweep(15);
        sweep(16);
        var line = Assert.Single(host.Lines);
        Assert.Contains("team tm", line, StringComparison.Ordinal);
        Assert.Equal("alarm.command", Assert.Single(host.Rows).Str("delivered_to"));

        host.Asked = null;
        sweep(17);
        Assert.Contains("owner-asking cleared (2m)", host.Lines[1], StringComparison.Ordinal);
        host.Asked = T0.AddMinutes(20);
        sweep(35);
        Assert.Equal(3, host.Lines.Count);
    }

    [Fact]
    public void AnUncarriedLeadIsToldOnceAsOwnerStalledAndALaterTurnClearsIt()
    {
        var host = new Host { Stalled = new LeadUncarried(3, "no-pane") };
        var sweep = Sweeper(host);

        sweep(1);
        sweep(2);
        Assert.Equal("orchestrator@tm has 3 unread and ah could not wake it (no-pane). Type anything in its pane.", Assert.Single(host.Lines));
        Assert.NotNull(host.Alarm("alarm/tm/owner-stalled"));

        host.Stalled = null;
        sweep(3);
        Assert.Contains("owner-stalled cleared", host.Lines[1], StringComparison.Ordinal);
        Assert.Null(host.Alarm("alarm/tm/owner-stalled"));
    }

    [Fact]
    public void ADeliveredOwnerStalledLineIsRefreshedWithoutTellingAndNotWhileTheSlotIsTaken()
    {
        var host = new Host { Stalled = new LeadUncarried(3, "no-pane") };
        var sweep = Sweeper(host);
        sweep(1);
        Assert.True(host.Alarm("alarm/tm/owner-stalled")!.Delivered);

        host.Stalled = new LeadUncarried(5, "no-pane");
        sweep(10);
        var refreshed = host.Alarm("alarm/tm/owner-stalled")!;
        Assert.Contains("has 5 unread", refreshed.Line, StringComparison.Ordinal);
        Assert.Equal(T0.AddMinutes(1), refreshed.LastToldAt);
        Assert.Single(host.Lines);
        Assert.Single(host.Rows);

        // Negative: an owner-asking command still running holds the team's slot, so this sweep leaves the line as it was.
        host.Hold = true;
        host.Asked = T0.AddMinutes(-30);
        host.Stalled = new LeadUncarried(7, "no-pane");
        sweep(11);
        Assert.Equal(2, host.Lines.Count);
        Assert.Contains("has 5 unread", host.Alarm("alarm/tm/owner-stalled")!.Line, StringComparison.Ordinal);
    }

    [Fact]
    public void ARaisedAlarmOfATeamPutOnHoldIsClearedWithNoLineAndOneStillWatchedIsKept()
    {
        var host = new Host { Stalled = new LeadUncarried(2, "no-pane") };
        var sweep = Sweeper(host);
        sweep(1);
        Assert.Single(host.Lines);

        // negative: still watched and still stalled, the raised alarm is kept
        sweep(2);
        Assert.NotNull(host.Alarm("alarm/tm/owner-stalled"));

        // ah2-180 Amendment 4: put on hold, the team leaves the list; its alarm is cleared and nothing is told
        host.Listed = false;
        host.HeldTeam = true;
        sweep(3);
        Assert.Null(host.Alarm("alarm/tm/owner-stalled"));
        Assert.Single(host.Lines);
    }

    [Fact]
    public void ARaisedAlarmOfATeamThatEndedIsClearedWithItsClearedLineEvenAfterARestart()
    {
        var host = new Host { Stalled = new LeadUncarried(2, "no-pane") };
        Sweeper(host)(1);
        Assert.Single(host.Lines);

        // a new daemon: nothing in memory names the team, only the record does
        host.Listed = false;
        Sweeper(host)(2);

        Assert.Null(host.Alarm("alarm/tm/owner-stalled"));
        Assert.Contains("owner-stalled cleared", host.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAnUncarriedMarkOnADownLeadIsOwnerDownsGroundNotOwnerStalled()
    {
        var host = new Host { Address = null, Stalled = new LeadUncarried(3, "no-pane") };
        var sweep = Sweeper(host);

        sweep(0);
        sweep(3);

        Assert.DoesNotContain(host.Lines, static l => l.Contains("could not wake", StringComparison.Ordinal));
        Assert.Null(host.Alarm("alarm/tm/owner-stalled"));
    }

    [Fact]
    public void AnOrchestratorDownPastItsTimeRunsTheCommandOnce()
    {
        var host = new Host { Address = null };
        var sweep = Sweeper(host);

        sweep(0);
        sweep(1);
        Assert.Empty(host.Lines);
        sweep(2);
        sweep(3);

        Assert.Contains("is down", Assert.Single(host.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeWithNoCommandNothingIsToldOrJournalledAndSettingOneLaterStillFires()
    {
        var host = new Host { Asked = T0, CommandSet = false };
        var sweep = Sweeper(host);

        sweep(20);

        // ah2-180 Amendment 2 (b): raised though no channel carried it; nothing journalled
        Assert.Empty(host.Rows);
        Assert.False(host.Alarm("alarm/tm/owner-asking")!.Delivered);

        host.CommandSet = true;
        sweep(21);
        Assert.Single(host.Lines);
    }

    [Fact]
    public void WithNoChannelARaisedOwnerStalledIsKeptWithItsLineAndClearsWhenTheLeadIsRead()
    {
        var host = new Host { CommandSet = false, Stalled = new LeadUncarried(2, "no-pane") };
        var sweep = Sweeper(host);

        sweep(1);
        sweep(2);
        var raised = host.Alarm("alarm/tm/owner-stalled")!;
        Assert.Equal("owner-stalled", raised.Kind);
        Assert.Equal("orchestrator@tm has 2 unread and ah could not wake it (no-pane). Type anything in its pane.", raised.Line);
        Assert.False(raised.Delivered);
        Assert.Equal(T0.AddMinutes(1), raised.FirstToldAt);
        Assert.Empty(host.Lines);

        host.Stalled = null;
        sweep(3);
        Assert.Null(host.Alarm("alarm/tm/owner-stalled"));
        Assert.Empty(host.Lines);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NegativeARestartDoesNotPageAgainForAnEpisodeAlreadyTold(bool asking)
    {
        var host = asking ? new Host { Asked = T0.AddMinutes(-30) } : new Host { Address = null };
        var first = Sweeper(host);
        first(0);
        first(2);
        Assert.Single(host.Lines);

        var restarted = Sweeper(host, warmUpMinutes: 2);
        restarted(1);
        restarted(3);
        restarted(6);

        Assert.Single(host.Lines);
    }

    [Fact]
    public void NegativeDuringWarmUpAnOldQuestionWaitsAndThenFires()
    {
        var host = new Host { Asked = T0.AddMinutes(-30) };
        var sweep = Sweeper(host, warmUpMinutes: 2);

        sweep(1);
        Assert.Empty(host.Lines);

        sweep(2);
        Assert.Single(host.Lines);
    }

    [Fact]
    public void AnOpenRunThatEndedAndAnotherUnderTheSameDeadSessionIsANewEpisodeWithItsOwnWait()
    {
        var host = new Host { Address = null };
        var sweep = Sweeper(host);
        sweep(0);
        sweep(2);
        Assert.Single(host.Lines);

        host.Listed = false;
        sweep(3);
        host.Listed = true;
        sweep(4);
        Assert.Contains("owner-down cleared", host.Lines[1], StringComparison.Ordinal);
        Assert.Equal(2, host.Lines.Count);

        sweep(6);
        Assert.Equal(3, host.Lines.Count);
    }

    [Fact]
    public void AnOrchestratorDownIsToldWithADigestAgainEveryHourAndClearedOnce()
    {
        var host = new Host { Address = null };
        host.Waiting["queue-stall"] = 1;
        host.Waiting["member-down"] = 2;
        var sweep = Sweeper(host);

        sweep(0);
        sweep(2);
        Assert.EndsWith("a run is open. 3 waiting: member-down×2, queue-stall×1.", Assert.Single(host.Lines), StringComparison.Ordinal);
        sweep(30);
        sweep(61);
        Assert.Single(host.Lines);
        sweep(62);
        Assert.Equal(2, host.Lines.Count);

        host.Address = "orchestrator@tm";
        sweep(63);
        sweep(64);
        Assert.Equal(["ah: team tm: owner-down cleared (1h 1m)."], host.Lines.Skip(2));
    }

    [Fact]
    public void NegativeARestartMidEpisodeWaitsForTheRepeatFromTheLastLine()
    {
        var host = new Host { Address = null };
        var first = Sweeper(host);
        first(0);
        first(2);

        // The restarted daemon first sees the team one sweep in, so its down timer has not matured when warm-up ends at 2.
        var restarted = Sweeper(host, warmUpMinutes: 2);
        restarted(1);
        restarted(2);
        restarted(3);
        restarted(61);
        Assert.Single(host.Lines);

        restarted(62);
        Assert.Equal(2, host.Lines.Count);
        Assert.DoesNotContain(host.Lines, l => l.Contains("cleared", StringComparison.Ordinal));
    }

    [Fact]
    public void AQueuedOnlyTeamIsToldOnceNeverRepeatedClearedWhenItsWorkDrainsAndNeverMemberSwept()
    {
        var host = new Host { Address = null, QueuedOnly = true };
        var sweep = Sweeper(host);

        sweep(0);
        sweep(2);
        sweep(62);
        sweep(125);
        Assert.Equal("ah: team tm: its Orchestrator is down and its team has work waiting.", Assert.Single(host.Lines));

        host.Listed = false;
        sweep(126);
        Assert.Equal("ah: team tm: owner-down cleared (2h 4m).", host.Lines[1]);
        Assert.Equal(0, host.MemberReads);
    }

    [Fact]
    public void ATeamRunsOneCommandAtATimeAndASkippedFireRunsOnceTheLastHasEnded()
    {
        var host = new Host { Asked = T0.AddMinutes(-30), Hold = true };
        var sweep = Sweeper(host);
        sweep(0);
        host.Asked = T0.AddMinutes(-20);

        sweep(1);
        Assert.Single(host.Lines);
        Assert.Equal("still running", Assert.Single(host.Rows).Str("skipped"));

        host.Held[0](0);
        sweep(2);
        Assert.Equal(2, host.Lines.Count);
        Assert.Equal(0, host.Rows[1]["exit"].AsNumber());
    }

    [Fact]
    public void AStartThatThrowsFreesTheTeamSoTheNextSweepRunsTheCommand()
    {
        var host = new Host { Asked = T0.AddMinutes(-30), Throw = true };
        var sweep = Sweeper(host);
        _ = Assert.Throws<InvalidOperationException>(() => sweep(0));

        host.Throw = false;
        sweep(1);

        Assert.Single(host.Lines);
        Assert.DoesNotContain(host.Rows, r => r.Str("skipped") == "still running");
    }

    [Fact]
    public void ASweepReadsTheTeamListOnce()
    {
        var host = new Host { Asked = T0 };

        Sweeper(host)(20);

        Assert.Equal(1, host.TeamReads);
    }

    [Fact]
    public void NegativeAWorkingOrchestratorRaisesNothing()
    {
        var host = new Host();

        Sweeper(host)(60);

        Assert.Empty(host.Lines);
    }
}
