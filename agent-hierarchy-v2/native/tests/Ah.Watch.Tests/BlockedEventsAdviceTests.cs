using Ah.Core.Json;

namespace Ah.Watch.Tests;

/// <summary>What the member-blocked event tells the orchestrator to do about a prompt only the user can answer.</summary>
public sealed class BlockedEventsAdviceTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string Pane = "w1:p2";

    private static string Advice(string kind, string? transport, string? hash, string pane = Pane, string screen = "Do you want to proceed?")
    {
        var row = new JsonObject { ["name"] = "m1" };
        if (transport is not null)
        {
            row["transport"] = transport;
            row["transport_id"] = pane;
        }

        var team = new OwnedTeam("t", new JsonObject(), [new MemberView("m1", row, true, "blocked", "permission-prompt", null, null)]);
        var episodes = new BlockedEpisodes();
        SessionOutcome Run() => BlockedEvents.Evaluate(
            "sess", "/w", 1_000_000, "/w/.claude/ah2", "/r/bin/ah roster", [team], [], () => [], (_, _) => new PromptRead(hash, screen, "permission"),
            _ => kind, (_, _) => null, _ => 0, episodes);

        Assert.Empty(Run().Events);
        return Assert.Single(Run().Events).Text;
    }

    private static int BlockedEventsAfterTwoPolls(string kind, string screen, string? recognized) =>
        BlockedEventsOverPolls(kind, recognized, (1_000_000, Hash, screen), (1_000_000, Hash, screen)).Count;

    private static List<WatchEvent> BlockedEventsOverPolls(string kind, string? recognized, params (double Now, string Hash, string Screen)[] polls)
    {
        var row = new JsonObject { ["name"] = "m1", ["transport"] = "herdr", ["transport_id"] = Pane };
        var team = new OwnedTeam("t", new JsonObject(), [new MemberView("m1", row, true, "blocked", "permission", null, null)]);
        var episodes = new BlockedEpisodes();
        var events = new List<WatchEvent>();
        foreach (var poll in polls)
        {
            events.AddRange(BlockedEvents.Evaluate(
                "sess", "/w", poll.Now, "/w/.claude/ah2", "/r/bin/ah roster", [team], [], () => [], (_, _) => new PromptRead(poll.Hash, poll.Screen, recognized),
                _ => kind, (_, _) => null, _ => 0, episodes).Events);
        }

        return events;
    }

    private const string Other = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    [Fact]
    public void AnUnrecognisedClaudeScreenUnchangedForFiveMinutesWakesAsPossiblyBlocked()
    {
        const string Stuck = "Approve this?\n  1. Go\n  2. Stop\n";
        var events = BlockedEventsOverPolls("claude", null, (1_000_000, Hash, Stuck), (1_000_000, Hash, Stuck), (1_000_000 + BlockedEvents.UnrecognisedWakeMs, Hash, Stuck));

        var text = Assert.Single(events).Text;
        Assert.Contains("MAY be blocked", text, StringComparison.Ordinal);
        Assert.Contains("its screen shows no prompt ah recognises and has not changed for at least 5 minutes, and the sidebar shows it working", text, StringComparison.Ordinal);
        Assert.DoesNotContain("is BLOCKED", text, StringComparison.Ordinal);
        Assert.DoesNotContain("possibly blocked", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnrecognisedClaudeScreenThatKeepsChangingNeverWakes()
    {
        const string Spinner = "✻ Pausing… dramatically… (1m 39s)\n\n❯ \n";
        var events = BlockedEventsOverPolls("claude", null, (1_000_000, Hash, Spinner), (1_000_000 + 150_000, Other, Spinner), (1_000_000 + BlockedEvents.UnrecognisedWakeMs, Hash, Spinner));

        Assert.Empty(events);
    }

    // b458: the hook recorded blocked (permission), but the screen is the 21:18 one: a spinner over an empty composer.
    [Fact]
    public void AClaudeScreenTheClassifierFindsNoPromptOnRaisesNoBlockedEvent()
    {
        Assert.Equal(0, BlockedEventsAfterTwoPolls("claude", "✻ Pausing… dramatically… (1m 39s)\n\n❯ \n", null));
    }

    [Fact]
    public void AClaudeScreenTheClassifierFindsAPermissionPromptOnStillRaisesTheBlockedEvent()
    {
        Assert.Equal(1, BlockedEventsAfterTwoPolls("claude", "Do you want to proceed?\n❯ 1. Yes\n  2. No\n", "permission"));
    }

    // ah2-74: a member of the Orchestrator's implicit team: neither its row nor the team records a transport; spawn stamped the flavour.
    private static (SessionOutcome First, SessionOutcome Second, int Reads) ImplicitTeamPolls(string? flavour, string screen, string? recognized)
    {
        var (outcomes, reads) = ImplicitTeamTicks(flavour, (screen, recognized), (screen, recognized));
        return (outcomes[0], outcomes[1], reads);
    }

    /// <summary>One tick per screen of <paramref name="ticks"/>, the hook record staying blocked throughout.</summary>
    private static (List<SessionOutcome> Outcomes, int Reads) ImplicitTeamTicks(string? flavour, params (string Screen, string? Recognized)[] ticks)
    {
        var row = new JsonObject { ["name"] = "m1", ["transport_id"] = "t14s" };
        if (flavour is not null)
        {
            row["herdr_flavour"] = flavour;
        }

        var team = new OwnedTeam(null, new JsonObject { ["transport"] = JsonValue.Null, ["implicit"] = true }, [new MemberView("m1", row, true, "blocked", "permission", "2026-11-17T13:10:00.000Z", "s1")]);
        var episodes = new BlockedEpisodes();
        var reads = 0;
        var outcomes = new List<SessionOutcome>();
        foreach (var tick in ticks)
        {
            outcomes.Add(BlockedEvents.Evaluate(
                "sess", "/w", 1_000_000, "/w/.claude/ah2", "/r/bin/ah roster", [team], [], () => [], (_, _) =>
                {
                    reads++;
                    return new PromptRead(Hash, tick.Screen, tick.Recognized);
                },
                _ => "claude", (_, _) => null, _ => 0, episodes));
        }

        return (outcomes, reads);
    }

    private const string Spinner = "✻ Pausing… dramatically… (1m 39s)\n\n❯ \n";

    private const string Dialog = "Do you want to proceed?\n❯ 1. Yes\n  2. No\n";

    private static int Wakes(SessionOutcome outcome) => outcome.Gates.Count(static g => g.Row.Str("type") == "blocked-wake");

    [Fact]
    public void AnImplicitTeamHerdrCoreMemberWhoseReadableScreenShowsNoPromptRaisesNoWakeAndNoAlert()
    {
        var (first, second, reads) = ImplicitTeamPolls("core", "✻ Pausing… dramatically… (1m 39s)\n\n❯ \n", null);

        // one read per tick (B1), the second reused by the wake
        Assert.Equal(2, reads);
        Assert.Empty(first.Events.Concat(second.Events));
        Assert.Equal(0, Wakes(first) + Wakes(second));
        Assert.Equal([("s1", true)], first.ScreenClear!);
        Assert.Equal([("s1", true)], second.ScreenClear!);
    }

    // B1: the mark set on a no-prompt tick is removed on the tick that reads the real dialog, which wakes as before
    [Fact]
    public void ATickThatReadsARecognisedPromptRemovesTheMark()
    {
        var (outcomes, reads) = ImplicitTeamTicks("core", (Spinner, null), (Dialog, "permission"));

        Assert.Equal(2, reads);
        Assert.Equal([("s1", true)], outcomes[0].ScreenClear!);
        Assert.Equal([("s1", false)], outcomes[1].ScreenClear!);
        Assert.Equal(1, Wakes(outcomes[1]));
    }

    // B1: an unreadable tick removes the mark (it fails toward showing blocked)
    [Fact]
    public void AnUnreadableTickRemovesTheMark()
    {
        var (outcomes, _) = ImplicitTeamTicks("core", (Spinner, null), ("  \n", null));

        Assert.Equal([("s1", true)], outcomes[0].ScreenClear!);
        Assert.Equal([("s1", false)], outcomes[1].ScreenClear!);
    }

    // negative: a member whose hook record is not blocked is neither read nor marked
    [Fact]
    public void AMemberNotBlockedByItsHookRecordIsNeitherReadNorMarked()
    {
        var row = new JsonObject { ["name"] = "m1", ["transport_id"] = "t14s", ["herdr_flavour"] = "core" };
        var team = new OwnedTeam(null, new JsonObject { ["transport"] = JsonValue.Null }, [new MemberView("m1", row, true, "working", null, "2026-11-17T13:10:00.000Z", "s1")]);
        var reads = 0;
        var outcome = BlockedEvents.Evaluate(
            "sess", "/w", 1_000_000, "/w/.claude/ah2", "/r/bin/ah roster", [team], [], () => [], (_, _) =>
            {
                reads++;
                return new PromptRead(Hash, Spinner, null);
            },
            _ => "claude", (_, _) => null, _ => 0, new BlockedEpisodes());

        Assert.Equal(0, reads);
        Assert.Empty(outcome.ScreenClear!);
    }

    // ah2-102 F1: coverage is the read's guard without the blocked term: a working Claude herdr member is covered; a codex member and one
    // not live to the watcher are not (negatives)
    [Fact]
    public void CoverageNamesTheMembersTheBlockedPassWouldReadWhetherOrNotTheyAreBlocked()
    {
        static JsonObject Row(string name, string? kind = null)
        {
            var row = new JsonObject { ["name"] = name, ["transport_id"] = "t-" + name, ["herdr_flavour"] = "core" };
            if (kind is not null)
            {
                row["kind"] = kind;
            }

            return row;
        }

        var team = new OwnedTeam(null, new JsonObject { ["transport"] = JsonValue.Null },
        [
            new MemberView("m1", Row("m1"), true, "working", null, "2026-11-17T13:10:00.000Z", "s1"),
            new MemberView("m2", Row("m2", "codex"), true, "blocked", "permission", "2026-11-17T13:10:00.000Z", "s2"),
            new MemberView("m3", Row("m3"), false, "blocked", "permission", "2026-11-17T13:10:00.000Z", "s3"),
        ]);
        var outcome = BlockedEvents.Evaluate(
            "sess", "/w", 1_000_000, "/w/.claude/ah2", "/r/bin/ah roster", [team], [], () => [], (_, _) => new PromptRead(Hash, Spinner, null),
            r => r.Str("kind") ?? "claude", (_, _) => null, _ => 0, new BlockedEpisodes());

        Assert.Equal(["s1"], outcome.Covered);
    }

    [Fact]
    public void AnImplicitTeamHerdrCoreMemberAtARecognisedPromptRaisesTheWakeAndTheAlert()
    {
        var (first, second, _) = ImplicitTeamPolls("core", "Do you want to proceed?\n❯ 1. Yes\n  2. No\n", "permission");

        Assert.Empty(first.Events);
        Assert.Contains("is BLOCKED at a prompt", Assert.Single(second.Events).Text, StringComparison.Ordinal);
        Assert.Equal(1, Wakes(second));
    }

    // negative: a row with no transport and no flavour stamp is not on herdr: no screen read, and today's UNCONFIRMED wording
    [Fact]
    public void AnImplicitTeamMemberWithNoFlavourStampIsNotReadAndStaysUnconfirmed()
    {
        var (_, second, reads) = ImplicitTeamPolls(null, "✻ Pausing… dramatically… (1m 39s)\n\n❯ \n", null);

        Assert.Equal(0, reads);
        Assert.Contains("UNCONFIRMED: its screen could not be read", Assert.Single(second.Events).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableClaudeScreenStillRaisesTheBlockedEventOnTheRecordAlone()
    {
        Assert.Equal(1, BlockedEventsAfterTwoPolls("claude", "  \n", null));
    }

    [Fact]
    public void ANonClaudeScreenWithNoRecognisedPromptStillRaisesTheBlockedEvent()
    {
        Assert.Equal(1, BlockedEventsAfterTwoPolls("codex", "› working\n", null));
    }

    [Fact]
    public void TheAdviceNeverAnswersForTheUserAndSaysWhereTheUserAnswers()
    {
        var text = Advice("claude", "herdr", Hash);

        Assert.Contains("Only the user can answer this prompt. Never answer it yourself", text, StringComparison.Ordinal);
        Assert.Contains("say in chat which member waits and on what, and that they answer it in m1's pane", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Default: cancel", text, StringComparison.Ordinal);
    }

    private const string ParkTail = "\"options\":[\"Answer it in the pane of the member\",\"Cancel the prompt\"],\"default\":null,\"choice\":null,\"decider\":null,"
        + "\"rationale\":\"only the user can answer a prompt a member is blocked at\",\"revert\":\"none\",\"review\":false,"
        + "\"why_user\":\"unsure\",\"dangerous\":false,\"exchange\":null}'";

    private const string ParkHead = "/r/bin/ah msg decision add --team t --json '{\"kind\":\"parked\",\"item\":\"m1@t\",\"source\":\"watch:member-blocked\",\"question\":\"m1@t waits at a prompt only the user can answer: ";

    [Fact]
    public void TheParkCommandIsOnePlainCommandWithTheJsonArgumentAndEveryFieldTheValidatorRequires()
    {
        var text = Advice("claude", "herdr", Hash);
        Assert.Contains("watch:member-blocked", text, StringComparison.Ordinal);

        Assert.Contains(ParkHead + "<what it asks>\"," + ParkTail, text, StringComparison.Ordinal);
        Assert.Contains("Replace <what it asks> with what the prompt asks in your own words, and keep single quotes out of the text", text, StringComparison.Ordinal);
        Assert.DoesNotContain("printf", text, StringComparison.Ordinal);
        Assert.Contains("ONE PushNotification", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoScreenTextEverReachesThePrintedParkCommand()
    {
        const string Screen = "Edit ~/.config/agent-hierarchy.json and band.token and --session abc, then ah roster trust set";
        var text = Advice("claude", "herdr", Hash, screen: Screen);

        var at = text.IndexOf("/r/bin/ah msg decision add", StringComparison.Ordinal);
        Assert.True(at >= 0, text);
        var command = text[at..];
        var end = command.IndexOf("}'", StringComparison.Ordinal);
        command = command[..(end + 2)];
        Assert.Contains(ParkHead + "<what it asks>\"," + ParkTail, command, StringComparison.Ordinal);
        foreach (var leaked in new[] { "agent-hierarchy.json", "band.token", "--session", "trust set" })
        {
            Assert.DoesNotContain(leaked, command, StringComparison.Ordinal);
        }

        Assert.Contains("agent-hierarchy.json", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CancelIsOfferedOnlyOnTheUsersWordForAClaudeMemberOnHerdrWithAReadableScreen()
    {
        Assert.Contains("only if the user tells you to", Advice("claude", "herdr", Hash), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("codex", "herdr", Hash, Pane)]
    [InlineData("claude", "tmux", Hash, Pane)]
    [InlineData("claude", null, Hash, Pane)]
    [InlineData("claude", "herdr", null, Pane)]
    [InlineData("claude", "herdr", Hash, "w1-1")]
    [InlineData("claude", "herdr", Hash, "w1:t2")]
    [InlineData("claude", "herdr", "0123456789abcdef", Pane)]
    [InlineData("claude", "herdr", "0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef", Pane)]
    public void CancelIsNotOfferedWhereTheVerbWouldRefuseIt(string kind, string? transport, string? hash, string pane)
    {
        var text = Advice(kind, transport, hash, pane);

        Assert.DoesNotContain("--cancel", text, StringComparison.Ordinal);
        Assert.Contains("Only the user can answer this prompt", text, StringComparison.Ordinal);
    }
}
