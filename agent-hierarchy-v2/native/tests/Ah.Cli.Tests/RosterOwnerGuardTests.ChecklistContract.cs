namespace Ah.Cli.Tests;

/// <summary>The checklist's output contract (JSON by default, <c>--plain</c> for text), the repeated-add window and the state names.</summary>
public sealed partial class RosterOwnerGuardTests
{
    private static int Count(string text, string part) => text.Split(part).Length - 1;

    [Fact]
    public void AnAddPrintsJsonWithCreatedTrueAndTheSameAddAgainReturnsTheItemWithCreatedFalseAndWritesNothing()
    {
        TeamOwnedBy(Self);

        var first = Run("checklist", "add", "Fix X", "--team", "tm");
        var before = ReadLog(LogOf("_run"));
        var again = Run("checklist", "add", "Fix X", "--team", "tm");

        Assert.Equal(0, first.Code);
        Assert.Contains("\"item\": \"fix-x\"", first.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"created\": true", first.Stdout, StringComparison.Ordinal);
        Assert.Equal(0, again.Code);
        Assert.Contains("\"item\": \"fix-x\"", again.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"created\": false", again.Stdout, StringComparison.Ordinal);
        Assert.Equal(before, ReadLog(LogOf("_run")));
        Assert.Equal(1, Count(Run("checklist", "show", "--team", "tm").Stdout, "\"id\": \"fix-x"));
    }

