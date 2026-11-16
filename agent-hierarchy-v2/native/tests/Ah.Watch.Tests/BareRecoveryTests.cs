using System.Globalization;
using Ah.Core.Json;
using Ah.Launch;

namespace Ah.Watch.Tests;

/// <summary>The gating ladder before a bare member is stopped and relaunched: every rung has a test that it holds and one that it stops the signal.</summary>
public sealed class BareRecoveryTests
{
    private static readonly StallTeam Team = new("alpha", "/p/.claude/ah2", "/p", "alpha-orchestrator@alpha", "orch-session");

    private sealed class Host : IBareHost
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-21T20:00:00Z", CultureInfo.InvariantCulture);

        public string Name { get; set; } = "alpha-implementor";

        public string? Role { get; set; } = "implementor";

        public BareKind Kind { get; set; } = BareKind.Bare;

        public Queue<string?> Statuses { get; } = new();

        public string? Status { get; set; } = "idle";

        public List<string> Work { get; } = [];

        public JsonValue? Control { get; set; }

        public JsonObject? Record { get; set; }

        public bool Exits { get; set; } = true;

        public bool Joins { get; set; } = true;

        public string? LaunchError { get; set; }

        public List<(int Pid, string? Start)> Signals { get; } = [];

        public List<string> Launches { get; } = [];

        public List<WatchMail> Mails { get; } = [];

        public List<JsonObject> Journal_ { get; } = [];

        public bool Owned { get; set; } = true;

        public int OwnerReads { get; private set; }

        public Action? OnOwner { get; set; }

        public bool Owner()
        {
            OwnerReads++;
            OnOwner?.Invoke();
            return Owned;
        }

        public Action? OnWait { get; set; }

        public int TermPidInRecordAtSignal { get; private set; }

        public StallTeam? Other { get; set; }

        public string? FailReadsOf { get; set; }

        public List<string> WrittenFor { get; } = [];

        public IReadOnlyList<StallTeam> Teams() => Other is null ? [Team] : [Team, Other];

        public DateTimeOffset? SpawnedAt { get; set; }

        public bool HasPid { get; set; } = true;

        public IReadOnlyList<BareRow> Rows(StallTeam team) =>
            [new BareRow(Name, Role, new BareMember("alpha", Name, "w1:p1", "/p", new AhLaunchExpectation("wrangl:implementor", Name, "/p/.claude/ah2/teams/alpha.json", null, null)), null, SpawnedAt, HasPid)];

        public BareVerdict Check(StallTeam team, BareRow row, BareSighting? earlier) => Kind switch
        {
            BareKind.Bare => new BareVerdict(BareKind.Bare, 100, "t0", VerdictSession, [], "bare"),
            BareKind.Unmatched => new BareVerdict(BareKind.Unmatched, 100, "t0", "sess-1", ["M3"], "no"),
            BareKind.Launched => new BareVerdict(BareKind.Launched, 200, "t1", null, [], "ah"),
            BareKind.Unreadable => new BareVerdict(BareKind.Unreadable, null, null, null, [], "unreadable"),
            _ => new BareVerdict(BareKind.NoClaude, null, null, null, [], "none"),
        };

        public void SetMark(StallTeam team, string member, BareMark? mark)
        {
        }

        public void Mail(WatchMail mail) => Mails.Add(mail);

        public bool JournalFails { get; set; }

        public void Journal(JsonObject row)
        {
            if (JournalFails && row["kind"].AsString() == "recover-error")
            {
                throw new IOException("the journal write was refused");
            }

            Journal_.Add(row);
        }

        public JsonValue? ReadRecord(StallTeam team, string member)
        {
            if (team.Name == FailReadsOf)
            {
                throw new StoreUnavailableException("the state store is busy");
            }

            if (Record is null || team.Name != Team.Name)
            {
                return null;
            }

            return JsonValue.FromObject(Record);
        }

        public string? FailWritesOf { get; set; }

        public void WriteRecord(StallTeam team, string member, JsonObject record)
        {
            if (team.Name == FailWritesOf)
            {
                throw new IOException("the stall state write was refused");
            }

            WrittenFor.Add(team.Name);
            if (team.Name == Team.Name)
            {
                Record = record;
            }
        }

        public Func<int, JsonValue?>? ControlAt { get; set; }

        private int _controlReads;

        public JsonValue? ReadControl(StallTeam team, string member) => ControlAt is { } at ? at(++_controlReads) : Control;

        public string? AgentStatus(StallTeam team, BareRow row) => Statuses.Count > 0 ? Statuses.Dequeue() : Status;

        public IReadOnlyList<string> OpenWork(StallTeam team, BareRow row) => Work;

        public int BgOpen { get; set; }

        public int BackgroundOpen(StallTeam team, string? sessionId) => BgOpen;

        public List<string> ReBriefs { get; } = [];

        public string? ReBriefError { get; set; }

        public string? ReBrief(StallTeam team, BareRow row)
        {
            ReBriefs.Add(row.Name);
            return ReBriefError;
        }

        public bool Terminate(int pid, string? pidStart)
        {
            Signals.Add((pid, pidStart));
            TermPidInRecordAtSignal = Record is not null && Record["term_pid"].IsNumber ? (int)Record["term_pid"].AsNumber() : 0;
            return true;
        }

        public bool WaitExit(int pid, string? pidStart, TimeSpan wait)
        {
            OnWait?.Invoke();
            if (Exits)
            {
                Kind = BareKind.NoClaude;
            }

            return Exits;
        }

        public bool RecoveryEnabled { get; set; } = true;

        public List<(string Team, string Member, string Reason)> Brakes { get; } = [];

        public string VerdictSession { get; set; } = "sess-1";

        public int BrakeThrows { get; set; }

        /// <summary>What a failing brake throws.</summary>
        public Func<Exception> BrakeError { get; set; } = () => new IOException("disk full");

        public void DisableRecovery(StallTeam team, string member, string reason)
        {
            if (BrakeThrows > 0)
            {
                BrakeThrows--;
                throw BrakeError();
            }

            Brakes.Add((team.Name, member, reason));
            RecoveryEnabled = false;
        }

        public string? MemberSession { get; set; }

        public string? LastSessionId(StallTeam team, BareRow row) => MemberSession;

        public bool Held { get; set; }

        public bool TeamHeld(StallTeam team) => Held;

        public string? PreflightError { get; set; }

        public int Preflights { get; private set; }

        public string? PreflightRelaunch(StallTeam team, BareRow row, string sessionId)
        {
            Preflights++;
            return PreflightError;
        }

        public Action? OnRelaunch { get; set; }

