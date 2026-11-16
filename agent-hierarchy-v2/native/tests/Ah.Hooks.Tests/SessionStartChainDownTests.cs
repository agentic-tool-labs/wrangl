using System.Text;
using Ah.Config;
using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Hooks.Tests;

/// <summary>A session start whose chain of command is proven Down is told to wind down, with its <c>active</c> row written at once and no grace.</summary>
public sealed class SessionStartChainDownTests
{
    private const string Session = "s-a";

    private sealed class Directive : IDirectiveText
    {
        public string? DesignRoutingBlock(JsonObject resolved) => null;

        public string? ContractBlock(string role, JsonObject? resolved, bool peerRoute = false) => null;

        public string BuildDirective(JsonObject fullResolved, string? sessionId, string? hierarchyDirectory = null, string? model = null, bool subOrchestrator = false) => "DIRECTIVE";

        public string BuildNudge(JsonObject resolved) => string.Empty;

        public string BuildRoleSessionNotice(string role, string? agentType, JsonObject? resolved = null) => "NOTICE";

        public string StatusReport(string? cwd) => string.Empty;

        public string WindDown(string link, string why, string orchestratorAddress, string form) => StopDirective.Instance.WindDown(link, why, orchestratorAddress, form);
    }

    /// <summary>A member session in pane p1 of team tm; with <paramref name="unclaimed"/> the team has no orchestrator record, otherwise it names a session.</summary>
    private static HooksHostTests.Rig MemberRig(bool unclaimed)
    {
        var rig = AutoTrackTests.MemberRig("p1");
        if (unclaimed)
        {
            AutoTrackTests.WriteTeam(
                rig,
                new JsonObject
                {
                    ["members"] = new JsonArray { new JsonObject { ["name"] = "implementor-1", ["role"] = "implementor", ["transport_id"] = "p1" } },
                });
        }

        return rig;
    }

    private static string Start(HooksHostTests.Rig rig, string agentType, string source = "startup", HarnessSessions? sessions = null)
    {
        var box = new CallerBox("implementor-1@tm", "/box", "tm", "implementor-1", "/tm.json");
        var advisory = new AdvisoryServices(
            BindingAttestationTests.MailboxFor(rig),
            new StopPeerNudgeReportTests.FakeHooks(ReportState.Sent, mailed: true),
            new BindingAttestationTests.Callers([box], null),
            null!,
            null!,
            new Directive(),
            null!,
            null!,
            new BindingAttestationTests.Daemon(),
            null!,
            null!);
        var services = rig.Services with
        {
            Advisory = advisory,
            Sessions = sessions ?? new HarnessSessions(),
            Process = rig.Services.Process with { ParentPid = HarnessSessions.Pid },
        };
        var e = new JsonObject { ["hook_event_name"] = "SessionStart", ["session_id"] = Session, ["cwd"] = rig.Sandbox.Root + "/proj", ["source"] = source, ["agent_type"] = agentType };
        return Encoding.UTF8.GetString(rig.Run("sessionstart", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services).Stdout.Span);
    }

    /// <summary>The states of the chain-down rows written through the rig's state writer for <see cref="Session"/> or the given session.</summary>
    internal static List<string> States(HooksHostTests.Rig rig, string session = Session) =>
        [.. rig.Writer.Calls.Zip(rig.Writer.Arguments)
            .Where(p => p.First == "peer-records.append")
            .Select(p => JsonText.Parse(Encoding.UTF8.GetBytes(p.Second)).Member("rec")!.Value)
            .Where(r => r.Member("type") is { IsText: true } t && t.AsString() == "chain-down" && r.Member("session_id") is { IsText: true } s && s.AsString() == session)
            .Select(r => r.Member("state")!.Value.AsString())];

    [Fact]
    public void AMemberWhoseChainIsDownGetsTheWindDownTextAsTheLastBlockAfterItsNoticeAndAnActiveRow()
    {
        var rig = MemberRig(unclaimed: true);

        var output = Start(rig, "wrangl:implementor");

        var notice = output.IndexOf("NOTICE", StringComparison.Ordinal);
        var windDown = output.IndexOf("your chain of command is down (tm: unclaimed)", StringComparison.Ordinal);
        Assert.True(notice >= 0 && windDown > notice, output);
        Assert.Contains("(2) commit your work on your branch", output, StringComparison.Ordinal);
        Assert.Contains("ah msg send orchestrator@tm --kind note", output, StringComparison.Ordinal);
        Assert.DoesNotContain("DIRECTIVE", output[windDown..], StringComparison.Ordinal);
        Assert.Equal(["active"], States(rig));
    }

    [Fact]
    public void NegativeAMemberWhoseChainIsNotProvenDownIsToldNothingAndWritesNoRow()
    {
        var rig = MemberRig(unclaimed: false);

        var output = Start(rig, "wrangl:implementor");

        Assert.Contains("NOTICE", output, StringComparison.Ordinal);
        Assert.DoesNotContain("chain of command is down", output, StringComparison.Ordinal);
        Assert.Empty(States(rig));
    }

    [Fact]
    public void AMemberWhoseRowIsAlreadyActiveIsToldAgainAndNoSecondRowIsWritten()
    {
        var rig = MemberRig(unclaimed: true);
        StopPeerNudgeReportTests.ChainDownRow(rig, Session);

        var output = Start(rig, "wrangl:implementor", source: "compact");

        Assert.Contains("your chain of command is down (tm: unclaimed)", output, StringComparison.Ordinal);
        Assert.Empty(States(rig));
    }

    [Fact]
    public void NegativeATopOrchestratorWhoseTeamReadsOrphanedIsNotToldAndWritesNoRow()
    {
        var rig = new HooksHostTests.Rig();
        AutoTrackTests.WriteTeam(
            rig,
            new JsonObject
            {
                ["orchestrator"] = new JsonObject { ["session_id"] = "former-session", ["pid"] = 99999, ["pid_start"] = "Mon Jan  1 00:00:00 2001" },
                ["members"] = new JsonArray(),
            });
        var sessions = new HarnessSessions();
        sessions.Starts[99999] = "Tue Jan  2 00:00:00 2001";

        var output = Start(rig, "wrangl:orchestrator", sessions: sessions);

        Assert.DoesNotContain("chain of command is down", output, StringComparison.Ordinal);
        Assert.Empty(States(rig));
    }
}
