using System.Runtime.Versioning;
using Ah.Abstractions;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>ah-msgs-fast-2 §3.8 H3: a mailbox register or unregister publishes its session's registration status entry.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class RegistrationEntryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-05-28T20:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void ARegisterWritesItsSessionsEntryAndAnUnregisterDeletesItWhileAMissedUnregisterLeavesEveryEntry()
    {
        var sandbox = UnitSandbox.Create();
        var home = sandbox.Root + "/ah";
        _ = Directory.CreateDirectory(home + "/registrations");
        var store = new MemoryEntries();
        store.Rows.Add(new StatusEntryRow(sandbox.Root + "/gone/.claude/ah2", "meta", "version", "{\"version\":1}"));
        var fx = MeshFixture.Create(new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = home }, Now, sandbox.Root, statusEntries: store);

        fx.Prove("s-1", null, new BoxRef("imp@tm", home + "/mailbox/tm/imp"));
        fx.Prove("s-2", null, new BoxRef("rev@tm", home + "/mailbox/tm/rev"));

        Assert.Equal(["s-1", "s-2"], store.Registrations.Select(static r => r.Key));
        Assert.Contains("imp@tm", store.Registrations[0].Json, StringComparison.Ordinal);

        // negative: an unregister that removed nothing publishes no delete
        Assert.False(fx.Mailbox.RemoveRegistration("s-none"));
        Assert.Equal(2, store.Registrations.Count);

        Assert.True(fx.Mailbox.RemoveRegistration("s-1"));
        Assert.Equal(["s-2"], store.Registrations.Select(static r => r.Key));
    }

    /// <summary>The store's contract over a list: enough for the deltas, which read status dirs and replace rows.</summary>
    private sealed class MemoryEntries : IStatusEntryStore
    {
        internal List<StatusEntryRow> Rows { get; } = [];

        internal List<StatusEntryRow> Registrations => [.. Rows.Where(static r => r.Kind == "registration").OrderBy(static r => r.Key, StringComparer.Ordinal)];

        public IReadOnlyList<StatusEntryRow> Read(string statusDir) => [.. Rows.Where(r => r.PoolDir == statusDir || r.Kind == "registration")];

        public IReadOnlyList<string> StatusDirs() => [.. Rows.Where(static r => r.Kind == "meta").Select(static r => r.PoolDir)];

        public IReadOnlyList<StatusEntryRow> ReadKind(string poolDir, string kind, string? key = null) =>
            [.. Rows.Where(r => r.PoolDir == poolDir && r.Kind == kind && (key is null || r.Key == key))];

        public IReadOnlyList<string> Listing(string poolDir) => [.. Rows.Where(r => r.Kind == "view" && r.Key == poolDir).Select(static r => r.PoolDir)];

        public IReadOnlyList<string> PoolsWith(string kind, string key) => [.. Rows.Where(r => r.Kind == kind && r.Key == key).Select(static r => r.PoolDir)];

        public (bool Ok, string Message) Replace(
            IReadOnlyList<(string PoolDir, string Kind)> scopes, IReadOnlyList<(string PoolDir, string Kind, string Key)> deletes, IReadOnlyList<StatusEntryRow> rows, long changedMs)
        {
            _ = Rows.RemoveAll(r => scopes.Contains((r.PoolDir, r.Kind)) || deletes.Contains((r.PoolDir, r.Kind, r.Key)) || rows.Any(n => (n.PoolDir, n.Kind, n.Key) == (r.PoolDir, r.Kind, r.Key)));
            Rows.AddRange(rows);
            return (true, string.Empty);
        }
    }
}
