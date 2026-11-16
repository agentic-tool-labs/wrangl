using System.Text;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Hooks.Tests;

/// <summary>The user's question log (spec qa-history-all §4): the Orchestrator's typed prompts and its replies land in the <c>qa_log</c> table.</summary>
public sealed class QaLogCaptureTests
{
    private const string Session = "sess-1";

    private sealed record Scene(HooksHostTests.Rig Rig, string Dir, string Transcript, GateServices Services)
    {
        public List<JsonValue> Rows() => [.. Services.Hierarchy.Decisions.QaLogRows(Dir)];
    }

    private static Scene Arrange()
    {
        var rig = new HooksHostTests.Rig(new Dictionary<string, string> { ["AH_MAIL_DOORBELL"] = "0" }, processTable: new HarnessSessions().Table);
        var dir = rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj");
        _ = Directory.CreateDirectory(dir);
        rig.Writer.Executing = [.. rig.Services.Hierarchy.StateDomains.Where(static d => d.Name is "decisions" or "peer-records" or "qa-log")];
        var transcript = Path.Combine(rig.Sandbox.Home, ".claude", "projects", "proj", Session + ".jsonl");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        var advisory = StopOrchestratorLivenessTests.Advisory(rig, [], deciderDir: dir) with
        {
            Recovery = HierarchyModule.CreateRecovery(rig.Services.Files, rig.Services.Environment, rig.Services.Clock),
        };
        return new Scene(rig, dir, transcript, rig.Services with { Advisory = advisory });
    }

    private static void Prompt(Scene s, string prompt) => Run(s, "userpromptsubmit-peer-tracking", new JsonObject { ["hook_event_name"] = "UserPromptSubmit", ["prompt"] = prompt });

    private static void Stop(Scene s) => Run(s, "stop-orchestrator-liveness", new JsonObject { ["hook_event_name"] = "Stop", ["transcript_path"] = s.Transcript });

    private static void Run(Scene s, string unit, JsonObject e)
    {
        e["session_id"] = Session;
        e["cwd"] = s.Rig.Sandbox.Root + "/proj";
        _ = s.Rig.Run(unit, Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => s.Services);
    }

