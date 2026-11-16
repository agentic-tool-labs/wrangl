using System.Runtime.Versioning;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>A registration whose recorded process the kernel showed gone is not looked up again; a live one is looked up every time.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class RegistrationLiveMemoTests
{
    private const string Recorded = "Wed Nov  4 11:00:00 2026";

    /// <summary>A pid no system gives out, so the kernel's own answer for it is "no such process".</summary>
    private const int Gone = 999_999;

    private const int Reused = 4_243;

    private const int Live = 4_244;

    /// <summary>A pid that runs but whose start cannot be read, so its liveness is unknown.</summary>
    private const int Unknown = 4_245;

    /// <summary>A database session store that only records which rows were ended.</summary>
    private sealed class EndingStore : ISessionAuthority
    {
        internal List<(string Session, long Pid, string Start)> Ended { get; } = [];

        public bool IsDatabase => true;

        public SessionAuthorityResult Register(string registrationFile, string sessionId, Func<ReadOnlyMemory<byte>?, SessionDecision> decide) => new(false, "failed", "unused");

        public SessionAuthorityResult Unregister(string registrationFile, string sessionId) => new(false, "failed", "unused");

        public bool EndGone(string sessionId, long pid, string pidStart)
        {
            Ended.Add((sessionId, pid, pidStart));
            return true;
        }
    }

    private sealed class CountingTable : IProcessTable, IProcessInspector
    {
        private readonly Dictionary<int, string?> _starts = new() { [Reused] = "Thu Nov  5 15:00:00 2026", [Live] = Recorded, [Unknown] = null };

        internal Dictionary<long, int> Asks { get; } = [];

        public IReadOnlyList<int>? Ancestors(int pid) => null;

        public bool? HasAncestor(int pid, int candidate) => null;

        public string? StartTime(long pid)
        {
            Asks[pid] = Asks.GetValueOrDefault(pid) + 1;
            return _starts.GetValueOrDefault((int)pid);
        }

        public string? Command(int pid) => CommandLine(pid);

        public bool PidAlive(int pid) => _starts.ContainsKey(pid);

        public bool PidOwned(int pid) => _starts.ContainsKey(pid);

        public string? CommandLine(int pid) => _starts.ContainsKey(pid) ? "claude" : null;
    }

    private static JsonObject Registration(int pid) => new() { ["session_id"] = "s" + pid, ["pid"] = pid, ["pid_start"] = Recorded, ["harness"] = "claude" };

    [Theory]
    [InlineData(Gone, false)]
    [InlineData(Reused, false)]
    [InlineData(Live, true)]
    [InlineData(Unknown, true)]
    public void AGoneProcessIsLookedUpOnceAndEndedOnceAndALiveOneEveryTime(int pid, bool live)
    {
        var sandbox = UnitSandbox.Create();
        var vars = new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = sandbox.Root + "/ah" };
        var table = new CountingTable();
        var store = new EndingStore();
        var fx = MeshFixture.Create(vars, DateTimeOffset.Parse("2026-01-01T12:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture), sandbox.Root, table: table, sessions: store);
        fx.Hierarchy.Core.Liveness = MeshModule.CreateMemberLiveness(fx.Mailbox, table, table);

        Assert.Equal(live, fx.Mailbox.RegistrationLive(Registration(pid)));
        var afterFirst = table.Asks.GetValueOrDefault(pid);
        Assert.Equal(live, fx.Mailbox.RegistrationLive(Registration(pid)));
        Assert.Equal(live, fx.Mailbox.RegistrationLive(Registration(pid)));

        if (live)
        {
            Assert.True(table.Asks[pid] > afterFirst);
            Assert.Empty(store.Ended);
        }
        else
        {
            Assert.Equal(afterFirst, table.Asks.GetValueOrDefault(pid));
            Assert.Equal([("s" + pid, (long)pid, Recorded)], store.Ended);
        }
    }
}
