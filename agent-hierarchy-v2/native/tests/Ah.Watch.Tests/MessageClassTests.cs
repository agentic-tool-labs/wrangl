using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>
/// The served <c>class</c> of a message, committed as <c>Goldens/message-classes.json</c>. The web test (<c>tests/web/msg-filter.test.mjs</c>) checks the
/// classes the mock serves against this file, so the mock mirrors the rule without a JS copy of it.
/// </summary>
public sealed class MessageClassTests
{
    private const string Relay = "Another Claude session sent a message:\n<cross-session-message from=\"uds:/tmp/1.sock\">\nah mail from ";
    private const string Plugin = "The ah2 plugin sent a message:\nah mail for member@team (1):\n";

    private static readonly (string Text, string Class)[] Texts =
    [
        ("Please make the filter the default", MessageClass.Work),
        ("[hierarchy-msg 20261018-01 request] Build step 0", MessageClass.Work),
        (Relay + "orchestrator@team to member@team (brief): [hierarchy-msg 20261018-01] read it", MessageClass.Work),
        (Relay + "member@team to orchestrator@team (report): [hierarchy-msg 20261018-01 response]", MessageClass.Work),
        (Plugin + "- brief from orchestrator@team", MessageClass.Work),
        (Plugin + "- report from member@team", MessageClass.Work),
        (Plugin + "- note from member@team", MessageClass.Work),
        ("Another Claude session sent a message:\nCan you look at the failing test?", MessageClass.Work),
        ("A kind nobody has met yet", MessageClass.Work),
        (Relay + "member@team to orchestrator@team (note): QUESTION: which branch?", MessageClass.NeedsYou),
        (Plugin + "- note from member@team: QUESTION: which branch?", MessageClass.NeedsYou),
        (Plugin + "- event member-blocked", MessageClass.NeedsYou),
        (Plugin + "- event member-stuck-escalated", MessageClass.NeedsYou),
        (Plugin + "- event api-failed: ah: tell the user in one line", MessageClass.NeedsYou),
        (Plugin + "- event user-ask", MessageClass.NeedsYou),
        (Plugin + "- event mail-unacked", MessageClass.NeedsYou),
        (Plugin + "- event delivery-failed", MessageClass.NeedsYou),
        (Relay + "ah@team to orchestrator@team (note): [link 1 from x]\nQUESTION: which branch?", MessageClass.NeedsYou),
        (Relay + "ah@team to orchestrator@team (event): member-blocked [ah-entry 1]", MessageClass.NeedsYou),
        (Relay + "member@team to orchestrator@team (note): halfway there", MessageClass.Internal),
        (Plugin + "- note from member@team: halfway there", MessageClass.Internal),
        (Plugin + "- event resume", MessageClass.Internal),
        (Plugin + "- event relay-needed: ah: mail entry 1 was not picked up", MessageClass.Internal),
        (Relay + "ah@team to member@team (event): ah: your brief is overdue", MessageClass.Work),
        (Relay + "ah@team to orchestrator@team (event): resume [ah-entry 1]", MessageClass.Internal),
        (Plugin + "- event a-new-event-nobody-listed", MessageClass.Work),
        (Plugin + "- event resume-later", MessageClass.Work),
        (Plugin + "- event member-waiting", MessageClass.Work),
        (Plugin + "- event job-overdue: job 1 is past its ETA\n- event resume", MessageClass.Internal),
        ("ah: why did this fire?", MessageClass.Work),
        ("ah: You have 2 messages. Run ah msg next.", MessageClass.Work),
        ("Stop hook feedback: ah: You have 2 messages. Run ah msg next.", MessageClass.Internal),
        ("<task-notification>\n<task-id>a1</task-id>\n<result>the report</result>\n</task-notification>", MessageClass.Work),
        ("<task-notification>\n<task-id>a1</task-id>\n<result>This agent's report was delivered to you as a message from \"x\".</result>\n</task-notification>", MessageClass.Internal),
        ("[Cross-session idle notice] \"uds:/tmp/1.sock\" is idle now", MessageClass.Internal),
        ("[Cross-session delivery notice] held for approval", MessageClass.Internal),
        ("<task-notification>\n<task-id>a1</task-id>\n</task-notification>", MessageClass.Internal),
        ("<system-reminder>\nhook text\n</system-reminder>", MessageClass.Internal),
        ("<local-command-caveat>The command below</local-command-caveat>", MessageClass.Internal),
        ("<local-command-stdout>Compacted</local-command-stdout>", MessageClass.Internal),
        ("Base directory for this skill: /skills/frontend-design", MessageClass.Internal),
        ("This session is being continued from a previous conversation", MessageClass.Internal),
        ("[ah-nudge] NOT DONE: entry 1 was released", MessageClass.Internal),
    ];

    private static readonly (string? Slug, string Class)[] Slugs =
    [
        ("ui-msg-filter", MessageClass.Work),
        ("release-notes", MessageClass.Work),
        ("alarms-b", MessageClass.Work),
        ("lnk-fix", MessageClass.Work),
        (null, MessageClass.Work),
        ("pipeline-run-anchor", MessageClass.Internal),
        ("release", MessageClass.Internal),
        ("checkpoint", MessageClass.Internal),
        ("ln-20261021-094825-15ri", MessageClass.Internal),
    ];

    [Fact]
    public void AUserTextHasTheClassOfWhoItIsFor()
    {
        foreach (var (text, expected) in Texts)
        {
            Assert.True(expected == MessageClass.OfUserText(text), text);
        }
    }

    [Fact]
    public void AnExchangeIsInternalOnlyByItsWholeReservedSlug()
    {
        foreach (var (slug, expected) in Slugs)
        {
            Assert.Equal(expected, MessageClass.OfExchange(slug));
        }
    }

    [Fact]
    public void OnlyAUserTextEntryCarriesAClass()
    {
        var parser = new TranscriptParser(new TestUsage());
        TranscriptEntry Parse(string json) => parser.Parse(System.Text.Encoding.UTF8.GetBytes(json), 0, json.Length + 1);

        var internalText = Parse("{\"type\":\"user\",\"message\":{\"content\":\"<system-reminder>x</system-reminder>\"}}");
        Assert.Equal(MessageClass.Internal, internalText.Class);
        Assert.Equal("internal", internalText.ToJson().Member("class")?.AsString());
        Assert.Null(Parse("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"ah: hi\"}]}}").ToJson().Member("class"));
        Assert.Null(Parse("{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t\",\"content\":\"x\"}]}}").ToJson().Member("class"));
    }

    [Fact]
    public void TheClassesAreTheCommittedGoldenTheWebTestReads()
    {
        var texts = new JsonObject();
        foreach (var (text, cls) in Texts)
        {
            texts[text] = cls;
        }

        var slugs = new JsonObject();
        foreach (var (slug, cls) in Slugs.Where(static s => s.Slug is not null))
        {
            slugs[slug!] = cls;
        }

        ModelGolden.Check(new JsonObject { ["texts"] = JsonValue.FromObject(texts), ["slugs"] = JsonValue.FromObject(slugs) }, "message-classes");
    }
}
