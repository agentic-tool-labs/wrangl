using System.Text;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Hooks.Tests;

/// <summary>A peer's mail that names a request restarts that request's check-in clock, whoever sends it.</summary>
public sealed class PeerMailResetsClockTests
{
    private const string Session = "sess-orch";
    private const string RequestId = "20261027-134627-jzbn";
    private const string OtherId = "20261027-111111-zzzz";

    private sealed class NoTeamIntent : Ah.Config.ITeamIntent
    {
        public IReadOnlyList<string> Phrases() => [];

        public string? MatchedPhrase(string prompt) => null;
    }

    private static HooksHostTests.Rig Seeded(out Func<GateServices> services)
    {
        var rig = new HooksHostTests.Rig();
        var built = rig.Services with { Advisory = StopOrchestratorLivenessTests.Advisory(rig, []) with { TeamIntent = new NoTeamIntent() } };
        services = () => built;
        var file = AhPaths.UserEntry(rig.Sandbox.Home, AhPaths.PeerPendingFile);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var row = new JsonObject
        {
            ["type"] = "dispatch",
            ["session_id"] = Session,
            ["request_id"] = RequestId,
            ["to"] = "implementor",
            ["to_addr"] = "implementor@team",
            ["created"] = "2026-10-27T13:46:27.000Z",
        };
        File.WriteAllText(file, Encoding.UTF8.GetString(JsonText.ToUtf8(row, 0)) + "\n");
        return rig;
    }

    private static string Heard(HooksHostTests.Rig rig, Func<GateServices> services, string body, string fromName = "someone-else")
    {
        var prompt = $"<cross-session-message from=\"uds:/tmp/cc-socks/999.sock\" from-name=\"{fromName}\">{body}</cross-session-message>";
        var e = new JsonObject { ["hook_event_name"] = "UserPromptSubmit", ["session_id"] = Session, ["cwd"] = "/private/tmp", ["prompt"] = prompt };
        _ = rig.Run("userpromptsubmit-peer-tracking", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: services);
        return string.Join("\n", rig.Writer.Calls.Select((c, i) => (c, i)).Where(static x => x.c == "peer-records.append").Select(x => rig.Writer.Arguments[x.i]));
    }

    [Fact]
    public void AMailFromTheAddresseeThatNamesTheRequestRecordsHeardForThatRequest()
    {
        var rig = Seeded(out var services);

        var rows = Heard(rig, services, $"ah mail (note): see [hierarchy-msg /x/msgs/{RequestId}--implementor--sf5--request.md]", fromName: "implementor@team");

        Assert.Contains("\"type\":\"heard\"", rows, StringComparison.Ordinal);
        Assert.Contains($"\"request\":\"{RequestId}\"", rows, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAThirdPartysNoteNamingTheRequestRecordsNothingHeard()
    {
        var rig = Seeded(out var services);

        var rows = Heard(rig, services, $"note {RequestId}: looks fine to me");

        Assert.DoesNotContain("\"type\":\"heard\"", rows, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAThirdPartyMailQuotingTheRequestRecordsNothingHeard()
    {
        var rig = Seeded(out var services);

        var rows = Heard(rig, services, $"ah mail (report): Implementor report: [hierarchy-msg /x/msgs/{RequestId}--implementor--sf5--response.md]");

        Assert.DoesNotContain("\"type\":\"heard\"", rows, StringComparison.Ordinal);
    }

    [Fact]
    public void AMailFromAnotherPeerThatNamesAnotherRequestRecordsNothingHeard()
    {
        var rig = Seeded(out var services);

        var rows = Heard(rig, services, $"ah mail (note): see [hierarchy-msg /x/msgs/{OtherId}--implementor--other--request.md]");

        Assert.DoesNotContain("\"type\":\"heard\"", rows, StringComparison.Ordinal);
    }
}
