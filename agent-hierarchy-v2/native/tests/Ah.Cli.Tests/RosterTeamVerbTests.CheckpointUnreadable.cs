using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>A checkpoint is the successor's handoff: a message store that cannot say what is open refuses it, and nothing is written.</summary>
public sealed partial class RosterTeamVerbTests
{
    private IMsgSource? _msgSource;

    private void BreakMessageStore() => _msgSource = new UnreadableMsgs();

    private List<string> CheckpointFiles() =>
        [.. Directory.EnumerateFiles(_sandbox.Root, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f).Contains("checkpoint", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Contains("snapshot", StringComparison.OrdinalIgnoreCase))];

    [Fact]
    public void ATopLevelDismissOfALiveIdleCleanMemberIsHeldWhenTheMessageStoreCannotBeRead()
    {
        ArrangeCheckpoint();
        BreakMessageStore();
        var plan = Roster(["dismiss", Impl, "--plan", "--team", "proj"]);
        Assert.True(plan.Code == 0, plan.Stdout + plan.Stderr);
        var token = JsonText.Parse(plan.Stdout).Member("close_token") is { IsText: true } t ? t.AsString() : "none";

        var (code, stdout, stderr) = Roster(["dismiss", Impl, "--close", "--confirm", "--plan-token", token, "--team", "proj"]);

        Assert.True(code != 0, $"exit {code}: {stdout}{stderr}");
        Assert.Contains("cannot be read", stdout + stderr, StringComparison.Ordinal);
        Assert.False(Killed("%1"));
    }

    [Fact]
    public void AQuitWithAnUnreadableMessageStoreRefusesAtItsCheckpointAndWritesNoRecord()
    {
        ArrangeCheckpoint();
        _withPanes = true;
        _callers = new RecordingCallers { ProcessTable = new NoAncestry() };
        BreakMessageStore();
        var before = CheckpointFiles();

        var (code, stdout, stderr) = Roster(["quit", "--team", "proj"]);

        Assert.True(code != 0, $"exit {code}: {stdout}");
        Assert.Contains("cannot be read", stdout + stderr, StringComparison.Ordinal);
        Assert.Equal(before, CheckpointFiles());
    }

    [Fact]
    public void AnOwnerVerbOnACorruptStoreIsRefusedByNameAndPointsAtDbRestore()
    {
        ArrangeCheckpoint();
        File.WriteAllText(Path.Join(_sandbox.AhHome, "state", "ah.db"), new string('x', 8192));

        var (code, stdout, stderr) = Roster(["dismiss", Impl, "--plan", "--team", "proj"]);

        Assert.True(code == 2, $"exit {code}: {stdout}{stderr}");
        Assert.Equal("store-unreadable", JsonText.Parse(stdout).Member("refused")?.AsString());
        Assert.Contains("ah db restore", stderr, StringComparison.Ordinal);
    }

    /// <summary>A message store that is down: every read throws <see cref="StoreUnavailableException"/>.</summary>
    private sealed class UnreadableMsgs : IMsgScope
    {
        private static StoreUnavailableException Down() => new("the state database cannot be read");

        public IMsgScope Scope() => throw Down();

        public void Dispose()
        {
        }

        public IReadOnlyList<MsgExchangePair> Exchanges(IReadOnlyList<string> poolDirs) => throw Down();

        public IReadOnlyList<MsgFileRow> Messages(string poolDir, string? id = null, string? type = null) => throw Down();

        public IReadOnlyList<MsgFileRow> MessagesById(string id, string type) => throw Down();

        public IReadOnlyList<MsgFileRow> ArchivedMessages(string poolDir) => throw Down();

        public MsgFileRow? ByPath(string path) => throw Down();

        public long Version() => throw Down();

        public IReadOnlyList<string> ChangedIds(string poolDir, long sinceVersion) => throw Down();
    }
}