    [Fact]
    public void NegativeATitleThatDiffersByCaseMakesANewItemAndOneThatDiffersByWhitespaceOnlyIsTheSameItem()
    {
        TeamOwnedBy(Self);
        _ = Run("checklist", "add", "Fix X", "--team", "tm");

        var spaced = Run("checklist", "add", "  Fix   X ", "--team", "tm");
        var cased = Run("checklist", "add", "fix x", "--team", "tm");

        Assert.Contains("\"created\": false", spaced.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"created\": true", cased.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"item\": \"fix-x-2\"", cased.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeTheSameTitleOutsideTheWindowMakesANewItem()
    {
        TeamOwnedBy(Self);
        _ = Directory.CreateDirectory(ProgressFolder);
        WriteLog(LogOf("_run"), string.Join('\n', Row("fix-x", "add", ",\"title\":\"Fix X\"")) + "\n");

        var add = Run("checklist", "add", "Fix X", "--team", "tm");

        Assert.Contains("\"created\": true", add.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"item\": \"fix-x-2\"", add.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeTheSameTitleAfterTheFirstItemWasDroppedMakesANewItem()
    {
        TeamOwnedBy(Self);
        _ = Run("checklist", "add", "Fix X", "--team", "tm");
        Assert.Equal(0, Run("checklist", "set", "fix-x", "--state", "dropped", "--team", "tm").Code);

        var add = Run("checklist", "add", "Fix X", "--team", "tm");

        Assert.Contains("\"created\": true", add.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"item\": \"fix-x-2\"", add.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeTheSameTitleInAnotherStreamMakesANewItem()
    {
        TwoStreamsWithItemA();
        _ = Run("checklist", "add", "Fix X", "--team", "tm");

        var other = Run("checklist", "add", "Fix X", "--stream", "s2", "--team", "tm");

        Assert.Contains("\"created\": true", other.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"stream\": \"s2\"", other.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainPrintsTheTextLineForAddSetAndDoneAndShowPrintsTheListNotJson()
    {
        TeamOwnedBy(Self);

        var add = Run("checklist", "add", "Fix X", "--plain", "--team", "tm");
        var set = Run("checklist", "set", "fix-x", "--state", "dispatched", "--plain", "--team", "tm");
        var shown = Run("checklist", "show", "--plain", "--team", "tm");
        var done = Run("checklist", "done", "fix-x", "--plain", "--team", "tm");

        Assert.Equal("checklist: fix-x → queued\n", add.Stdout);
        Assert.Equal("checklist: fix-x → dispatched\n", set.Stdout);
        Assert.StartsWith("fix-x dispatched owner=- | Fix X", shown.Stdout, StringComparison.Ordinal);
        Assert.Equal("checklist: fix-x → done\n", done.Stdout);
    }

    [Theory]
    [InlineData("3e304c3")]
    [InlineData("3e304c3d5a1b2c3d4e5f60718293a4b5c6d7e8f9")]
    public void DoneAcceptsACommitGivenWithAShortOrFullSha(string sha)
    {
        TeamOwnedBy(Self);
        _ = Run("checklist", "add", "Fix X", "--team", "tm");

        var done = Run("checklist", "done", "fix-x", "--commit", sha, "--plain", "--team", "tm");

        Assert.Equal("checklist: fix-x → done\n", done.Stdout);
    }

    [Fact]
    public void NegativeDoneWithAStrayWordAndNoCommitFlagIsStillTheUsageLine()
    {
        TeamOwnedBy(Self);
        _ = Run("checklist", "add", "Fix X", "--team", "tm");

        var done = Run("checklist", "done", "fix-x", "3e304c3", "--team", "tm");

        Assert.NotEqual("checklist: fix-x → done\n", done.Stdout);
        Assert.Contains("usage: ah roster checklist", done.Stderr + done.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowPrintsJsonWithItemsAndQueueByDefault()
    {
        TeamOwnedBy(Self);
        _ = Run("checklist", "add", "Fix X", "--team", "tm");

        var shown = Run("checklist", "show", "--team", "tm").Stdout;

        Assert.StartsWith("{", shown, StringComparison.Ordinal);
        Assert.Contains("\"items\":", shown, StringComparison.Ordinal);
        Assert.Contains("\"queue\":", shown, StringComparison.Ordinal);
        Assert.Contains("\"state\": \"queued\"", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void WaitingUserNeedsANoteTheUnderscoreAliasIsAcceptedAndBothShowAsWaitingUser()
    {
        TeamOwnedBy(Self);
        _ = Run("checklist", "add", "Fix X", "--team", "tm");
        var before = ReadLog(LogOf("_run"));

        var bare = Run("checklist", "set", "fix-x", "--state", "waiting-user", "--team", "tm");
        var noted = Run("checklist", "set", "fix-x", "--state", "waiting-user", "--note", "needs the user", "--team", "tm");
        var alias = Run("checklist", "set", "fix-x", "--state", "waiting_user", "--note", "still the user", "--team", "tm");

        Assert.NotEqual(0, bare.Code);
        Assert.Contains("needs --note", bare.Stderr, StringComparison.Ordinal);
        Assert.Contains("\"state\": \"waiting-user\"", noted.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"state\": \"waiting-user\"", alias.Stdout, StringComparison.Ordinal);
        Assert.NotEqual(before, ReadLog(LogOf("_run")));
    }

    [Fact]
    public void NegativeAnUnnotatedWaitingUserWritesNothing()
    {
        TeamOwnedBy(Self);
        _ = Run("checklist", "add", "Fix X", "--team", "tm");
        var before = ReadLog(LogOf("_run"));

        var bare = Run("checklist", "set", "fix-x", "--state", "waiting_user", "--team", "tm");

        Assert.NotEqual(0, bare.Code);
        Assert.Equal(before, ReadLog(LogOf("_run")));
    }

    [Fact]
    public void SettingTheStateDoneIsRefusedWithAPointerToTheDoneVerbAndWritesNothingWhileDoneWorks()
    {
        TeamOwnedBy(Self);
        _ = Run("checklist", "add", "Fix X", "--team", "tm");
        var before = ReadLog(LogOf("_run"));

        var state = Run("checklist", "set", "fix-x", "--state", "done", "--team", "tm");
        var unchanged = ReadLog(LogOf("_run"));
        var done = Run("checklist", "done", "fix-x", "--team", "tm");

        Assert.NotEqual(0, state.Code);
        Assert.Contains("done is not a state; run ah roster checklist done fix-x; nothing written", state.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, unchanged);
        Assert.Equal(0, done.Code);
        Assert.Contains("\"state\": \"done\"", done.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuestionTakesItsOptionsAndAReAskWithoutThemWritesNone()
    {
        TeamOwnedBy(Self);
        _ = Run("checklist", "add", "Fix X", "--team", "tm");

        var asked = Run("checklist", "set", "fix-x", "--state", "waiting-user", "--note", "Which?", "--option", "Ship", "--option", "Hold", "--team", "tm");
        var log = ReadLog(LogOf("_run"));
        var again = Run("checklist", "set", "fix-x", "--state", "waiting-user", "--note", "Which now?", "--team", "tm");

        Assert.Equal(0, asked.Code);
        Assert.Contains("\"options\":[\"Ship\",\"Hold\"]", log, StringComparison.Ordinal);
        Assert.Equal(0, again.Code);
        Assert.DoesNotContain("options", ReadLog(LogOf("_run"))[log.Length..], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("waiting-orch", new[] { "a" }, "only with --state waiting-user")]
    [InlineData(null, new[] { "a" }, "only with --state waiting-user")]
    [InlineData("waiting-user", new[] { "a", "b", "c", "d", "e" }, "1 to 4 options")]
    [InlineData("waiting-user", new[] { "a", "a" }, "repeat")]
    [InlineData("waiting-user", new[] { "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" }, "1 to 64")]
    public void NegativeABadOptionWritesNothing(string? state, string[] options, string why)
    {
        TeamOwnedBy(Self);
        _ = Run("checklist", "add", "Fix X", "--team", "tm");
        var before = ReadLog(LogOf("_run"));
        var args = new List<string> { "checklist", "set", "fix-x", "--note", "Which?", "--team", "tm" };
        args.AddRange(state is null ? [] : ["--state", state]);
        args.AddRange(options.SelectMany(static o => new[] { "--option", o }));

        var refused = Run([.. args]);

        Assert.NotEqual(0, refused.Code);
        Assert.Contains(why, refused.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, ReadLog(LogOf("_run")));
    }

    [Fact]
    public void TheUsageAndTheInvalidStateTextMarkTheStatesThatNeedANote()
    {
        TeamOwnedBy(Self);
        _ = Run("checklist", "add", "Fix X", "--team", "tm");

        var bad = Run("checklist", "set", "fix-x", "--state", "nonsense", "--team", "tm");
        var usage = Run("checklist", "bogus", "--team", "tm");

        Assert.Contains("blocked*, waiting-user*", bad.Stderr, StringComparison.Ordinal);
        Assert.Contains("(* needs --note)", bad.Stderr, StringComparison.Ordinal);
        Assert.Contains("blocked*|waiting-user*", usage.Stderr, StringComparison.Ordinal);
        Assert.Contains("checklist done <id>", usage.Stderr, StringComparison.Ordinal);
    }
}
