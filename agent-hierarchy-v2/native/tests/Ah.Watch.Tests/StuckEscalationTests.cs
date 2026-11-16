using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Watch.Tests;

public sealed class StuckEscalationTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public DateTimeOffset Now = Start;
        public readonly List<JsonValue> Rows = [];
        public readonly List<WatchMail> Mails = [];
        public readonly List<JsonObject> Journal = [];
        public DateTimeOffset? ToolAt;
        public bool MailFails;
        public string TopTeam = "tm";
        private readonly StallWatch _watch;

        public Rig(StallMode mode = StallMode.On)
        {
            _watch = new StallWatch(new Fake(this), StallSettings.Defaults with { Mode = mode }, () => Now);
        }

        public void Sweep() => _watch.Sweep();

        public void Stuck(int minutesAgo, string? topTeam = "tm", string code = "git-cotenant", string session = "s1")
        {
            var o = new JsonObject
            {
                ["type"] = "watch-event",
                ["session_id"] = session,
                ["kind"] = "MEMBER-STUCK",
                ["text"] = "MEMBER-STUCK impl: 2 tool calls not permitted in 10 min (" + code + ").\nDetails: ah roster permissions diagnose impl",
                ["ts"] = Timestamps.ToIsoString(Now - TimeSpan.FromMinutes(minutesAgo)),
                ["member"] = "impl",
                ["code"] = code,
                ["team"] = "tm-ws1",
                ["top_team"] = topTeam is null ? JsonValue.Null : JsonValue.FromString(topTeam),
            };
            Rows.Add(JsonValue.FromObject(o));
        }
    }

    private sealed class Fake(Rig r) : IStallHost
    {
        public bool Owner() => true;

        public IReadOnlyList<StallTeam> Teams() => [new StallTeam(r.TopTeam, "/h", "/cwd", "orchestrator@" + r.TopTeam, "orch-session")];

        public IReadOnlyList<StallMember> Members(StallTeam team) => [];

        public StallJudgement Judge(StallTeam team, StallMember member) => throw new InvalidOperationException();

        public IReadOnlyList<string> OpenWork(StallTeam team, StallMember member) => [];

        public void Mail(WatchMail mail)
        {
            if (r.MailFails)
            {
                throw new InvalidOperationException("mailbox down");
            }

            r.Mails.Add(mail);
        }

        public void Journal(JsonObject row) => r.Journal.Add(row);

        public string? ReportedSession(string key) => null;

        public void SetReportedSession(string key, string? session)
        {
        }

        public void BeginSweep()
        {
        }

        public void ForgetStaleReported(TimeSpan unseenFor)
        {
        }

        public IReadOnlyList<JsonValue> PeerRows() => r.Rows;

        public void RecordPeerRow(JsonObject row) => r.Rows.Add(JsonValue.FromObject(row));

        public DateTimeOffset? LastToolAt(string hierarchyDir, string session) => r.ToolAt;
    }

    [Fact]
    public void AStuckMemberWithNoToolCallForTheWindowIsCopiedToTheTopOrchestratorOnceAtTheTenMinuteMark()
    {
        var rig = new Rig();
        rig.Stuck(minutesAgo: 0);

        rig.Now = Start + TimeSpan.FromMinutes(9);
        rig.Sweep();
        Assert.Empty(rig.Mails);

        rig.Now = Start + TimeSpan.FromMinutes(10);
        rig.Sweep();
        var mail = Assert.Single(rig.Mails);
        Assert.Equal("member-stuck-escalated", mail.Event);
        Assert.Equal("orchestrator@tm", mail.To);
        Assert.Contains("MEMBER-STUCK impl", mail.Text, StringComparison.Ordinal);

        rig.Now = Start + TimeSpan.FromMinutes(20);
        rig.Sweep();
        Assert.Single(rig.Mails);
        Assert.Contains(rig.Rows, r => r.Member("kind") is { IsText: true } k && k.AsString() == "MEMBER-STUCK-ESCALATED");
    }

    [Fact]
    public void NegativeAToolCallSinceTheReportClearsItAndOneBeforeItDoesNot()
    {
        var after = new Rig();
        after.Stuck(minutesAgo: 11);
        after.ToolAt = after.Now - TimeSpan.FromMinutes(5);
        after.Sweep();
        var before = new Rig();
        before.Stuck(minutesAgo: 11);
        before.ToolAt = before.Now - TimeSpan.FromMinutes(30);
        before.Sweep();

        Assert.Empty(after.Mails);
        Assert.Single(before.Mails);
    }

    [Fact]
    public void NegativeAReportYoungerThanTheWindowIsNotEscalated()
    {
        var rig = new Rig();
        rig.Stuck(minutesAgo: 9);
        rig.Sweep();

        Assert.Empty(rig.Mails);
    }

    [Fact]
    public void NegativeAReportWithNoTeamAboveItsLaneOrOneNotInTheWalkIsNotEscalated()
    {
        var none = new Rig();
        none.Stuck(minutesAgo: 11, topTeam: null);
        none.Sweep();
        var missing = new Rig { TopTeam = "other" };
        missing.Stuck(minutesAgo: 11);
        missing.Sweep();

        Assert.Empty(none.Mails);
        Assert.Empty(missing.Mails);
    }

    [Fact]
    public void AMailThatFailsRecordsNothingAndIsTriedAgainOnTheNextSweep()
    {
        var rig = new Rig { MailFails = true };
        rig.Stuck(minutesAgo: 11);
        rig.Sweep();
        Assert.Empty(rig.Mails);
        Assert.DoesNotContain(rig.Rows, r => r.Member("kind") is { IsText: true } k && k.AsString() == "MEMBER-STUCK-ESCALATED");

        rig.MailFails = false;
        rig.Sweep();
        Assert.Single(rig.Mails);
    }

    [Fact]
    public void NegativeReportModeSendsNothing()
    {
        var rig = new Rig(StallMode.Report);
        rig.Stuck(minutesAgo: 11);
        rig.Sweep();

        Assert.Empty(rig.Mails);
        Assert.Contains(rig.Journal, j => j.Get("kind")!.Value.AsString() == "member-stuck-escalated");
    }

    [Fact]
    public void TheNewestReportOfASessionAndClassIsTheOneJudged()
    {
        var rig = new Rig();
        rig.Stuck(minutesAgo: 60);
        rig.Stuck(minutesAgo: 3);
        rig.Sweep();

        Assert.Empty(rig.Mails);
    }
}
