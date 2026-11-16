using System.Globalization;

namespace Ah.Commands.Tests;

/// <summary>The <c>ahd new -i</c> line prompt and confirm, driven through an injected reader, and the flag parser.</summary>
public sealed class AhdNewPromptTests
{
    private static (string Answer, string Shown) Ask(string typed, string defaultValue, Func<string, string?> check)
    {
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var answer = NewVerb.Prompt(new StringReader(typed), stderr, "model", defaultValue, check);
        return (answer, stderr.ToString());
    }

    [Fact]
    public void EnterAcceptsTheDefaultShownInBrackets()
    {
        var (answer, shown) = Ask("\n", "opus", _ => null);

        Assert.Equal("opus", answer);
        Assert.Equal("model [opus]: ", shown);
    }

    [Fact]
    public void ATypedAnswerReplacesTheDefault()
    {
        Assert.Equal("sonnet", Ask("sonnet\n", "opus", _ => null).Answer);
    }

    [Fact]
    public void ARejectedAnswerPrintsTheRefusalAndAsksAgain()
    {
        var (answer, shown) = Ask("haiku\nsonnet\n", "opus", a => a == "haiku" ? "model \"haiku\" is not usable" : null);

        Assert.Equal("sonnet", answer);
        Assert.Contains("ahd new: model \"haiku\" is not usable\n", shown, StringComparison.Ordinal);
        Assert.Equal(2, shown.Split("model [opus]: ").Length - 1);
    }

    [Fact]
    public void EndOfFileAbortsWithExit130()
    {
        var failure = Assert.Throws<RosterFailure>(() => Ask(string.Empty, "opus", _ => null));

        Assert.Equal(NewVerb.Aborted, failure.ExitCode);
    }

    [Fact]
    public void ConfirmLaunchesOnEnterOrYAndNotOnN()
    {
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        Assert.True(NewVerb.Confirm(new StringReader("\n"), stderr));
        Assert.Equal("Launch? [Y/n] ", stderr.ToString());
        Assert.True(NewVerb.Confirm(new StringReader("y\n"), TextWriter.Null));
        Assert.False(NewVerb.Confirm(new StringReader("n\n"), TextWriter.Null));
        Assert.True(NewVerb.Confirm(new StringReader("maybe\nY\n"), TextWriter.Null));
        Assert.Equal(NewVerb.Aborted, Assert.Throws<RosterFailure>(() => NewVerb.Confirm(new StringReader(string.Empty), TextWriter.Null)).ExitCode);
    }

    [Fact]
    public void AcceptingInheritAfterARefusedRosterModelInheritsAndTheResolvedDefaultStillResolves()
    {
        // The roster's model was refused, so [inherit] was the default: Enter must pin inherit, not re-read the roster.
        Assert.Equal("inherit", NewVerb.ChosenModel(null, "inherit", "inherit", rosterRefused: true));

        // Nearest input that must not pin: a roster model that resolved, accepted as shown, stays a flagless resolution.
        Assert.Null(NewVerb.ChosenModel(null, "opus", "opus", rosterRefused: false));
        Assert.Null(NewVerb.ChosenModel(null, "inherit", "inherit", rosterRefused: false));

        Assert.Equal("sonnet", NewVerb.ChosenModel(null, "sonnet", "opus", rosterRefused: false));
        Assert.Equal("opus", NewVerb.ChosenModel("opus", "opus", "inherit", rosterRefused: false));
    }

    [Fact]
    public void TheParserTakesShortAndLongFlagsAndRejectsTheRest()
    {
        var parsed = NewVerb.Parse(["jim", "-ct", "claude", "-t", "rc4", "-m", "opus", "--tab", "-i", "--dry-run"])!;
        Assert.Equal(("jim", "claude", "rc4", "opus", true, true, true), (parsed.Name, parsed.CliType, parsed.Team, parsed.Model, parsed.Tab, parsed.Interactive, parsed.DryRun));

        Assert.Equal("rc4", NewVerb.Parse(["jim", "--team", "rc4", "--model", "opus", "--cli-type", "claude", "--interactive"])!.Team);
        Assert.Null(NewVerb.Parse(["jim", "-x"]));
        Assert.Null(NewVerb.Parse(["jim", "bob"]));
        Assert.Null(NewVerb.Parse(["jim", "-m"]));
        Assert.Null(NewVerb.Parse(["-i"])!.Name);
    }
}
