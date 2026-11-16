using System.Reflection;
using System.Runtime.Versioning;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>The process seam of one-liveness.md §3: each reading's answer under both policies, and the core member over JSON.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class PidRunningSeamTests
{
    private const string Start = "Wed Nov  4 11:00:00 2026";

    /// <summary>A mailbox any call to fails: the seam must never read registrations (the doorbell holds its lock).</summary>
    public class NoMailbox : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new InvalidOperationException("the seam read the mailbox: " + targetMethod?.Name);
    }

    private sealed class Procs : IProcessTable, IProcessInspector
    {
        private readonly Dictionary<int, (string Start, string Command, bool Owned)> _running = new()
        {
            [101] = (Start, "claude", true),
            [103] = ("Thu Nov  5 15:00:00 2026", "claude", true),
            [104] = (Start, "/bin/zsh", true),
            [105] = (Start, "claude", false),
        };

        public IReadOnlyList<int>? Ancestors(int pid) => null;

        public bool? HasAncestor(int pid, int candidate) => null;

        public string? StartTime(long pid) => _running.TryGetValue((int)pid, out var p) ? p.Start : null;

        public string? Command(int pid) => CommandLine(pid);

        public bool PidAlive(int pid) => _running.ContainsKey(pid);

        public bool PidOwned(int pid) => _running.TryGetValue(pid, out var p) && p.Owned;

        public string? CommandLine(int pid) => _running.TryGetValue(pid, out var p) ? p.Command : null;
    }

    private static readonly Procs Processes = new();

    private static Ah.Hierarchy.IMemberLiveness Seam(DateTimeOffset? boot = null) =>
        MeshModule.CreateMemberLiveness(DispatchProxy.Create<IMailbox, NoMailbox>(), Processes, Processes, () => boot);

    [Theory]
    [InlineData(0, Start, true, false, false)] // no reading
    [InlineData(200, Start, true, false, false)] // absent, and the mailbox is never read to find a reference
    [InlineData(105, Start, true, false, false)] // another user's process
    [InlineData(101, Start, true, true, true)] // same start
    [InlineData(103, Start, true, false, false)] // reused: started after the record
    [InlineData(101, "not a start", true, false, true)] // start can't be compared
    [InlineData(101, "local", true, true, true)] // an older record's local-time form of the same start
    [InlineData(101, null, true, true, true)] // no start, harness command line
    [InlineData(104, null, true, false, true)] // no start, command line not the harness
    [InlineData(104, null, false, true, true)] // not a claude harness: no command-line test
    public void EachReadingGivesEachPolicysAnswer(int pid, string? start, bool claude, bool proven, bool runningOrUnknown)
    {
        if (start == "local")
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(2026, 11, 4, 11, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Local);
            start = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{local:ddd MMM} {local.Day,2} {local:HH:mm:ss yyyy}");
        }

        Assert.Equal(proven, Seam().PidRunning(pid, start, LivenessPolicy.ProvenRunning, claude));
        Assert.Equal(runningOrUnknown, Seam().PidRunning(pid, start, LivenessPolicy.RunningOrUnknown, claude));
    }

    [Fact]
    public void ARecordFromBeforeTheBootIsNotRunningUnderEitherPolicyAndOneFromAfterIs()
    {
        var rebooted = Seam(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        var running = Seam(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));

        Assert.False(rebooted.PidRunning(101, Start, LivenessPolicy.ProvenRunning));
        Assert.False(rebooted.PidRunning(101, Start, LivenessPolicy.RunningOrUnknown));
        Assert.True(running.PidRunning(101, Start, LivenessPolicy.ProvenRunning));
        Assert.True(running.PidRunning(101, Start, LivenessPolicy.RunningOrUnknown));
    }

    /// <summary>A mailbox that holds no registrations and calls none live.</summary>
    public class EmptyMailbox : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.ReturnType == typeof(bool) ? false : targetMethod?.ReturnType == typeof(IReadOnlyList<JsonObject>) ? Array.Empty<JsonObject>() : null;
    }

    [Fact]
    public void AMemberIsFoundOnlyByAnExactAddressInARegistrationsAddressListAndNegativeANearAddressIsNot()
    {
        var seam = MeshModule.CreateMemberLiveness(DispatchProxy.Create<IMailbox, EmptyMailbox>(), Processes, Processes, () => null);
        static JsonObject Reg(string session, JsonArray addresses) =>
            new() { ["session_id"] = session, ["pid"] = 101, ["pid_start"] = Start, ["at"] = "2026-11-04T11:00:00Z", ["addresses"] = addresses };

        var near = Reg("near", [5, "imp@tm2", "xmp@tm", JsonValue.Null]);
        var exact = Reg("exact", ["lead@tm", "imp@tm"]);

        Assert.Equal("exact", seam.ForMember("imp", "tm", [near, exact])?.SessionId);
        Assert.Null(seam.ForMember("imp", "tm", [near]));
        Assert.Null(seam.ForMember("imp", "tm", [Reg("empty", [])]));
    }

    [Fact]
    public void TheCoreGuardsThePidDelegatesWhenWiredAndIsKillZeroWhenNot()
    {
        var sandbox = UnitSandbox.Create();
        var fx = MeshFixture.Create(new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = sandbox.Root + "/ah" }, DateTimeOffset.UtcNow, sandbox.Root);
        var core = fx.Hierarchy.Core;
        var self = JsonValue.FromNumber(Environment.ProcessId);
        JsonValue? reused = JsonValue.FromString("Thu Jan  1 00:00:00 2015");

        core.Liveness = null;
        Assert.True(core.PidRunning(self, reused, LivenessPolicy.ProvenRunning)); // today's kill-0
        Assert.False(core.PidRunning(JsonValue.FromNumber(0), null, LivenessPolicy.RunningOrUnknown));

        core.Liveness = Seam();
        JsonValue? start = JsonValue.FromString(Start);
        Assert.True(core.PidRunning(JsonValue.FromNumber(101), start, LivenessPolicy.ProvenRunning));
        Assert.False(core.PidRunning(JsonValue.FromNumber(103), start, LivenessPolicy.RunningOrUnknown));
        foreach (var bad in new[] { JsonValue.FromNumber(101.5), JsonValue.FromNumber(-101), JsonValue.FromString("101"), JsonValue.FromNumber(2147483648d), JsonValue.Null })
        {
            Assert.False(core.PidRunning(bad, start, LivenessPolicy.RunningOrUnknown));
        }
    }
}
