using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Mesh;

namespace Ah.Hooks.Tests;

/// <summary>A Stop whose obligations live in a database that cannot be read is held once, not allowed as if nothing were owed.</summary>
public sealed class StopPeerNudgeUnreadableStoreTests
{
    private const string Session = "sess-u";

    internal sealed class DbMode : IMessageModeReader
    {
        public string Read(string domain) => "db";
    }

    internal sealed class UnreadablePeerLog : IPeerRecordSource
    {
        private static InvalidOperationException Down() => new("the state database cannot be read");

        public IReadOnlyList<JsonValue> ReadAll() => throw Down();

        public IReadOnlyList<JsonValue> Rows(PeerQuery query) => throw Down();

        public IReadOnlyList<JsonValue> LatestByKeyFor(JsonValue? sessionId) => throw Down();

        public IReadOnlyList<PeerRowAt> Since(long cursor) => throw Down();

        public long Version() => throw Down();
    }

    private static string Stop(bool unreadable, bool reStop)
    {
        var rig = new HooksHostTests.Rig(peerRoute: unreadable ? new PeerRecordsRoute(new UnreadablePeerLog(), new DbMode()) : null);
        var advisory = new AdvisoryServices(
            BindingAttestationTests.MailboxFor(rig), new StopPeerNudgeReportTests.FakeHooks(ReportState.NotWritten, false), new BindingAttestationTests.Callers([]), null!, null!, StopDirective.Instance, null!, null!, new BindingAttestationTests.Daemon(), null!, null!);
        var services = rig.Services with { Advisory = advisory };
        var e = new JsonObject { ["hook_event_name"] = "Stop", ["session_id"] = Session, ["cwd"] = rig.Sandbox.Root + "/proj", ["stop_hook_active"] = reStop };
        var outcome = rig.Run("stop-peer-nudge", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services);
        return Encoding.UTF8.GetString(outcome.Stdout.Span);
    }

    [Fact]
    public void AnUnreadablePeerLogHoldsTheStopOnceAndTheSecondStopLeaves()
    {
        Assert.Contains("cannot be read", Stop(unreadable: true, reStop: false), StringComparison.Ordinal);
        Assert.Equal(string.Empty, Stop(unreadable: true, reStop: true));
    }

    [Fact]
    public void AReadablePeerLogWithNothingOwedStaysSilent() =>
        Assert.Equal(string.Empty, Stop(unreadable: false, reStop: false));
}
