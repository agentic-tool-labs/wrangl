namespace Ah.Commands.Tests;

/// <summary>ah2-471 D4: <c>--key=value</c> is one flag in the roster parser.</summary>
public sealed class RosterArgsTests
{
    [Fact]
    public void AnEqualsFormFlagIsOneFlag()
    {
        var a = RosterArgs.Parse(["checklist", "add", "x y", "--team=pantry", "--note=a=b", "--plain=yes", "--cwd=", "--item", "i"]);

        Assert.Equal(["checklist", "add", "x y"], a.Positionals);
        Assert.Equal("pantry", a.Text("team"));
        Assert.Equal("a=b", a.Text("note"));
        Assert.True(a.IsTrue("plain"));
        Assert.True(a.IsTrue("cwd"));

        // the negative: a word with no = is parsed as before, taking the next word as its value
        Assert.Equal("i", a.Text("item"));
    }

    /// <summary>ah2-471 D5: a <c>--</c> word holding whitespace is split as a shell would have; T-D4/D5.</summary>
    [Fact]
    public void AFlagWordHoldingWhitespaceIsSplit()
    {
        var a = RosterArgs.Parse(["checklist", "add", "x y", "--team=pantry", "--cwd /p --plain"]);

        Assert.Equal(["checklist", "add", "x y"], a.Positionals);
        Assert.Equal(("pantry", "/p", true), (a.Text("team"), a.Text("cwd"), a.IsTrue("plain")));

        // the negatives: a value with spaces, and a --k=v word whose value holds spaces, are never split
        Assert.Equal("has  spaces", RosterArgs.Parse(["--note", "has  spaces"]).Text("note"));
        Assert.Equal("a --b c", RosterArgs.Parse(["--note=a --b c"]).Text("note"));
    }
}
