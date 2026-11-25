using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>spawn-one and spawn-ad-hoc return without waiting for the member to register; a detached confirm-registration waits instead.</summary>
public sealed partial class RosterTeamVerbTests
{
    [Fact]
    public void ASpawnLeavesTheWaitToADetachedConfirmerThatMailsTheSpawnerWhenTheMemberNeverRegisters()
    {
        var mail = new FullMail();
        _eventMail = mail;

        var (code, stdout, stderr) = SpawnBare();

        Assert.True(code == 0, stdout + stderr);
        var line = Assert.Single(_detached);
        Assert.Contains(" confirm-registration ", line, StringComparison.Ordinal);
        Assert.Contains("--to orchestrator@proj", line, StringComparison.Ordinal);
        var member = JsonText.Parse(stdout).Member("member")!.Value;
        var name = member.Member("name")!.Value.AsString();

        var confirmed = Roster("confirm-registration", "--member", name, "--pane", member.Member("transport_id")!.Value.AsString(), "--transport", "tmux", "--to", "orchestrator@proj", "--team", "proj", "--wait-ms", "0");

        Assert.Equal(0, confirmed.Code);
        var sent = Assert.Single(mail.Sent, s => s.Event == "member-unconfirmed");
        Assert.Equal(["orchestrator@proj"], sent.To);
        Assert.Equal(name, sent.Data.Get("member")!.Value.AsString());
        Assert.Contains("did not register", sent.Data.Get("reason")!.Value.AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdHocQaSpawnThatNeverRegistersIsMailedOnceNamingItsPaneAndTheCheckinCommand()
    {
        var mail = new FullMail();
        _eventMail = mail;
        OnTmux();
        WriteTeam("proj", LiveTeamJson("tmux", string.Empty));
        AgeSpawnedRows();

        var (code, stdout, stderr) = Roster("spawn-ad-hoc", "qa", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--allow-main");

        Assert.True(code == 0, stdout + stderr);
        var member = JsonText.Parse(stdout).Member("member")!.Value;
        var name = member.Member("name")!.Value.AsString();
        var pane = member.Member("transport_id")!.Value.AsString();
        var line = Assert.Single(_detached);
        Assert.Contains("--spawned-at " + member.Member("spawned_at")!.Value.AsString(), line, StringComparison.Ordinal);
        string[] confirm = ["confirm-registration", "--member", name, "--pane", pane, "--transport", "tmux", "--to", "orchestrator@proj", "--team", "proj", "--spawned-at", member.Member("spawned_at")!.Value.AsString(), "--wait-ms", "0"];

        Assert.Equal(0, Roster(confirm).Code);
        Assert.Equal(0, Roster(confirm).Code);

        var sent = mail.Sent.Where(s => s.Event == "member-unconfirmed").ToList();
        Assert.Single(sent.Select(s => s.Id).Distinct());
        Assert.Contains(pane, sent[0].Text, StringComparison.Ordinal);
        Assert.Contains("`ah roster checkin`", sent[0].Text, StringComparison.Ordinal);
        Assert.Contains("still queued", sent[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberDismissedBeforeTheWindowEndsIsNotMailedUnconfirmed()
    {
        var mail = new FullMail();
        _eventMail = mail;
        var (code, stdout, stderr) = SpawnBare();
        Assert.True(code == 0, stdout + stderr);
        var member = JsonText.Parse(stdout).Member("member")!.Value;
        WriteTeam("proj", LiveTeamJson("tmux", string.Empty));

        var confirmed = Roster("confirm-registration", "--member", member.Member("name")!.Value.AsString(), "--pane", member.Member("transport_id")!.Value.AsString(), "--transport", "tmux", "--to", "orchestrator@proj", "--team", "proj", "--wait-ms", "0");

        Assert.Equal(0, confirmed.Code);
        Assert.DoesNotContain(mail.Sent, s => s.Event == "member-unconfirmed");
    }

    [Fact]
    public void AMemberThatRegisteredIsNotMailedUnconfirmed()
    {
        var mail = new FullMail();
        _eventMail = mail;
        WriteTeam("proj", LiveTeamJson("tmux", string.Empty));
        Registered(ImplSession, live: true);

        var confirmed = Roster("confirm-registration", "--member", Impl, "--pane", "%9", "--transport", "tmux", "--to", "orchestrator@proj", "--team", "proj", "--wait-ms", "0");

        Assert.Equal(0, confirmed.Code);
        Assert.DoesNotContain(mail.Sent, s => s.Event == "member-unconfirmed");
    }
}
