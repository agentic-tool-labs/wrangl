using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Watch.Tests;

/// <summary>The board audit mail (b284): a disagreeing set mails once, the same set again mails nothing, a different set mails, an empty one never does.</summary>
public sealed class BoardAuditTests
{
    [Fact]
    public void ADisagreeingSetMailsOnceAndTheSameSetAgainMailsNothing()
    {
        var team = new StallTeam("t", "/h", "/cwd", "orchestrator@t", "sid");
        var box = new List<JsonValue>();
        var sent = new List<WatchMail>();
        void Send(WatchMail m)
        {
            sent.Add(m);
            box.Add(JsonValue.FromObject(new JsonObject { ["kind"] = "event", ["event"] = m.Event, ["data"] = m.Data }));
        }

        ChecklistAuditFinding[] set = [new("b1", "main", "dispatched", "done", "ship abc at 2026-11-07T00:00:00Z", false)];
        Assert.True(BoardAudit.Mail(team, set, box, Send));
        Assert.False(BoardAudit.Mail(team, set, box, Send));
        Assert.Single(sent);
        Assert.Contains("main/b1  stored dispatched  expected done", sent[0].Text, StringComparison.Ordinal);

        // nearest non-matches: one changed field is a new set and mails; no findings mails nothing
        Assert.True(BoardAudit.Mail(team, [set[0] with { Expected = "reported" }], box, Send));
        Assert.False(BoardAudit.Mail(team, [], box, Send));
        Assert.Equal(2, sent.Count);
    }
}