    private static string User(string text) => Line(new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = text } });

    private static string ToolResult() => Line(new JsonObject
    {
        ["type"] = "user",
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray { new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "t1", ["content"] = "ok" } } },
    });

    private static string Assistant(string? text, bool toolUse = false)
    {
        var content = new JsonArray();
        if (text is not null)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }

        if (toolUse)
        {
            content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = "t1", ["name"] = "Bash", ["input"] = new JsonObject() });
        }

        return Line(new JsonObject { ["type"] = "assistant", ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content } });
    }

    private static string Line(JsonObject o) => Encoding.UTF8.GetString(JsonText.ToUtf8(o, 0)) + "\n";

    private static string Text(JsonValue row, string key) => row.Member(key) is { IsText: true } t ? t.AsString() : string.Empty;

    [Fact]
    public void ATypedPromptAndTheWholeReplyOfItsTurnAreLogged()
    {
        var s = Arrange();
        const string asked = "why did the build fail?";

        Prompt(s, asked);
        // a Stop hook's feedback and a meta entry sit inside the turn, not at its start
        var meta = Line(new JsonObject { ["type"] = "user", ["isMeta"] = true, ["message"] = new JsonObject { ["role"] = "user", ["content"] = "caveat" } });
        File.WriteAllText(s.Transcript, User("earlier") + Assistant("old answer") + User(asked) + Assistant("Looking.", toolUse: true) + ToolResult()
            + User("Stop hook feedback:\nreport first") + meta + Assistant("It failed on a missing using."));
        Stop(s);

        var rows = s.Rows();
        var prompt = Assert.Single(rows, static r => Text(r, "kind") == "prompt");
        Assert.Equal(asked, Text(prompt, "text"));
        Assert.True(prompt.Member("question")!.Value.AsBool());
        var reply = Assert.Single(rows, static r => Text(r, "kind") == "reply" && Text(r, "part") != "direct");
        Assert.Equal("Looking.\n\nIt failed on a missing using.", Text(reply, "text"));
        Assert.Equal("It failed on a missing using.", Text(reply, "final"));
        Assert.Equal(Text(prompt, "id"), Text(reply, "prompt_id"));
        Assert.Equal("typed", Text(reply, "trigger"));
        var direct = Assert.Single(rows, static r => Text(r, "part") == "direct");
        Assert.Equal("Looking.", Text(direct, "text"));
        Assert.Equal("d" + Text(prompt, "id"), Text(direct, "id"));
    }

    // ---------------------------------------------------------------- replies by part (spec qa-history-replies §10)

    private static string Entry(JsonObject o, string uuid)
    {
        o["uuid"] = uuid;
        return Line(o);
    }

    private static string Asked(string uuid, string text, bool meta = false, string? origin = null)
    {
        var o = new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = text } };
        if (meta)
        {
            o["isMeta"] = true;
        }

        if (origin is not null)
        {
            o["origin"] = new JsonObject { ["kind"] = origin };
        }

        return Entry(o, uuid);
    }

    private static string Says(string message, string text) => Entry(new JsonObject
    {
        ["type"] = "assistant",
        ["message"] = new JsonObject { ["id"] = message, ["role"] = "assistant", ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } } },
    }, message + "-" + text.Length);

    private static string Calls(string message, string toolUseId) => Entry(new JsonObject
    {
        ["type"] = "assistant",
        ["message"] = new JsonObject
        {
            ["id"] = message,
            ["role"] = "assistant",
            ["content"] = new JsonArray { new JsonObject { ["type"] = "tool_use", ["id"] = toolUseId, ["name"] = "Bash", ["input"] = new JsonObject { ["command"] = "true" } } },
        },
    }, message + "-" + toolUseId);

    private static string Result(string toolUseId) => Entry(new JsonObject
    {
        ["type"] = "user",
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray { new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = toolUseId, ["content"] = "ok" } } },
    }, "r-" + toolUseId);

    private static string Queued(string text, string origin = "human") => Line(new JsonObject
    {
        ["type"] = "attachment",
        ["attachment"] = new JsonObject { ["type"] = "queued_command", ["commandMode"] = "prompt", ["prompt"] = text, ["origin"] = new JsonObject { ["kind"] = origin } },
    });

    // the lines a byte check passes over: they sit between steps in a real transcript
    private static string Noise() => Line(new JsonObject { ["type"] = "api-request", ["body"] = new string('x', 2000) });

    private static string? FirstStep(Scene s, string toolUseId) =>
        HierarchyModule.QaEarlyStepDirect(s.Services.Files, s.Transcript, toolUseId, HierarchyModule.QaIsUserTyped)?.Direct;

    // the activity unit's PostToolUse, as the hook runs it after the call
    private static void PostToolUse(Scene s, string toolUseId) => Run(s, "activity", new JsonObject
    {
        ["hook_event_name"] = "PostToolUse", ["tool_name"] = "Bash", ["tool_use_id"] = toolUseId, ["transcript_path"] = s.Transcript,
        ["tool_input"] = new JsonObject { ["command"] = "true" },
    });

    private static void Later(Scene s, int seconds = 5) => ((Ah.TestKit.FakeClock)s.Services.Clock).UtcNow += TimeSpan.FromSeconds(seconds);

    private static string PromptId(Scene s, string text) => Text(s.Rows().Last(r => Text(r, "kind") == "prompt" && Text(r, "text") == text), "id");

    private static List<JsonValue> Replies(Scene s, string? part = null) => [.. s.Rows().Where(r => Text(r, "kind") == "reply" && (part is null ? Text(r, "part") != "direct" : Text(r, "part") == part))];

    [Fact]
    public void TheDirectReplyIsTheTextBeforeTheQuestionsFirstToolCallAndTheTurnRowCarriesTheFinal()
    {
        var s = Arrange();
        const string asked = "is the cache warm?";
        Prompt(s, asked);
        File.WriteAllText(s.Transcript, Asked("u1", asked) + Noise() + Says("m1", "Yes, it is warm.") + Calls("m1", "A") + Noise() + Result("A")
            + Says("m2", "Checking more.") + Calls("m2", "B") + Result("B") + Calls("m3", "C") + Result("C") + Noise() + Says("m4", "All checks passed."));

        Assert.Equal("Yes, it is warm.", FirstStep(s, "A"));
        Assert.Equal("Yes, it is warm.", FirstStep(s, "B")); // the second step tries again: the first's call may not have been written yet
        Assert.Null(FirstStep(s, "C")); // negative: a third step is past the early steps
        PostToolUse(s, "A");
        PostToolUse(s, "B");
        Assert.Equal("Yes, it is warm.", Text(Assert.Single(Replies(s, "direct")), "text"));
        Stop(s);

        var direct = Assert.Single(Replies(s, "direct"));
        Assert.Equal("Yes, it is warm.", Text(direct, "text"));
        var turn = Assert.Single(Replies(s));
        Assert.Equal("All checks passed.", Text(turn, "final"));
        Assert.Equal("Yes, it is warm.\n\nChecking more.\n\nAll checks passed.", Text(turn, "text"));
    }

    [Fact]
    public void AQuestionTypedDuringTheFirstQuestionsFirstToolIsFiledApartFromIt()
    {
        var s = Arrange();
        const string q1 = "is the cache warm?";
        const string q2 = "and the queue?";
        Prompt(s, q1);
        var head = Asked("u1", q1) + Says("m1", "Warm.") + Calls("m1", "A");
        File.WriteAllText(s.Transcript, head);
        // the mod bridge submits a prompt typed mid-turn at Enter, while A still runs: the latest prompt is q2 before A's PostToolUse
        Later(s);
        Prompt(s, q2);
        PostToolUse(s, "A");
        File.WriteAllText(s.Transcript, head + Result("A") + Queued(q2) + Says("m2", "Empty.") + Calls("m2", "B") + Result("B"));
        PostToolUse(s, "B");

        var directs = Replies(s, "direct").ToDictionary(r => Text(r, "prompt_id"), r => Text(r, "text"));
        Assert.Equal(new Dictionary<string, string> { [PromptId(s, q1)] = "Warm.", [PromptId(s, q2)] = "Empty." }, directs);
    }

    [Fact]
    public void AFastFirstToolNotYetInTheTranscriptIsCapturedOnTheSecondStep()
    {
        var s = Arrange();
        const string asked = "what failed?";
        Prompt(s, asked);
        // the transcript is written buffered: at A's PostToolUse its call is not there yet
        File.WriteAllText(s.Transcript, Asked("u1", asked) + Says("m1", "The lint step."));
        PostToolUse(s, "A");
        Assert.Empty(Replies(s, "direct"));

        File.WriteAllText(s.Transcript, Asked("u1", asked) + Says("m1", "The lint step.") + Calls("m1", "A") + Result("A") + Calls("m2", "B"));
        PostToolUse(s, "B");

        Assert.Equal(("The lint step.", PromptId(s, asked)), (Text(Assert.Single(Replies(s, "direct")), "text"), Text(Assert.Single(Replies(s, "direct")), "prompt_id")));
    }

    [Fact]
    public void NegativeAThirdToolStepWritesNothingAndReadsNoQuestionRows()
    {
        var s = Arrange();
        const string asked = "what failed?";
        Prompt(s, asked);
        File.WriteAllText(s.Transcript, Asked("u1", asked) + Says("m1", "The lint step.") + Calls("m1", "A") + Result("A") + Calls("m2", "B") + Result("B") + Calls("m3", "C"));
        var before = s.Rig.Writer.Calls.Count;

        // the scan alone decides: past the early steps it answers null, so the unit returns before the gate, the rows read and the append
        Assert.Null(FirstStep(s, "C"));
        PostToolUse(s, "C");

        Assert.DoesNotContain("qa-log.append", s.Rig.Writer.Calls.Skip(before));
        Assert.Empty(Replies(s, "direct"));
    }

    [Fact]
    public void TwoQuestionsWithTheSameTextInTurnsApartAreEachFiledUnderTheirOwn()
    {
        var s = Arrange();
        Prompt(s, "yes");
        File.WriteAllText(s.Transcript, Asked("u1", "yes") + Says("m1", "Going ahead.") + Calls("m1", "A") + Result("A") + Says("m2", "Done."));
        PostToolUse(s, "A");
        Stop(s);
        var first = PromptId(s, "yes");
        Later(s);
        Prompt(s, "yes");
        var second = PromptId(s, "yes");
        File.AppendAllText(s.Transcript, Asked("u2", "yes") + Says("m3", "Again, then.") + Calls("m3", "B") + Result("B") + Says("m4", "Done again."));
        PostToolUse(s, "B");
        Stop(s);

        Assert.NotEqual(first, second);
        var directs = Replies(s, "direct").ToDictionary(r => Text(r, "prompt_id"), r => Text(r, "text"));
        Assert.Equal(new Dictionary<string, string> { [first] = "Going ahead.", [second] = "Again, then." }, directs);
        Assert.Equal([first, second], Replies(s).Select(static r => Text(r, "prompt_id")));
    }

    /// <summary>
    /// What the G1 rule (newest equal text) does when the same text is typed again during the first one's first tool step: the first
    /// one's direct reply is filed under the second, whose own is then ignored by id. Kept as the record of that limit.
    /// </summary>
    [Fact]
    public void KnownLimitTheSameTextTypedDuringTheFirstOnesFirstToolFilesItsReplyUnderTheSecond()
    {
        var s = Arrange();
        Prompt(s, "yes");
        var first = PromptId(s, "yes");
        var head = Asked("u1", "yes") + Says("m1", "Going ahead.") + Calls("m1", "A");
        File.WriteAllText(s.Transcript, head);
        Later(s);
        Prompt(s, "yes");
        var second = PromptId(s, "yes");
        PostToolUse(s, "A");
        File.WriteAllText(s.Transcript, head + Result("A") + Queued("yes") + Says("m2", "Again, then.") + Calls("m2", "B") + Result("B"));
        PostToolUse(s, "B");

        var directs = Replies(s, "direct").ToDictionary(r => Text(r, "prompt_id"), r => Text(r, "text"));
        Assert.Equal(new Dictionary<string, string> { [second] = "Going ahead." }, directs);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ABoundaryNamingTwoEquallyRecentQuestionsOrNoneFilesNothing()
    {
        JsonValue Row(string id, double at, string text, string session = Session) =>
            new JsonObject { ["kind"] = "prompt", ["id"] = id, ["at"] = at, ["session_id"] = session, ["text"] = text };

        Assert.Equal("p2", HierarchyModule.QaQuestionFor([Row("p1", 1, "yes"), Row("p2", 2, "yes")], Session, "yes"));
        Assert.Null(HierarchyModule.QaQuestionFor([Row("p1", 2, "yes"), Row("p2", 2, " yes ")], Session, "yes"));
        // the mod bridge puts its hook context before the prompt it submits
        Assert.Equal("p1", HierarchyModule.QaQuestionFor([Row("p1", 1, "why?")], Session, "ah: context\n\nwhy?"));
        // negatives: another session's row, a text that only ends the same way, and no row at all
        Assert.Null(HierarchyModule.QaQuestionFor([Row("p1", 1, "yes", "other")], Session, "yes"));
        Assert.Null(HierarchyModule.QaQuestionFor([Row("p1", 1, "yes")], Session, "oh yes"));
        Assert.Null(HierarchyModule.QaQuestionFor([], Session, "yes"));
    }

    [Fact]
    public void ALongPromptWithContextBeforeItThatEndsInAnEarlierShortPromptsTextFilesUnderItsOwnCutRow()
    {
        var tail = "\n\nyes";
        var longPrompt = new string('a', 17 * 1024) + tail;
        JsonValue[] rows = [HierarchyModule.QaPromptRow("p1", 1, Session, null, "yes"), HierarchyModule.QaPromptRow("p2", 2, Session, null, longPrompt)];
        Assert.True(rows[1].Member("truncated")!.Value.AsBool());

        Assert.Equal("p2", HierarchyModule.QaQuestionFor(rows, Session, "ah: context\n\n" + longPrompt));
        Assert.Equal("p2", HierarchyModule.QaQuestionFor(rows, Session, longPrompt));
        // negative: with no newer cut row the short prompt's whole text still matches the end
        Assert.Equal("p1", HierarchyModule.QaQuestionFor(rows[..1], Session, "ah: context\n\n" + longPrompt));
        // negative: a cut row matches only at the start or after a blank line
        Assert.Null(HierarchyModule.QaQuestionFor(rows[1..], Session, "x" + longPrompt));
    }

    [Fact]
    public void UnderTheModAPromptWhoseContextTheTypedTestRejectsStillGetsItsDirectRowAndATypedTurnRow()
    {
        var s = Arrange();
        const string asked = "what is the port?";
        Prompt(s, asked);
        // the first prompt after a start carries the Orchestrator directive before it, which may quote a cross-session tag
        var boundary = "AH DIRECTIVE: a peer writes <cross-session-message from=\"x\"> to you\n\n" + asked;
        File.WriteAllText(s.Transcript, Asked("u1", boundary) + Says("m1", "Port 8080.") + Calls("m1", "A") + Result("A") + Says("m2", "Checked."));
        PostToolUse(s, "A");
        Assert.Equal("Port 8080.", Text(Assert.Single(Replies(s, "direct")), "text"));
        Stop(s);

        var turn = Assert.Single(Replies(s));
        Assert.Equal(("typed", PromptId(s, asked)), (Text(turn, "trigger"), Text(turn, "prompt_id")));
        Assert.Single(Replies(s, "direct"));
    }

    [Fact]
    public void ARelayAfterOnlyAHandBackSinceTheParkRestsOnNoUserPrompt()
    {
        var s = Arrange();
        Prompt(s, "ship the fix");
        Later(s);
        var parkedAt = Ah.Core.Time.Timestamps.ToIsoString(s.Services.Clock.UtcNow);
        Later(s);
        Prompt(s, "<agent-message from=\"aa96e7cdc5d731538\">[Subagent hand-back] done</agent-message>");

        // the rule `ah msg decision answer` refuses a relay by (answer_no_user_prompt when it gives null)
        Assert.Null(HierarchyModule.RelayPromptId(s.Services.Hierarchy.Peers.LatestUserPrompt(Session), parkedAt));

        // negative: a prompt the user typed after the park is one a relay rests on
        Later(s);
        Prompt(s, "the first option");
        Assert.Equal(PromptId(s, "the first option"), HierarchyModule.RelayPromptId(s.Services.Hierarchy.Peers.LatestUserPrompt(Session), parkedAt));
    }

    [Fact]
    public void APromptTheModBridgeSubmittedWritesNoQuestionRowAndNoUserPromptRecord()
    {
        var s = Arrange();
        Run(s, "userpromptsubmit-peer-tracking", new JsonObject { ["hook_event_name"] = "UserPromptSubmit", ["prompt"] = "report first", ["plugin_submitted"] = true });

        Assert.DoesNotContain(s.Rows(), static r => Text(r, "kind") == "prompt");
        Assert.Null(s.Services.Hierarchy.Peers.LatestUserPrompt(Session));

        // negative: the same text unmarked is the user's
        Prompt(s, "report first");
        Assert.Single(s.Rows(), static r => Text(r, "kind") == "prompt");
    }

    [Fact]
    public void AMidTurnQuestionGetsItsOwnDirectReplyAndTheTurnRow()
    {
        var s = Arrange();
        Prompt(s, "deploy it");
        const string midTurn = "wait, which region?";
        File.WriteAllText(s.Transcript, Asked("u1", "deploy it") + Says("m1", "Deploying.") + Calls("m1", "A") + Result("A") + Queued(midTurn) + Noise()
            + Says("m2", "Region us-east.") + Calls("m2", "B") + Result("B") + Says("m3", "Deployed."));
        Prompt(s, midTurn);

        Assert.Equal("Region us-east.", FirstStep(s, "B"));
        Stop(s);

        var second = Assert.Single(s.Rows(), r => Text(r, "kind") == "prompt" && Text(r, "text") == midTurn);
        var direct = Assert.Single(Replies(s, "direct"));
        Assert.Equal(("Region us-east.", Text(second, "id")), (Text(direct, "text"), Text(direct, "prompt_id")));
        Assert.Equal(Text(second, "id"), Text(Assert.Single(Replies(s)), "prompt_id"));
    }

    [Fact]
    public void NegativeAMidTurnPeerMessagesFirstStepWritesNothing()
    {
        var s = Arrange();
        File.WriteAllText(s.Transcript, Asked("u1", "deploy it") + Says("m1", "Deploying.") + Calls("m1", "A") + Result("A")
            + Queued("<agent-message from=\"a1\">done</agent-message>", "peer") + Says("m2", "Noted.") + Calls("m2", "B"));

        Assert.Null(FirstStep(s, "B"));
        Assert.Equal("Deploying.", FirstStep(s, "A"));
    }

    [Fact]
    public void AHandBackTurnsReplyJoinsTheLatestRealQuestionAsLaterWithoutTheEarlierTurnsText()
    {
        var s = Arrange();
        const string asked = "ship the fix";
        const string handBack = "<agent-message from=\"aa96e7cdc5d731538\">[Subagent hand-back] done</agent-message>";
        Prompt(s, asked);
        File.WriteAllText(s.Transcript, Asked("u1", asked) + Says("m1", "Dispatched.") + Asked("u2", handBack, meta: true, origin: "peer")
            + Says("m2", "Shipped at abc123.") + Calls("m2", "A") + Result("A") + Says("m3", "Merged."));
        Prompt(s, handBack);
        Stop(s);

        var prompt = Assert.Single(s.Rows(), static r => Text(r, "kind") == "prompt");
        var turn = Assert.Single(Replies(s));
        Assert.Equal(("later", Text(prompt, "id")), (Text(turn, "trigger"), Text(turn, "prompt_id")));
        Assert.Equal("Shipped at abc123.\n\nMerged.", Text(turn, "text"));
        Assert.Empty(Replies(s, "direct"));
    }

    [Fact]
    public void NegativeAMetaEntryWithAnOriginNotSeenOpeningATurnIsPassedOver()
    {
        var s = Arrange();
        File.WriteAllText(s.Transcript, Asked("u1", "go on") + Says("m1", "Step one.") + Calls("m1", "A") + Result("A")
            + Asked("u2", "injected", meta: true, origin: "auto-continuation") + Says("m2", "Step two."));

        var scan = HierarchyModule.QaReplyOf(s.Services.Files, s.Transcript, null, HierarchyModule.QaIsUserTyped);

        Assert.Equal(("go on", "u1"), (scan.Opening, scan.OpeningId));
        Assert.Equal("Step one.", scan.Direct);
        Assert.Equal("Step two.", scan.Final);
    }

    [Fact]
    public void ASecondStopOfTheSameTurnReplacesItsTurnRow()
    {
        var s = Arrange();
        const string asked = "run the suite";
        Prompt(s, asked);
        var head = Asked("u1", asked) + Says("m1", "Running.") + Calls("m1", "A") + Result("A") + Says("m2", "Half done.");
        File.WriteAllText(s.Transcript, head);
        Stop(s);
        File.WriteAllText(s.Transcript, head + Asked("u9", "Stop hook feedback:\nreport first") + Says("m3", "Finished.") + Calls("m3", "B") + Result("B") + Says("m4", "All green."));
        Stop(s);

        var turn = Assert.Single(Replies(s));
        Assert.Equal("Running.\n\nHalf done.\n\nFinished.\n\nAll green.", Text(turn, "text"));
        Assert.Equal("All green.", Text(turn, "final"));
        Assert.Single(Replies(s, "direct"));
    }

    [Fact]
    public void ATypedTurnWithNoToolCallWritesItsDirectRowOnly()
    {
        var s = Arrange();
        const string asked = "what is the port?";
        Prompt(s, asked);
        File.WriteAllText(s.Transcript, Asked("u1", asked) + Says("m1", "It is 8080."));
        Stop(s);

        Assert.Equal("It is 8080.", Text(Assert.Single(Replies(s, "direct")), "text"));
        Assert.Empty(Replies(s));
    }

    [Fact]
    public void ANoToolTurnAStopHookHeldKeepsTheTextSaidAfterTheHold()
    {
        var s = Arrange();
        const string asked = "what is the port?";
        Prompt(s, asked);
        var head = Asked("u1", asked) + Says("m1", "It is 8080.");
        File.WriteAllText(s.Transcript, head);
        Stop(s);
        File.WriteAllText(s.Transcript, head + Asked("u9", "Stop hook feedback:\nsay which config") + Says("m2", "Set in app.json."));
        Stop(s);

        // the second Stop's longer text replaces the direct row; nothing said after the hold is dropped
        Assert.Equal("It is 8080.\n\nSet in app.json.", Text(Assert.Single(Replies(s, "direct")), "text"));
        Assert.Empty(Replies(s));
    }

    [Fact]
    public void NegativeAHandBackWritesNoUserPromptRecordSoTheLatestPromptStaysTheTypedOne()
    {
        var s = Arrange();
        Prompt(s, "ship the fix");
        var typed = s.Services.Hierarchy.Peers.LatestUserPrompt(Session)?.Member("prompt_id")?.AsString();

        Prompt(s, "<agent-message from=\"aa96e7cdc5d731538\">[Subagent hand-back] done</agent-message>");

        Assert.NotNull(typed);
        Assert.Equal(typed, s.Services.Hierarchy.Peers.LatestUserPrompt(Session)?.Member("prompt_id")?.AsString());
    }

    /// <summary>The prompts the ah2-57 rule newly refuses as the user's (spec §7): each passed the typed test before and fails it now.</summary>
    [Theory]
    [InlineData("<agent-message from=\"aa96e7cdc5d731538\">[Subagent hand-back] done</agent-message>", false)]
    [InlineData("[SYSTEM NOTIFICATION - NOT USER INPUT] wake up", false)]
    [InlineData("ah: reminder: 1 unread (1 note). Run `ah msg next`.", false)]
    [InlineData("<<autonomous-loop-dynamic>>", false)]
    [InlineData("ah: You have 2 messages (2 note). Run `ah msg next`.", false)]
    [InlineData("<system-reminder>a</system-reminder>\n", false)]
    [InlineData("<command-message>clear</command-message>\n<command-args></command-args>", false)]
    [InlineData("<local-command-caveat>Caveat: generated by local commands</local-command-caveat>", false)]
    [InlineData("[ah-entry e1 to orchestrator@t]", false)]
    [InlineData("   \n ", false)]
    // negatives: a prompt that only quotes an injected shape later, or answers in plain text, is still the user's
    [InlineData("look at this:\n<agent-message from=\"a1\">quoted</agent-message>\nis it a bug?", true)]
    [InlineData("yes", true)]
    public void TheTypedTestAlsoRequiresTheUserTypedRule(string prompt, bool typed)
    {
        var s = Arrange();
        var advisory = s.Services.Advisory!;
        Assert.Equal(typed, Ah.Hooks.Activity.UserPromptSubmitPeerTracking.IsTypedPrompt(advisory, prompt, prompt, s.Services.Hierarchy.Peers.ParseWrapper(prompt)));
    }

    [Fact]
    public void AReplyInALaterMailTurnJoinsTheOpenThreadAsLater()
    {
        var s = Arrange();
        Prompt(s, "ship it");
        const string mail = "<task-notification>agent finished</task-notification>";
        File.WriteAllText(s.Transcript, User("ship it") + Assistant("Dispatched.") + User(mail) + Assistant("Shipped at abc123."));

        Prompt(s, mail);
        Stop(s);

        var rows = s.Rows();
        var prompt = Assert.Single(rows, static r => Text(r, "kind") == "prompt");
        var reply = Assert.Single(rows, static r => Text(r, "kind") == "reply");
        Assert.Equal("Shipped at abc123.", Text(reply, "text"));
        Assert.Equal("later", Text(reply, "trigger"));
        Assert.Equal(Text(prompt, "id"), Text(reply, "prompt_id"));
    }

    [Fact]
    public void NegativeAMailPromptWritesNoPromptRow()
    {
        var s = Arrange();

        Prompt(s, "<task-notification>agent finished</task-notification>");

        Assert.DoesNotContain(s.Rows(), static r => Text(r, "kind") == "prompt");
    }

    [Theory]
    [InlineData("why did the build fail?", true)]
    [InlineData("look at this:\n<agent-message from=\"a1\">quoted</agent-message>\nis it a bug?", true)]
    [InlineData("first line\n\nsecond line\nthird?", true)]
    [InlineData("<command-name>/review</command-name>\n<command-message>review</command-message>\n<command-args>the auth change</command-args>", true)]
    [InlineData("/review the auth change", true)]
    [InlineData("<system-reminder>ctx</system-reminder>\nwhat now?", true)]
    [InlineData("[ah-entry e1 to orchestrator@t] means what?", true)]
    [InlineData("ah: you have it backwards", true)]
    [InlineData("  <agent-message from=\"aa96e7cdc5d731538\">[Subagent hand-back] The text below is the final report", false)]
    [InlineData("Another Claude session sent a message:\n<agent-message from=\"a1\">hi</agent-message>", false)]
    [InlineData("<cross-session-message from=\"tm-orchestrator\">\n[hierarchy-msg /m/x--request.md]\n</cross-session-message>", false)]
    [InlineData("<task-notification>agent finished</task-notification>", false)]
    [InlineData("<system-reminder>a</system-reminder>\n<system-reminder>b</system-reminder>\n", false)]
    [InlineData("[SYSTEM NOTIFICATION - NOT USER INPUT] wake up", false)]
    [InlineData("The wrangl plugin sent a message:\nah mail for orchestrator@t (1):", false)]
    [InlineData("ah mail for orchestrator@t (1):\n- note from user: hi\n[ah-entry e2]", false)]
    [InlineData("ah: You have 2 messages (2 note). Run `ah msg next`.", false)]
    [InlineData("ah: reminder: 1 unread (1 note). Run `ah msg next`.", false)]
    [InlineData("[ah-entry e1 to orchestrator@t]\n[ah-entry e2 to orchestrator@t]", false)]
    [InlineData("<local-command-caveat>Caveat: generated by local commands</local-command-caveat>", false)]
    [InlineData("<command-name>/clear</command-name>\n<command-message>clear</command-message>\n<command-args></command-args>", false)]
    [InlineData("<<autonomous-loop-dynamic>>", false)]
    [InlineData("   \n ", false)]
    public void APromptIsUserTypedUnlessItStartsAsAnInjectedPrompt(string prompt, bool typed) => Assert.Equal(typed, HierarchyModule.QaIsUserTyped(prompt));

    [Fact]
    public void NegativeASubagentHandBackWritesNoQuestionAndItsTurnIsALaterReplyToNoPrompt()
    {
        var s = Arrange();
        const string handBack = "<agent-message from=\"aa96e7cdc5d731538\">[Subagent hand-back] The report follows:\n  done</agent-message>";
        File.WriteAllText(s.Transcript, User(handBack) + Assistant("Relayed the report."));

        Prompt(s, handBack);
        Stop(s);

        var reply = Assert.Single(s.Rows());
        Assert.Equal(("reply", "later", true), (Text(reply, "kind"), Text(reply, "trigger"), reply.Member("prompt_id")!.Value.IsNull));
    }

    [Fact]
    public void ARowInADirWhereTheSessionLeadsTwoTeamsNamesNoTeamSoBothHistoriesShowIt()
    {
        Ah.Mesh.DeciderBox Box(string team, string dir) => new($"orchestrator@{team}", team, Session) { Dir = dir };

        Assert.Null(Ah.Hooks.Activity.QaLogCapture.TeamOf([Box("a", "/d"), Box("b", "/d"), Box("c", "/e")], "/d"));

        // negative: one team in the dir keeps its name
        Assert.Equal("c", Ah.Hooks.Activity.QaLogCapture.TeamOf([Box("a", "/d"), Box("b", "/d"), Box("c", "/e")], "/e"));
    }

    [Fact]
    public void RowsOlderThanThirtyDaysAreDroppedOnTheNextAppend()
    {
        var s = Arrange();
        var now = s.Rig.Services.Clock.UtcNow.ToUnixTimeMilliseconds();
        string Row(string id, double days) => Line(new JsonObject { ["v"] = 1d, ["kind"] = "prompt", ["id"] = id, ["at"] = now - (days * 86_400_000), ["text"] = id });
        // the history written before the upgrade: the import pass takes it, and a row past the window never shows
        File.WriteAllText(Path.Combine(s.Dir, "qa-log.jsonl"), Row("old", 40) + Row("edge", 29) + Row("fresh", 1));
        using (Ah.StateDb.StateDbModule.Open(s.Rig.Writer.AhHome!, mayCreate: true))
        {
        }

        _ = Ah.StateDb.StateDbModule.ImportHierDirFiles(s.Rig.Writer.AhHome!, s.Dir);

        Prompt(s, "what changed?");

        Assert.Equal(["edge", "fresh", "what changed?"], s.Rows().Select(static r => Text(r, "text")));
    }
}
