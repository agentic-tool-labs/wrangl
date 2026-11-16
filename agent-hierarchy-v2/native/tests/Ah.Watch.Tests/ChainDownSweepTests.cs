using System.Globalization;
using Ah.Core.Json;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Watch.Tests;

/// <summary>ahd winds down a member whose chain of command has been proven down for the whole grace, once, and never on anything less than proof.</summary>
public sealed class ChainDownSweepTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-03-01T12:00:00Z", CultureInfo.InvariantCulture);

    private sealed class ChainHost : FakeHost, IWatchHost
    {
        internal Func<WatchRegistration, ChainState> Chain { get; set; } = static _ => new ChainState(ChainKind.Up, null, "up");

        internal int Asked { get; private set; }

        ChainState IWatchHost.ChainOf(WatchRegistration member)
        {
            Asked++;
            return Chain(member);
        }

        string IWatchHost.WindDownText(string link, string why, string orchestratorAddress) => $"wind down: {link}: {why}; resume note to {orchestratorAddress}";
    }

    private static ChainState Down => new(ChainKind.Down, "beta", "orchestrator orphaned");

    private static ChainState Unknown => new(ChainKind.Unknown, null, "cannot tell");

    private sealed class Rig(FakeHost host)
    {
        internal FakeHost Host { get; } = host;

        internal FakeClock Clock { get; } = new(Start);

        private WatchTick? _tick;

        /// <summary>One tick, then the clock moves on by <paramref name="seconds"/>.</summary>
        internal async Task TickAsync(int seconds = 0)
        {
            var files = new NullFiles();
            var env = new MapEnv();
            _tick ??= new WatchTick(new WatchDependencies(
                Host, env, Clock, files, Ah.Hierarchy.HierarchyModule.CreateRecovery(files, env, Clock), WatchModule.CreateStatusDirs(), () => true));
            await _tick.RunAsync(CancellationToken.None);
            Clock.UtcNow = Clock.UtcNow.AddSeconds(seconds);
        }
    }

    private static Rig WithMember(FakeHost host, params string[] addresses)
    {
        host.FeedBackGates = true;
        host.Registrations.Add(new WatchRegistration("m1", "claude", 200, "2026-03-01T11:00:00.000Z", [.. addresses.Select(static a => new WatchBox(a, "/boxes/" + a))], "/private/tmp/ahprobe.h"));
        return new Rig(host);
    }

    private static IEnumerable<WatchMail> Mailed(Rig rig, string name) => rig.Host.Mails.Where(m => m.Event == name);

    [Fact]
    public async Task AChainThatComesBackWithinTheGraceSendsNothingAndTheGraceStartsAgainAfterwards()
    {
        var host = new ChainHost { Chain = static _ => Down };
        var rig = WithMember(host, "impl@beta");

        await rig.TickAsync(119);
        await rig.TickAsync(1);
        host.Chain = static _ => new ChainState(ChainKind.Up, null, "up");
        await rig.TickAsync(10);
        host.Chain = static _ => Down;
        await rig.TickAsync(119);
        await rig.TickAsync(0);

        Assert.Empty(host.Mails);
        Assert.Empty(host.AppendedPeers);
    }

    [Fact]
    public async Task AChainDownForTheGraceWritesOneRowOneWindDownFromAhdAndOneNoteToTheOrchestratorBox()
    {
        var host = new ChainHost { Chain = static _ => Down };
        var rig = WithMember(host, "impl@beta");

        await rig.TickAsync(121);
        await rig.TickAsync(30);
        await rig.TickAsync(30);

        var row = Assert.Single(host.AppendedPeers);
        Assert.Equal("chain-down", row.Str("type"));
        Assert.Equal("active", row.Str("state"));
        Assert.Equal("m1", row.Str("session_id"));
        Assert.Equal("impl", row.Str("member"));
        Assert.Equal("beta", row.Str("link"));
        var wind = Assert.Single(Mailed(rig, "wind-down"));
        Assert.Equal("impl@beta", wind.To);
        Assert.Equal("m1", wind.ToSession);
        Assert.Equal("wind down: beta: orchestrator orphaned; resume note to orchestrator@beta", wind.Text);
        Assert.Equal("chain-down:m1", wind.Data.Get("request_id")!.Value.AsString());
        Assert.Equal(Start.ToUnixTimeMilliseconds(), (long)wind.Data.Get("due_slot")!.Value.AsNumber());
        Assert.Null(wind.Data.Get("open_requests"));
        var note = Assert.Single(Mailed(rig, "member-wound-down"));
        Assert.Equal("orchestrator@beta", note.To);
        Assert.Equal("impl wound down: chain broken at beta (orchestrator orphaned)", note.Text);
        Assert.Equal("chain-down-note:m1", note.Data.Get("request_id")!.Value.AsString());
        Assert.True(note.Data.Get("due_slot")!.Value.IsNumber);
    }

    private static bool Active(FakeHost host) =>
        RosterModule.ChainDownActive(host.SeededPeers.Concat(host.AppendedPeers).Select(static r => (JsonValue)r), "m1");

    [Fact]
    public async Task T8aAChainThatReturnsClearsTheRowOnceAndNudgesResume()
    {
        var host = new ChainHost { Chain = static _ => Down };
        var rig = WithMember(host, "impl@beta");
        await rig.TickAsync(121);
        await rig.TickAsync(1);
        Assert.True(Active(host));

        host.Chain = static _ => new ChainState(ChainKind.Up, null, "up");
        await rig.TickAsync(30);
        await rig.TickAsync(30);

        Assert.False(Active(host));
        var cleared = Assert.Single(host.AppendedPeers, static r => r.Str("state") == "cleared");
        Assert.Equal("chain-down", cleared.Str("type"));
        Assert.Equal("m1", cleared.Str("session_id"));
    }

    private static ChainHost LeadWithAHookWrittenRestoredBareRow(ChainState chain)
    {
        var host = new ChainHost { Chain = _ => chain };
        host.SeededPeers.Add(new JsonObject { ["type"] = "chain-down", ["state"] = "active", ["session_id"] = "m1", ["why"] = "restored-bare", ["link"] = "lane" });
        return host;
    }

    [Fact]
    public async Task T3iAHookWrittenRestoredBareRowSurvivesTicksWhileTheLeadStaysDownAndNothingIsMailed()
    {
        var host = LeadWithAHookWrittenRestoredBareRow(new ChainState(ChainKind.Down, "lane", "restored-bare"));
        var rig = WithMember(host, "lead@beta");

        await rig.TickAsync(1);
        await rig.TickAsync(121);
        await rig.TickAsync(121);

        Assert.True(Active(host));
        Assert.Empty(host.AppendedPeers);
        Assert.Empty(host.Mails);
    }

    [Fact]
    public async Task T3jTheSameRowIsClearedOnceTheChainIsProvenWholeAgain()
    {
        var host = LeadWithAHookWrittenRestoredBareRow(new ChainState(ChainKind.Up, null, "up"));
        var rig = WithMember(host, "lead@beta");

        await rig.TickAsync(30);
        await rig.TickAsync(30);

        _ = Assert.Single(host.AppendedPeers, static r => r.Str("state") == "cleared");
        Assert.False(Active(host));
    }

    [Fact]
    public async Task NegativeT3kTheSameRowStaysWhenTheChainCannotBeJudged()
    {
        var host = LeadWithAHookWrittenRestoredBareRow(Unknown);
        var rig = WithMember(host, "lead@beta");

        await rig.TickAsync(30);
        await rig.TickAsync(30);

        Assert.True(Active(host));
        Assert.Empty(host.AppendedPeers);
    }

    [Fact]
    public async Task AChainThatIsUnknownLeavesTheRowActiveAndAWholeChainWithNoRowWritesNothing()
    {
        var host = new ChainHost { Chain = static _ => Unknown };
        host.SeededPeers.Add(new JsonObject { ["type"] = "chain-down", ["state"] = "active", ["session_id"] = "m1" });
        var rig = WithMember(host, "impl@beta");

        await rig.TickAsync(300);
        await rig.TickAsync(30);

        Assert.True(Active(host));
        Assert.Empty(host.AppendedPeers);

        var quiet = new ChainHost();
        var other = WithMember(quiet, "impl@beta");
        await other.TickAsync(30);
        await other.TickAsync(30);

        Assert.Empty(quiet.AppendedPeers);
    }

    [Fact]
    public async Task T3hAFailedMemberMailWritesNoRowAndTheNextTickSendsItUnderTheSameDueSlot()
    {
        var host = new ChainHost { Chain = static _ => Down, FailMail = static m => m.Event == "wind-down" };
        var rig = WithMember(host, "impl@beta");

        await rig.TickAsync(121);
        await rig.TickAsync(10);

        Assert.Empty(host.AppendedPeers);
        Assert.Empty(host.Mails);

        host.FailMail = null;
        await rig.TickAsync(10);
        await rig.TickAsync(10);

        var row = Assert.Single(host.AppendedPeers);
        Assert.Equal("active", row.Str("state"));
        var wind = Assert.Single(Mailed(rig, "wind-down"));
        Assert.Equal(Start.ToUnixTimeMilliseconds(), (long)wind.Data.Get("due_slot")!.Value.AsNumber());
        Assert.Single(Mailed(rig, "member-wound-down"));
    }

    [Fact]
    public async Task AnUnknownChainNeverWindsAMemberDownHoweverLongItStaysUnknown()
    {
        var host = new ChainHost { Chain = static _ => Unknown };
        var rig = WithMember(host, "impl@beta");

        for (var i = 0; i < 60; i++)
        {
            await rig.TickAsync(60);
        }

        Assert.Empty(host.Mails);
        Assert.Empty(host.AppendedPeers);
    }

    [Fact]
    public async Task AMemberWithAnActiveChainDownRowIsNotToldAgain()
    {
        var host = new ChainHost { Chain = static _ => Down };
        host.SeededPeers.Add(new JsonObject { ["type"] = "chain-down", ["state"] = "active", ["session_id"] = "m1", ["member"] = "impl", ["team"] = "beta" });
        var rig = WithMember(host, "impl@beta");

        await rig.TickAsync(121);
        await rig.TickAsync(60);

        Assert.Empty(host.Mails);
        Assert.Empty(host.AppendedPeers);
    }

    [Fact]
    public async Task OnlyAMemberBoxIsJudgedAnOrchestratorOnlyRegistrationIsNot()
    {
        var host = new ChainHost { Chain = static _ => Down };
        var rig = WithMember(host, "orchestrator@beta");

        await rig.TickAsync(121);
        await rig.TickAsync(60);

        Assert.Equal(0, host.Asked);
        Assert.Empty(host.Mails);
    }

    [Fact]
    public async Task AHostWithoutAChainReaderNeverWindsAnyoneDown()
    {
        var host = new FakeHost();
        var rig = WithMember(host, "impl@beta");

        for (var i = 0; i < 60; i++)
        {
            await rig.TickAsync(60);
        }

        Assert.Empty(host.Mails);
        Assert.Empty(host.AppendedPeers);
    }
}