        public string? Relaunch(StallTeam team, BareRow row, string sessionId)
        {
            OnRelaunch?.Invoke();
            Launches.Add(sessionId);
            if (LaunchError is null)
            {
                Kind = BareKind.Launched;
            }

            return LaunchError;
        }

        public List<string> VerifiedFor { get; } = [];

        public bool Verified(StallTeam team, BareRow row, int oldPid, string sessionId)
        {
            VerifiedFor.Add(sessionId);
            return Joins;
        }
    }

    private static (Host Host, BareWatch Watch) Rig(Action<Host>? setup = null)
    {
        var host = new Host();
        setup?.Invoke(host);
        return (host, new BareWatch(host, TimeSpan.FromSeconds(30), () => host.Now, null, work =>
        {
            work();
            return Task.CompletedTask;
        }));
    }

    private static RecoverRecord Stopped(Host h, string state = RecoverRecord.Stopping, int attempt = 1, string? lastError = null) =>
        new() { State = state, SessionId = "sess-1", Pid = 100, PidStart = "t0", PaneId = "w1:p1", Attempt = attempt, DetectedAt = h.Now, LastError = lastError };

    private static void Sweeps(Host host, BareWatch watch, int count, int seconds = 30)
    {
        for (var i = 0; i < count; i++)
        {
            watch.Sweep();
            host.Now += TimeSpan.FromSeconds(seconds);
        }
    }

    private static string State(Host host) => host.Record!["state"].AsString();

    private static JsonValue Control(string json) => JsonText.Parse(json);

    [Theory]
    [InlineData(10, false, false)]
    [InlineData(120, false, true)]
    [InlineData(10, true, false)]
    [InlineData(120, true, true)]
    public void AFreshSpawnPidOrNotGetsNoBareStateOrRecoveryUntilItsPatienceIsUp(int ageSeconds, bool hasPid, bool watched)
    {
        var (host, watch) = Rig(h =>
        {
            h.SpawnedAt = h.Now - TimeSpan.FromSeconds(ageSeconds);
            h.HasPid = hasPid;
        });

        watch.Sweep();

        Assert.Equal(watched, host.Record is not null);
        Assert.Equal(watched, host.Journal_.Any(j => j["kind"].AsString() == "bare-state"));
    }

    [Fact]
    public void AnUnreadableRecordWritesNothingAndSignalsNothingAndTheNextTeamIsStillSwept()
    {
        var (host, watch) = Rig(h =>
        {
            h.Record = (Stopped(h) with { TermPid = 100, TermPidStart = "t0" }).ToJson();
            h.FailReadsOf = "alpha";
            h.Other = new StallTeam("beta", "/q/.claude/ah2", "/q", "beta-orchestrator@beta", "orch-b");
        });
        var stored = JsonText.Stringify(JsonValue.FromObject(host.Record!), 2);

        Sweeps(host, watch, 3);

        Assert.Equal(stored, JsonText.Stringify(JsonValue.FromObject(host.Record!), 2));
        Assert.DoesNotContain("alpha", host.WrittenFor);
        Assert.Empty(host.Signals);
        Assert.Contains(host.Journal_, j => j["kind"].AsString() == "recover-error" && j["team"].AsString() == "alpha");
        Assert.Contains("beta", host.WrittenFor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARefusedRecordWriteSkipsThatMemberAndTheNextIsStillWrittenEvenWhenTheJournalRefusesToo(bool journalFails)
    {
        var (host, watch) = Rig(h =>
        {
            h.FailWritesOf = "alpha";
            h.JournalFails = journalFails;
            h.Other = new StallTeam("beta", "/q/.claude/ah2", "/q", "beta-orchestrator@beta", "orch-b");
        });

        Sweeps(host, watch, 3);

        Assert.DoesNotContain("alpha", host.WrittenFor);
        Assert.Contains("beta", host.WrittenFor);
        if (!journalFails)
        {
            Assert.Contains(host.Journal_, j => j["kind"].AsString() == "recover-error" && j["team"].AsString() == "alpha" && j["error_type"].AsString() == "System.IO.IOException");
        }
    }

    [Fact]
    public async Task SweepsRunAPeriodAfterTheLastStartAndOneThatOverranIsFollowedAtTheNextTick()
    {
        var (host, watch) = Rig(h => h.Owned = false);
        var jobs = new FakeJobs();
        watch.Register(jobs);
        var (_, tick, run) = Assert.Single(jobs.Jobs);
        Assert.Equal(TimeSpan.FromSeconds(15), tick);
        var start = host.Now;
        var at = new List<double>();
        host.OnOwner = () => at.Add((host.Now - start).TotalSeconds);

        // the sweeps at 0 and 30 take 1 s, the one at 60 takes 40 s; the ticks fall every 15 s
        host.OnOwner += () => host.Now += TimeSpan.FromSeconds(at[^1] == 60 ? 40 : 1);
        for (var t = 0; t <= 120; t += 15)
        {
            if (host.Now - start < TimeSpan.FromSeconds(t))
            {
                host.Now = start + TimeSpan.FromSeconds(t);
            }

            if (host.Now - start == TimeSpan.FromSeconds(t))
            {
                await run(CancellationToken.None);
            }
        }

        // not at 15 or 45 (less than a period since the start); 105 follows the 40 s sweep at once, 120 is too soon after it
        Assert.Equal([0, 30, 60, 105], at);
    }

    [Fact]
    public void AMemberJustRelaunchedIsCheckedAtTheNextSweepThoughItsSpawnIsFresh()
    {
        var (host, watch) = Rig(h =>
        {
            h.Kind = BareKind.Launched;
            h.SpawnedAt = h.Now - TimeSpan.FromSeconds(10);
            h.Record = (Stopped(h, RecoverRecord.Launching) with { LaunchedAt = h.Now - TimeSpan.FromSeconds(10) }).ToJson();
        });

        watch.Sweep();

        Assert.Equal("done", State(host));
    }

    [Fact]
    public void AMatchedIdleMemberIsStoppedOnceAfterTwoSweepsAnd60SecondsThenRelaunchedWithItsSessionAndVerified()
    {
        var (host, watch) = Rig();

        Sweeps(host, watch, 2);
        Assert.Empty(host.Signals);
        Assert.Equal("waiting-idle", State(host));

        watch.Sweep();

        Assert.Equal([(100, "t0")], host.Signals);
        Assert.Equal(["sess-1"], host.Launches);
        Assert.Equal("launching", State(host));

        host.Now += TimeSpan.FromSeconds(30);
        watch.Sweep();

        Assert.Equal("done", State(host));
        Assert.DoesNotContain(host.Mails, m => m.Event == "member-relaunched");
        var mail = Assert.Single(host.Mails, m => m.Event == "team-readopt");
        Assert.Contains("re-adopted 1 (alpha-implementor --resume sess", mail.Text, StringComparison.Ordinal);
        Assert.Contains("bare members found (alpha-implementor at 20:00Z) — re-adopted", mail.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("restored after restart", mail.Text, StringComparison.Ordinal);
        Assert.Single(host.Signals);
    }

    [Theory]
    [InlineData("no work tree recorded for alpha-implementor; resume with --allow-main")]
    [InlineData("pane state unknown: herdr did not answer")]
    public void AMemberWhoseRelaunchWouldBeRefusedIsLeftRunningAskedOnceAndTheBrakeStaysOff(string refusal)
    {
        var (host, watch) = Rig(h => h.PreflightError = refusal);

        Sweeps(host, watch, 100);

        Assert.Empty(host.Signals);
        Assert.Empty(host.Launches);
        Assert.Empty(host.Brakes);
        Assert.True(host.RecoveryEnabled);
        Assert.Equal("asked", State(host));
        Assert.Equal($"relaunch would be refused: {refusal}; left running", host.Record!["asked_reason"].AsString());
        Assert.Equal(1, host.Preflights);
        Assert.Single(host.Mails, m => m.Event == "member-bare-asked");
    }

    [Fact]
    public void ARelaunchRefusedAfterThePreflightPassedStillTripsTheBrakeBecauseTheMemberIsNowDead()
    {
        var (host, watch) = Rig(h => h.LaunchError = RecoveryState.RefusalPrefix + "no work tree recorded");

        Sweeps(host, watch, 3);

        Assert.Equal(1, host.Preflights);
        Assert.Single(host.Signals);
        Assert.Equal("asked", State(host));
        Assert.Single(host.Brakes);
        Assert.False(host.RecoveryEnabled);
    }

    [Fact]
    public void WithRecoveryOffAHeldTeamOrAnAhLaunchedMemberNoPreflightRunsAndNothingIsSignalled()
    {
        foreach (var setup in new Action<Host>[] { h => h.RecoveryEnabled = false, h => h.Held = true, h => h.Kind = BareKind.Launched })
        {
            var (host, watch) = Rig(h =>
            {
                h.PreflightError = "would be refused";
                setup(h);
            });

            Sweeps(host, watch, 10);

            Assert.Equal(0, host.Preflights);
            Assert.Empty(host.Signals);
        }
    }

    [Fact]
    public void AMemberStillWaitingForItsIdleRungsIsNotPreflighted()
    {
        var (host, watch) = Rig(h => h.PreflightError = "would be refused");

        Sweeps(host, watch, 2);

        Assert.Equal(0, host.Preflights);
        Assert.Equal("waiting-idle", State(host));
    }

    [Fact]
    public void AFailedRecordOfAnEarlierSessionIsMarkedReportedSilentlyAndNeverCountedOrNamed()
    {
        var (host, watch) = Rig(h =>
        {
            h.Kind = BareKind.Launched;
            h.MemberSession = "sess-2";
            h.Record = (Stopped(h, RecoverRecord.Failed, 2, "no ah-launched process was recorded within 60 s") with { Reported = false, FinishedAt = h.Now }).ToJson();
        });

        watch.Sweep();

        Assert.DoesNotContain(host.Mails, m => m.Event == "team-readopt");
        Assert.True(host.Record!["reported"].IsTruthy);
        Assert.Equal("failed", State(host));
        Assert.Equal("superseded by session sess-2", host.Record["note"].AsString());
    }

    [Fact]
    public void AFailedRecordOfTheCurrentSessionIsCountedAndNamedOnce()
    {
        var (host, watch) = Rig(h =>
        {
            h.Kind = BareKind.Launched;
            h.MemberSession = "sess-1";
            h.Record = (Stopped(h, RecoverRecord.Failed, 2, "no ah-launched process was recorded within 60 s") with { Reported = false, FinishedAt = h.Now }).ToJson();
        });

        Sweeps(host, watch, 3);

        var mail = Assert.Single(host.Mails, m => m.Event == "team-readopt");
        Assert.Contains("failed 1", mail.Text, StringComparison.Ordinal);
        Assert.Contains("alpha-implementor could not be relaunched", mail.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ALiveAhLaunchedMemberIsNeverSignalledRelaunchedOrReported()
    {
        var (host, watch) = Rig(h => h.Kind = BareKind.Launched);

        Sweeps(host, watch, 5);

        Assert.Empty(host.Signals);
        Assert.Empty(host.Launches);
        Assert.Empty(host.Mails);
        Assert.Null(host.Record);
    }

    [Fact]
    public void WithRecoverySwitchedOffAMatchedIdleMemberIsNeverSignalledOrRelaunchedEvenWhenAskedToNowButIsStillRecorded()
    {
        var (host, watch) = Rig(h =>
        {
            h.RecoveryEnabled = false;
            h.Control = Control("{\"hold\":false,\"now_at\":\"2026-10-21T19:59:00Z\"}");
        });

        Sweeps(host, watch, 20);

        Assert.Empty(host.Signals);
        Assert.Empty(host.Launches);
        Assert.Equal("detected", State(host));
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("done")]
    public void AMemberAtAnEmptyPromptIsStoppedAndRelaunchedOnTheSameScheduleWhetherHerdrSaysIdleOrDone(string status)
    {
        var (host, watch) = Rig(h => h.Status = status);

        Sweeps(host, watch, 2);
        Assert.Empty(host.Signals);
        Assert.Equal("waiting-idle", State(host));

        watch.Sweep();

        Assert.Equal([(100, "t0")], host.Signals);
        Assert.Equal(["sess-1"], host.Launches);
        Assert.DoesNotContain(host.Mails, m => m.Event == "member-bare-busy");
    }

    [Fact]
    public void ADoneMemberIsNeverToldBusyHoweverLongItWaits()
    {
        var (host, watch) = Rig(h =>
        {
            h.Status = "done";
            h.RecoveryEnabled = false;
        });

        Sweeps(host, watch, 400);

        Assert.DoesNotContain(host.Mails, m => m.Event == "member-bare-busy");
        Assert.Empty(host.Signals);
    }

    [Theory]
    [InlineData("working")]
    [InlineData("blocked")]
    public void AMemberHerdrSaysIsWorkingOrBlockedIsNeverSignalledAndIsToldBusyOnce(string status)
    {
        var (host, watch) = Rig(h => h.Status = status);

        Sweeps(host, watch, 400);

        Assert.Empty(host.Signals);
        Assert.Empty(host.Launches);
        Assert.Single(host.Mails, m => m.Event == "member-bare-busy");
    }

    [Fact]
    public void ABusyMemberIsToldOnceInHundredsOfSweepsAndNotAgainByAFreshWatchOverTheSameRecord()
    {
        var (host, watch) = Rig(h => h.Status = "working");

        Sweeps(host, watch, 100);
        Assert.Single(host.Mails, m => m.Event == "member-bare-busy");

        var reloaded = new BareWatch(host, TimeSpan.FromSeconds(30), () => host.Now, null, work =>
        {
            work();
            return Task.CompletedTask;
        });
        Sweeps(host, reloaded, 100);

        Assert.Single(host.Mails, m => m.Event == "member-bare-busy");
        Assert.True(host.Record!["busy_told"].IsTruthy);
    }

    [Fact]
    public void AHeldBusyMemberGetsNoBusyMailAndNothingBeyondItsDetectionRecord()
    {
        var (host, watch) = Rig(h =>
        {
            h.Status = "working";
            h.Control = Control("{\"hold\":true,\"now_at\":null}");
        });

        Sweeps(host, watch, 100);

        Assert.DoesNotContain(host.Mails, m => m.Event == "member-bare-busy");
        Assert.Empty(host.Signals);
        Assert.Equal("detected", State(host));
    }

    [Fact]
    public void SwitchingRecoveryOnLetsTheSameIdleMemberBeStopped()
    {
        var (host, watch) = Rig(h => h.RecoveryEnabled = false);
        Sweeps(host, watch, 5);
        Assert.Empty(host.Signals);

        host.RecoveryEnabled = true;
        Sweeps(host, watch, 5);

        Assert.Single(host.Signals);
    }

    [Fact]
    public void AHoldThatLandsAfterTheSweepReadTheControlButBeforeTheSignalSendsNothing()
    {
        var (host, watch) = Rig();
        host.ControlAt = _ => Control(State(host) == "stopping" ? "{\"hold\":true,\"now_at\":null}" : "{\"hold\":false,\"now_at\":null}");

        Sweeps(host, watch, 20);

        Assert.Empty(host.Signals);
        Assert.Empty(host.Launches);
        Assert.NotEqual("stopping", State(host));
    }

    [Fact]
    public void NegativeWithNoHoldTheSameMemberIsSignalledOnce()
    {
        var (host, watch) = Rig(h => h.ControlAt = _ => Control("{\"hold\":false,\"now_at\":null}"));

        Sweeps(host, watch, 20);

        Assert.Single(host.Signals);
    }

    [Fact]
    public void ABusyMemberThatIsNotHeldIsStillToldOnce()
    {
        var (host, watch) = Rig(h =>
        {
            h.Status = "working";
            h.Control = Control("{\"hold\":false,\"now_at\":null}");
        });

        Sweeps(host, watch, 100);

        Assert.Single(host.Mails, m => m.Event == "member-bare-busy");
    }

    [Fact]
    public void ABusyMemberIsNeverSignalledHoweverLongItStaysBusyAndTheOrchestratorIsToldOnce()
    {
        var (host, watch) = Rig(h => h.Status = "working");

        Sweeps(host, watch, 400);

        Assert.Empty(host.Signals);
        Assert.Empty(host.Launches);
        Assert.Equal("waiting-idle", State(host));
        Assert.Single(host.Mails, m => m.Event == "member-bare-busy");
    }

    [Fact]
    public void AMemberThatTurnsBusyBetweenTheCheckAndTheSignalIsNotSignalled()
    {
        var (host, watch) = Rig();
        Sweeps(host, watch, 2);
        host.Statuses.Enqueue("idle");
        host.Statuses.Enqueue("working");

        watch.Sweep();

        Assert.Empty(host.Signals);
        Assert.Equal("waiting-idle", State(host));
    }

    [Fact]
    public void AMemberWhoseProcessChangedBetweenTheCheckAndTheSignalIsNotSignalled()
    {
        var (host, watch) = Rig();
        Sweeps(host, watch, 2);
        host.Kind = BareKind.Unmatched;

        watch.Sweep();

        Assert.Empty(host.Signals);
    }

    [Fact]
    public void AnUnmatchedClaudeIsNeverSignalledEvenWhenIdleForever()
    {
        var (host, watch) = Rig(h => h.Kind = BareKind.Unmatched);

        Sweeps(host, watch, 20);

        Assert.Empty(host.Signals);
        Assert.Null(host.Record);
    }

    /// <summary>A rig whose member was seen while its team was held, with the hold now lifted.</summary>
    private static (Host Host, BareWatch Watch) HeldThenLifted(Action<Host>? setup = null)
    {
        var (host, watch) = Rig(h =>
        {
            h.Held = true;
            setup?.Invoke(h);
        });
        Sweeps(host, watch, 1);
        host.Held = false;
        return (host, watch);
    }

    [Fact]
    public void AMemberThatWasHeldIsRelaunchedDespiteAnOpenRequestAndReBriefedOnceAlone()
    {
        var (host, watch) = HeldThenLifted(h => h.Work.Add("request impl (r1) from orchestrator"));

        Sweeps(host, watch, 4);
        Assert.Equal(["sess-1"], host.Launches);
        Assert.Equal("done", State(host));
        Sweeps(host, watch, 10);

        Assert.Single(host.Signals);
        Assert.Equal(["alpha-implementor"], host.ReBriefs);
    }

    [Fact]
    public void NegativeAMemberThatWasHeldWithBackgroundWorkOpenIsAskedAndNotSignalled()
    {
        var (host, watch) = HeldThenLifted(h => h.BgOpen = 1);

        Sweeps(host, watch, 10);

        Assert.Empty(host.Signals);
        Assert.Equal("asked", State(host));
        Assert.Empty(host.ReBriefs);
    }

    [Fact]
    public void NegativeAMemberNeverHeldIsNotReBriefedAfterItsRelaunch()
    {
        var (host, watch) = Rig();

        Sweeps(host, watch, 6);

        Assert.Equal("done", State(host));
        Assert.Empty(host.ReBriefs);
    }

    [Fact]
    public void AFailedReBriefRaisesTheFailureEventAndTheMemberStaysUp()
    {
        var (host, watch) = HeldThenLifted(h => h.ReBriefError = "mail_error: refused");

        Sweeps(host, watch, 6);

        Assert.Equal("done", State(host));
        Assert.Contains("mail_error: refused", Assert.Single(host.Mails, m => m.Event == "member-relaunch-failed").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenWorkStopsTheSignalAndAsksTheOrchestratorOnce()
    {
        var (host, watch) = Rig(h => h.Work.Add("request impl (r1) from orchestrator"));

        Sweeps(host, watch, 10);

        Assert.Empty(host.Signals);
        Assert.Equal("asked", State(host));
        var ask = Assert.Single(host.Mails, m => m.Event == "member-bare-asked");
        Assert.Contains("open work", ask.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AHoldStopsTheSignalAndReleasingItLetsTheNextSweepThrough()
    {
        var (host, watch) = Rig(h => h.Control = Control("{\"hold\":true,\"now_at\":null}"));

        Sweeps(host, watch, 6);
        Assert.Empty(host.Signals);

        host.Control = Control("{\"hold\":false,\"now_at\":null}");
        Sweeps(host, watch, 2);
        Assert.Empty(host.Signals);

        watch.Sweep();

        Assert.Single(host.Signals);
    }

    [Fact]
    public void NoMoreThanTwoAttemptsAreMade()
    {
        var (host, watch) = Rig(h => h.Record = new RecoverRecord { State = RecoverRecord.WaitingIdle, SessionId = "sess-1", Pid = 100, PidStart = "t0", PaneId = "w1:p1", Attempt = 2, DetectedAt = h.Now }.ToJson());

        Sweeps(host, watch, 6);

        Assert.Empty(host.Signals);
    }

    [Fact]
    public void TheOrchestratorsOwnSessionIsNeverTouchedNorRecorded()
    {
        var (host, watch) = Rig(h =>
        {
            h.Role = "orchestrator";
            h.Name = "alpha-orchestrator";
        });
        Sweeps(host, watch, 10);
        Assert.Empty(host.Signals);
        Assert.Null(host.Record);

        var (other, otherWatch) = Rig(h =>
        {
            h.Role = "implementor";
            h.Name = "alpha-orchestrator";
        });
        Sweeps(other, otherWatch, 10);
        Assert.Empty(other.Signals);
        Assert.Null(other.Record);
    }

    [Fact]
    public void ASubOrchestratorIsNeverTouchedEither()
    {
        var (host, watch) = Rig(h => h.Role = "sub-orchestrator");

        Sweeps(host, watch, 10);

        Assert.Empty(host.Signals);
    }

    [Fact]
    public void NowSkipsTheStabilityAndHoldRungsButNotBusyOrOpenWork()
    {
        var control = "{\"hold\":true,\"now_at\":\"2026-10-21T19:59:00Z\"}";
        var (idle, idleWatch) = Rig(h => h.Control = Control(control));
        idleWatch.Sweep();
        Assert.Single(idle.Signals);

        var (busy, busyWatch) = Rig(h =>
        {
            h.Control = Control(control);
            h.Status = "working";
        });
        Sweeps(busy, busyWatch, 5);
        Assert.Empty(busy.Signals);

        var (working, workingWatch) = Rig(h =>
        {
            h.Control = Control(control);
            h.Work.Add("request impl (r1) from orchestrator");
        });
        Sweeps(working, workingWatch, 5);
        Assert.Empty(working.Signals);
    }

    [Fact]
    public void NowNeverSkipsTheAttemptsCapOrAProcessThatStayedAfterItsTerm()
    {
        var now = "{\"hold\":false,\"now_at\":\"2026-10-21T19:59:00Z\"}";
        var (capped, cappedWatch) = Rig(h =>
        {
            h.Control = Control(now);
            h.Record = new RecoverRecord { State = RecoverRecord.WaitingIdle, SessionId = "sess-1", Pid = 100, PidStart = "t0", PaneId = "w1:p1", Attempt = 2, DetectedAt = h.Now }.ToJson();
        });
        Sweeps(capped, cappedWatch, 5);
        Assert.Empty(capped.Signals);

        var (stuck, stuckWatch) = Rig(h =>
        {
            h.Control = Control(now);
            h.Record = (Stopped(h, RecoverRecord.Asked, 1, BareWatch.TermStuck) with { AskedReason = BareWatch.TermStuck }).ToJson();
        });
        Sweeps(stuck, stuckWatch, 5);
        Assert.Empty(stuck.Signals);

        var (notSent, notSentWatch) = Rig(h =>
        {
            h.Control = Control(now);
            h.Record = (Stopped(h, RecoverRecord.Asked, 1, "term not sent") with { AskedReason = "term not sent" }).ToJson();
        });
        notSentWatch.Sweep();
        Assert.Single(notSent.Signals);
    }

    [Fact]
    public void ARecordClearedAndRevivedForTheSameProcessKeepsItsAttemptsAndItsStuckTerm()
    {
        var (host, watch) = Rig(h => h.Record = (Stopped(h, RecoverRecord.Cleared, 1, BareWatch.TermStuck)).ToJson());

        Sweeps(host, watch, 8);

        Assert.Empty(host.Signals);
        Assert.Equal(1, (int)host.Record!["attempt"].AsNumber());
    }

    [Fact]
    public void AStoppingRecordWhoseNeededPaneNowHoldsAnAhLaunchedProcessLaunchesNothing()
    {
        var (host, watch) = Rig(h =>
        {
            h.Kind = BareKind.Launched;
            h.Joins = false;
            h.Record = Stopped(h).ToJson();
        });

        Sweeps(host, watch, 2);

        Assert.Empty(host.Launches);
        Assert.Empty(host.Signals);
        Assert.Equal("launching", State(host));
        Assert.Contains("sess-1", host.VerifiedFor);
    }

    [Fact]
    public void AStoppingRecordWithADifferentBareClaudeInThePaneStartsOverAndLaunchesNothing()
    {
        var (host, watch) = Rig(h =>
        {
            h.Kind = BareKind.Unmatched;
            h.Record = Stopped(h).ToJson();
        });

        watch.Sweep();

        Assert.Empty(host.Launches);
        Assert.Empty(host.Signals);
        Assert.Equal("cleared", State(host));
    }

    [Fact]
    public void AStoppingRecordWithNoClaudeLeftIsLaunchedOnce()
    {
        var (host, watch) = Rig(h =>
        {
            h.Kind = BareKind.NoClaude;
            h.Record = Stopped(h).ToJson();
        });

        watch.Sweep();

        Assert.Equal(["sess-1"], host.Launches);
    }

    [Fact]
    public void TheStopAndTheLaunchRunApartFromTheSweepAndOnlyOneRunsPerMember()
    {
        var host = new Host();
        var queued = new List<(Action Work, TaskCompletionSource Done)>();
        var watch = new BareWatch(host, TimeSpan.FromSeconds(30), () => host.Now, null, work =>
        {
            var done = new TaskCompletionSource();
            queued.Add((work, done));
            return done.Task;
        });
        Sweeps(host, watch, 2);

        watch.Sweep();

        Assert.Equal("stopping", State(host));
        Assert.Empty(host.Signals);
        var started = Assert.Single(queued);

        host.Now += TimeSpan.FromSeconds(30);
        watch.Sweep();
        watch.Sweep();
        Assert.Single(queued);
        Assert.Empty(host.Signals);

        started.Work();
        started.Done.SetResult();

        Assert.Single(host.Signals);
        Assert.Single(host.Launches);
    }

    [Fact]
    public void ARecoveryThatThrowsLeavesTheMemberAskedAndTheSweepRunning()
    {
        var (host, watch) = Rig();
        Sweeps(host, watch, 2);
        host.OnRelaunch = () => throw new InvalidOperationException("herdr is gone");

        watch.Sweep();

        Assert.Equal("asked", State(host));
        Assert.Contains("herdr is gone", host.Record!["asked_reason"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ALaunchingRecordWhoseLaunchBeganSecondsAgoWaitsForThePaneAndOneThatBeganLongAgoRetriesOnce()
    {
        var (recent, recentWatch) = Rig(h =>
        {
            h.Kind = BareKind.NoClaude;
            h.Record = (Stopped(h, RecoverRecord.Launching) with { LaunchStartedAt = h.Now - TimeSpan.FromSeconds(10) }).ToJson();
        });
        recentWatch.Sweep();
        Assert.Empty(recent.Launches);

        var (old, oldWatch) = Rig(h =>
        {
            h.Kind = BareKind.NoClaude;
            h.Record = (Stopped(h, RecoverRecord.Launching) with { LaunchStartedAt = h.Now - TimeSpan.FromSeconds(100) }).ToJson();
        });
        oldWatch.Sweep();
        Assert.Single(old.Launches);
        Assert.Equal(2, (int)old.Record!["attempt"].AsNumber());
    }

    [Fact]
    public void ALaunchThatLeftNoProcessAfterTheAttemptsCapFailsInsteadOfLaunchingAgain()
    {
        var (host, watch) = Rig(h =>
        {
            h.Kind = BareKind.NoClaude;
            h.Record = (Stopped(h, RecoverRecord.Launching, attempt: 2) with { LaunchStartedAt = h.Now - TimeSpan.FromSeconds(100) }).ToJson();
        });

        watch.Sweep();

        Assert.Empty(host.Launches);
        Assert.Equal("failed", State(host));
    }

    [Fact]
    public void ALaunchWhosePaneHoldsAnotherProcessFailsWithItsErrorAfterThePatienceAndNotBefore()
    {
        var (recent, recentWatch) = Rig(h =>
        {
            h.Kind = BareKind.Unmatched;
            h.Record = (Stopped(h, RecoverRecord.Launching) with { LaunchStartedAt = h.Now - TimeSpan.FromSeconds(10) }).ToJson();
        });
        recentWatch.Sweep();
        Assert.Equal("launching", State(recent));
        Assert.True(recent.Record!["last_error"].IsNull);

        var (old, oldWatch) = Rig(h =>
        {
            h.Kind = BareKind.Unmatched;
            h.Record = (Stopped(h, RecoverRecord.Launching) with { LaunchStartedAt = h.Now - TimeSpan.FromSeconds(100) }).ToJson();
        });
        oldWatch.Sweep();
        Assert.Empty(old.Launches);
        Assert.Equal("failed", State(old));
        Assert.Contains("could not be confirmed", old.Record!["last_error"].AsString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RecoverRecord.Stopping, "the stop could not be confirmed")]
    [InlineData(RecoverRecord.Launching, "the launch could not be confirmed")]
    public void ARecordWhosePaneStaysUnreadableFailsWithItsErrorAfterThePatienceAndNotBefore(string state, string error)
    {
        var (host, watch) = Rig(h =>
        {
            h.Kind = BareKind.Unreadable;
            h.Record = (Stopped(h, state) with { DetectedAt = h.Now - TimeSpan.FromHours(1), LaunchStartedAt = h.Now - TimeSpan.FromHours(1) }).ToJson();
        });

        watch.Sweep();
        Assert.Equal(state, State(host)); // an old record still gets the whole patience, counted from the first unreadable sweep
        host.Now += TimeSpan.FromSeconds(89);
        watch.Sweep();
        Assert.Equal(state, State(host));

        host.Now += TimeSpan.FromSeconds(1);
        watch.Sweep();
        Assert.Empty(host.Launches);
        Assert.Equal("failed", State(host));
        Assert.Contains(error, host.Record!["last_error"].AsString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RecoverRecord.Stopping)]
    [InlineData(RecoverRecord.Launching)]
    public void NegativeAPaneReadInBetweenStartsThePatienceAgain(string state)
    {
        var (host, watch) = Rig(h =>
        {
            h.Kind = BareKind.Unreadable;
            h.Record = Stopped(h, state).ToJson();
        });

        watch.Sweep();
        host.Kind = BareKind.Unmatched;
        host.Now += TimeSpan.FromSeconds(60);
        watch.Sweep();

        host.Kind = BareKind.Unreadable;
        host.Record = Stopped(host, state).ToJson();
        host.Now += TimeSpan.FromSeconds(60);
        watch.Sweep();

        Assert.Equal(state, State(host));
    }

    [Fact]
    public void AnUnreadablePaneLeavesARecordThatIsNotLaunchingOrStoppingAlone()
    {
        var (host, watch) = Rig(h =>
        {
            h.Kind = BareKind.Unreadable;
            h.Record = (Stopped(h, RecoverRecord.WaitingIdle) with { DetectedAt = h.Now - TimeSpan.FromSeconds(100) }).ToJson();
        });

        watch.Sweep();

        Assert.Equal("waiting-idle", State(host));
    }

    [Fact]
    public void TheLaunchBeginIsRecordedBeforeTheRelaunchRuns()
    {
        var (host, watch) = Rig();
        Sweeps(host, watch, 2);
        string? seen = null;
        host.OnRelaunch = () => seen = host.Record!["launch_started_at"].IsNull ? null : host.Record["launch_started_at"].AsString();

        watch.Sweep();

        Assert.NotNull(seen);
    }

    [Fact]
    public void TheProcessIsNamedInTheRecordBeforeTheSignalIsSent()
    {
        var (host, watch) = Rig();

        Sweeps(host, watch, 3);

        Assert.Equal(100, host.TermPidInRecordAtSignal);
        Assert.Equal(100, (int)host.Record!["term_pid"].AsNumber());
    }

    [Fact]
    public void ATaskThatThrowsAfterTheTermNeverLetsNowSignalTheSameProcessAgain()
    {
        var (host, watch) = Rig();
        Sweeps(host, watch, 2);
        host.OnWait = () => throw new InvalidOperationException("boom");

        watch.Sweep();

        Assert.Single(host.Signals);
        Assert.Equal("asked", State(host));
        host.OnWait = null;
        host.Control = Control("{\"hold\":false,\"now_at\":\"2026-10-21T23:59:00Z\"}");

        Sweeps(host, watch, 5);

        Assert.Single(host.Signals);
        Assert.Empty(host.Launches);
    }

    [Fact]
    public void ADaemonThatLostTheFenceDuringTheWaitLaunchesNothingAndLeavesTheRecordStopping()
    {
        var (host, watch) = Rig();
        Sweeps(host, watch, 2);
        host.OnWait = () => host.Owned = false;

        watch.Sweep();

        Assert.Single(host.Signals);
        Assert.Empty(host.Launches);
        Assert.Equal("stopping", State(host));
    }

    [Fact]
    public void TheLaunchTimeIsStampedWhenTheLaunchReturnsNotWhenItStarted()
    {
        var (host, watch) = Rig();
        Sweeps(host, watch, 2);
        host.OnRelaunch = () => host.Now += TimeSpan.FromSeconds(45);

        watch.Sweep();

        Assert.Equal(host.Now, DateTimeOffset.Parse(host.Record!["launched_at"].AsString(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TheMailsNameTheTeamOnTheHoldAndNowCommandLines()
    {
        var (busy, busyWatch) = Rig(h => h.Status = "working");
        Sweeps(busy, busyWatch, 80);
        Assert.Contains("ah roster recover hold alpha-implementor --team alpha", Assert.Single(busy.Mails, m => m.Event == "member-bare-busy").Text, StringComparison.Ordinal);

        var (asked, askedWatch) = Rig(h => h.Work.Add("request impl (r1) from orchestrator"));
        Sweeps(asked, askedWatch, 10);
        var text = Assert.Single(asked.Mails, m => m.Event == "member-bare-asked").Text;
        Assert.Contains("ah roster recover now --team alpha alpha-implementor", text, StringComparison.Ordinal);
        Assert.DoesNotContain("--relaunch", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAskedMailOfABareMemberNeverOffersARelaunchTheCliRefuses()
    {
        var (host, watch) = Rig(h => h.Work.Add("request impl (r1) from orchestrator"));

        Sweeps(host, watch, 10);

        var text = Assert.Single(host.Mails, m => m.Event == "member-bare-asked").Text;
        Assert.DoesNotContain("--relaunch", text, StringComparison.Ordinal);
        Assert.DoesNotContain("resume --checkpoint", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AProcessThatDoesNotExitOnTermIsNotSignalledAgainAndNothingIsLaunched()
    {
        var (host, watch) = Rig(h => h.Exits = false);

        Sweeps(host, watch, 12);

        Assert.Single(host.Signals);
        Assert.Empty(host.Launches);
        Assert.Equal("asked", State(host));
        Assert.Single(host.Mails, m => m.Event == "member-bare-asked");
    }

    [Fact]
    public void ALaunchThatIsNeverVerifiedIsNotLaunchedOverTheLiveProcessItMadeAndIsTold()
    {
        var (host, watch) = Rig(h => h.Joins = false);

        Sweeps(host, watch, 40);

        Assert.Single(host.Signals);
        Assert.Single(host.Launches);
        Assert.Equal("failed", State(host));
        Assert.Single(host.Brakes);
        Assert.False(host.RecoveryEnabled);
        Assert.Single(host.Mails, m => m.Event == "member-relaunch-failed");
        Assert.DoesNotContain(host.Mails, m => m.Event == "team-readopt");
        Assert.DoesNotContain(host.Mails, m => m.Event == "member-relaunched");
    }

    [Fact]
    public void ALaunchThatErrorsIsRetriedOnceThenGivenUp()
    {
        var (host, watch) = Rig(h => h.LaunchError = "pane gone");

        Sweeps(host, watch, 40);

        Assert.Equal(2, host.Launches.Count);
        Assert.Equal("failed", State(host));
        Assert.Single(host.Brakes);
        Assert.False(host.RecoveryEnabled);
        Assert.Contains("pane gone", Assert.Single(host.Mails, m => m.Event == "member-relaunch-failed").Text, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Mails, m => m.Event == "team-readopt");
    }

    [Fact]
    public void AMemberThatLeftBeforeTheSignalClearsItsRecordAndIsNotSignalled()
    {
        var (host, watch) = Rig();
        Sweeps(host, watch, 1);
        host.Kind = BareKind.NoClaude;

        Sweeps(host, watch, 1);

        Assert.Empty(host.Signals);
        Assert.Equal("cleared", State(host));
    }

    [Fact]
    public void ARefusedRelaunchEndsInAskedWithoutRetryAndTheDaemonSwitchesRecoveryOffAndTellsTheOrchestratorOnce()
    {
        var refusal = RecoveryState.RefusalPrefix + "Herdr refused to start alpha-implementor";
        var (host, watch) = Rig(h => h.LaunchError = refusal);

        Sweeps(host, watch, 12);

        Assert.Equal(["sess-1"], host.Launches);
        Assert.Equal(1, (int)host.Record!["attempt"].AsNumber());
        var brake = Assert.Single(host.Brakes);
        Assert.Equal(("alpha", "alpha-implementor"), (brake.Team, brake.Member));
        Assert.Equal(refusal, brake.Reason);
        Assert.False(host.RecoveryEnabled);
        var mail = Assert.Single(host.Mails, m => m.Event == "member-relaunch-failed");
        Assert.Contains("only the user can turn it back on", mail.Text, StringComparison.Ordinal);
        Assert.Contains("--relaunch alpha-implementor --yes", mail.Text, StringComparison.Ordinal);
        Assert.Single(host.Signals);
        Assert.DoesNotContain(host.Mails, m => m.Event == "member-bare-asked");
    }

    [Fact]
    public void AnAskedRecordKeepsItsReasonAndErrorWhenTheRelaunchWasRefused()
    {
        var refusal = RecoveryState.RefusalPrefix + "name in use";
        var (host, watch) = Rig(h => h.LaunchError = refusal);

        Sweeps(host, watch, 3);

        Assert.Equal("asked", State(host));
        Assert.Equal(refusal, host.Record!["asked_reason"].AsString());
        Assert.Equal(refusal, host.Record["last_error"].AsString());
    }

    [Fact]
    public void APlainLaunchErrorIsRetriedOnceThenFailsAndTheDaemonSwitchesRecoveryOffWithOneMail()
    {
        var (host, watch) = Rig(h => h.LaunchError = "boom");

        Sweeps(host, watch, 12);

        Assert.Equal(2, host.Launches.Count);
        Assert.Equal("failed", State(host));
        var brake = Assert.Single(host.Brakes);
        Assert.Contains("boom", brake.Reason, StringComparison.Ordinal);
        Assert.False(host.RecoveryEnabled);
        Assert.Single(host.Mails, m => m.Event == "member-relaunch-failed");
        Assert.Single(host.Signals);
    }

    [Fact]
    public void ALaunchThatSucceedsDoesNotTripTheBrake()
    {
        var (host, watch) = Rig();

        Sweeps(host, watch, 8);

        Assert.Equal("done", State(host));
        Assert.Empty(host.Brakes);
        Assert.True(host.RecoveryEnabled);
    }

    [Fact]
    public void ATermThatDoesNotExitAsksWithoutTrippingTheBrake()
    {
        var (host, watch) = Rig(h => h.Exits = false);

        Sweeps(host, watch, 6);

        Assert.Equal("asked", State(host));
        Assert.Empty(host.Brakes);
        Assert.True(host.RecoveryEnabled);
    }

    [Fact]
    public void ALaunchingRecordIsNotLaunchedWhileRecoveryIsOffAndIsAskedForAHandRelaunch()
    {
        var (host, watch) = Rig(h =>
        {
            h.Kind = BareKind.NoClaude;
            h.RecoveryEnabled = false;
            h.Record = Stopped(h, RecoverRecord.Launching).ToJson();
        });

        watch.Sweep();

        Assert.Empty(host.Launches);
        Assert.Empty(host.Signals);
        Assert.Equal("asked", State(host));
        Assert.StartsWith("recovery is switched off; relaunch by hand: ", host.Record!["asked_reason"].AsString(), StringComparison.Ordinal);
        Assert.Empty(host.Brakes);
    }

    [Fact]
    public void ALaunchingRecordIsStillLaunchedWhileRecoveryIsOn()
    {
        var (host, watch) = Rig(h =>
        {
            h.Kind = BareKind.NoClaude;
            h.Record = Stopped(h, RecoverRecord.Launching).ToJson();
        });

        watch.Sweep();

        Assert.Equal(["sess-1"], host.Launches);
    }

    [Fact]
    public void APendingRecordOfAnotherSessionIsClearedAndNothingIsSignalledOrLaunched()
    {
        var (host, watch) = Rig(h =>
        {
            h.Kind = BareKind.NoClaude;
            h.MemberSession = "sess-2";
            h.Record = Stopped(h, RecoverRecord.Launching).ToJson();
        });

        watch.Sweep();

        Assert.Equal("cleared", State(host));
        Assert.Empty(host.Launches);
        Assert.Empty(host.Signals);
    }

    [Fact]
    public void ASupersededRecordIsRevivedWithTheMembersNewSessionAndDoesNotFlap()
    {
        var (host, watch) = Rig(h =>
        {
            h.MemberSession = "sess-2";
            h.VerdictSession = "sess-2";
            h.Record = Stopped(h, RecoverRecord.Detected, attempt: 0).ToJson();
        });

        watch.Sweep();
        Assert.Equal("cleared", State(host));

        host.Now += TimeSpan.FromSeconds(30);
        watch.Sweep();
        Assert.NotEqual("cleared", State(host));
        Assert.Equal("sess-2", host.Record!["session_id"].AsString());

        var journalBefore = host.Journal_.Count;
        Sweeps(host, watch, 1);
        Assert.NotEqual("cleared", State(host));
        Assert.DoesNotContain(host.Journal_.Skip(journalBefore), j => j["to"].AsString() == "cleared");
    }

    [Fact]
    public void ABrakeWhoseSwitchWriteFailsIsJournaledAndTriedAgainOnTheNextSweep()
    {
        var (host, watch) = Rig(h =>
        {
            h.LaunchError = RecoveryState.RefusalPrefix + "refused";
            h.BrakeThrows = 1;
        });

        Sweeps(host, watch, 3);
        Assert.Empty(host.Brakes);
        Assert.Contains(host.Journal_, j => j["kind"].AsString() == "bare-recover-brake-failed" && j["error"].AsString() == "disk full");

        Sweeps(host, watch, 1);

        Assert.Single(host.Brakes);
        Assert.False(host.RecoveryEnabled);
    }

    [Fact]
    public void ABrakeWhoseSwitchRowWriteDidNotLandIsJournaledAndTriedAgainOnTheNextSweep()
    {
        var (host, watch) = Rig(h =>
        {
            h.LaunchError = RecoveryState.RefusalPrefix + "refused";
            h.BrakeThrows = 1;
            h.BrakeError = () => new InvalidOperationException("home.recover-switch was not written: busy");
        });

        Sweeps(host, watch, 3);
        Assert.Empty(host.Brakes);
        Assert.Contains(host.Journal_, j => j["kind"].AsString() == "bare-recover-brake-failed" && j["error"].AsString().Contains("busy", StringComparison.Ordinal));

        Sweeps(host, watch, 1);

        Assert.Single(host.Brakes);
        Assert.False(host.RecoveryEnabled);
    }

    [Fact]
    public void ABrakeThatFailsWithAnUnexpectedErrorIsNotOwedAsAFailedSwitchWrite()
    {
        var (host, watch) = Rig(h =>
        {
            h.LaunchError = RecoveryState.RefusalPrefix + "refused";
            h.BrakeThrows = 1;
            h.BrakeError = () => new ArgumentException("a bug");
        });

        Sweeps(host, watch, 3);

        Assert.DoesNotContain(host.Journal_, j => j["kind"].AsString() == "bare-recover-brake-failed");
    }

    [Fact]
    public void APendingRecordOfTheSameSessionIsNotSuperseded()
    {
        var (host, watch) = Rig(h =>
        {
            h.Kind = BareKind.NoClaude;
            h.MemberSession = "sess-1";
            h.Record = Stopped(h, RecoverRecord.Launching).ToJson();
        });

        watch.Sweep();

        Assert.Equal(["sess-1"], host.Launches);
    }

    [Fact]
    public void ATerminalRecordOfAnotherSessionIsLeftAlone()
    {
        var (host, watch) = Rig(h =>
        {
            h.Kind = BareKind.Launched;
            h.MemberSession = "sess-2";
            h.Record = Stopped(h, RecoverRecord.Failed, attempt: 2, lastError: "x").ToJson();
        });

        watch.Sweep();

        Assert.Equal("failed", State(host));
        Assert.Empty(host.Launches);
        Assert.Empty(host.Signals);
    }

    [Fact]
    public void ALaunchingRecordWhoseMemberSessionIsUnknownActsAsBefore()
    {
        var (host, watch) = Rig(h =>
        {
            h.Kind = BareKind.NoClaude;
            h.MemberSession = null;
            h.Record = Stopped(h, RecoverRecord.Launching).ToJson();
        });

        watch.Sweep();

        Assert.Equal(["sess-1"], host.Launches);
    }
}
