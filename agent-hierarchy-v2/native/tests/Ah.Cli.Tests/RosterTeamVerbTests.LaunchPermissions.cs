using System.Globalization;
using Ah.Commands;
using Ah.Config;
using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>A spawn or a resume says whether the member got its launch-only rules, and tells whoever can fix it when it did not.</summary>
public sealed partial class RosterTeamVerbTests
{
    private sealed class FullMail : IRosterEventMail
    {
        public List<(string Id, string Event, string Text, JsonObject Data, IReadOnlyList<string> To)> Sent { get; } = [];

        public IReadOnlyList<string> SendEvent(string cwd, string hierarchyDir, string id, string eventName, string text, JsonObject data, IReadOnlyList<string> recipients)
        {
            Sent.Add((id, eventName, text, data, recipients));
            return recipients;
        }
    }

    private static string LaunchTestPluginRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ah.sln")))
        {
            dir = dir.Parent;
        }

        return Path.GetFullPath(Path.Combine(dir!.FullName, ".."));
    }

    /// <summary>Makes the sandbox plugin hold the member surface and the project's asks log hold an effective member-launch consent.</summary>
    private void ConsentToMemberRules(string hierarchyDir, string answer = "Allow")
    {
        _ = Directory.CreateDirectory(Path.Combine(_sandbox.PluginRoot, "policy"));
        File.Copy(Path.Combine(LaunchTestPluginRoot(), "policy", "member-surface.json"), Path.Combine(_sandbox.PluginRoot, "policy", "member-surface.json"), true);
        var q = ConfigModule.MemberLaunchConsentQuestion;
        var asked = "[{\"question\":\"" + q + "\",\"header\":\"Member rules\",\"multiSelect\":false,\"options\":[{\"label\":\"Allow\",\"description\":\"d\"},{\"label\":\"Not now\",\"description\":\"d\"}]}]";
        var at = DateTime.UtcNow.AddMinutes(-5).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        _ = Directory.CreateDirectory(hierarchyDir);
        File.AppendAllText(
            Path.Combine(hierarchyDir, "asks.jsonl"),
            "{\"at\":\"" + at + "\",\"writer\":\"" + ConfigModule.AskRecordWriter + "\",\"session_id\":\"s\",\"questions\":" + asked + ",\"response\":{\"questions\":" + asked
            + ",\"answers\":{\"" + q + "\":\"" + answer + "\"},\"annotations\":{}},\"metadata\":{\"source\":\"ah:R7;consent=member-launch-rules;cv=2\"}}\n");
    }

    private (int Code, string Stdout, string Stderr) SpawnBare()
    {
        OnTmux();
        WriteTeam("proj", LiveTeamJson("tmux", string.Empty));
        AgeSpawnedRows();
        return Roster("spawn-ad-hoc", "implementor", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--allow-main");
    }

    private void OptOutOfMemberRules()
    {
        var file = Path.Combine(_sandbox.Home, ".claude", "ah2", "agent-hierarchy.json");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{\"member_allow\":{\"enabled\":false}}");
    }

    [Fact]
    public void AMemberThatLaunchesWithNoRecordedAnswerGetsItsRulesAndNoWarning()
    {
        var mail = new FullMail();
        _eventMail = mail;
        _ = Directory.CreateDirectory(_sandbox.PluginRoot);
        _ = Directory.CreateDirectory(Path.Combine(_sandbox.PluginRoot, "policy"));
        File.Copy(Path.Combine(LaunchTestPluginRoot(), "policy", "member-surface.json"), Path.Combine(_sandbox.PluginRoot, "policy", "member-surface.json"), true);

        var (code, stdout, stderr) = SpawnBare();

        Assert.True(code == 0, stdout + stderr);
        Assert.False(JsonText.Parse(stdout).AsObject().Get("permissions")!.Value.Member("file")!.Value.IsNull);
        Assert.DoesNotContain("no-consent", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(mail.Sent, s => s.Event == "member-launched-bare");
    }

    [Fact]
    public void AMemberThatOptedOutSaysSoInItsOutputOnStderrAndToTheOrchestrator()
    {
        OptOutOfMemberRules();
        var mail = new FullMail();
        _eventMail = mail;

        var (code, stdout, stderr) = SpawnBare();

        Assert.True(code == 0, stdout + stderr);
        var permissions = JsonText.Parse(stdout).AsObject().Get("permissions")!.Value;
        Assert.True(permissions.Member("file")!.Value.IsNull);
        Assert.Equal("no-consent", permissions.Member("cause")!.Value.AsString());
        Assert.Equal(ConfigModule.LaunchRemedy("no-consent"), permissions.Member("remedy")!.Value.AsString());
        Assert.Contains("permissions: none (no-consent)", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("permissions: none", stdout.Split('\n')[0], StringComparison.Ordinal);
        var sent = Assert.Single(mail.Sent, s => s.Event == "member-launched-bare");
        Assert.Contains(sent.To, a => a.StartsWith("orchestrator@", StringComparison.Ordinal));
        Assert.Equal("no-consent", sent.Data.Get("cause")!.Value.AsString());
    }

    [Fact]
    public void ALaneSpawnThatOptedOutTellsTheTopOrchestratorNotItsOwnLead()
    {
        OptOutOfMemberRules();
        ParentLane(string.Empty);
        var mail = new FullMail();
        _eventMail = mail;

        var (code, stdout, stderr) = Roster(SpawnIntoLane);

        Assert.True(code == 0, stdout + stderr);
        var sent = Assert.Single(mail.Sent, s => s.Event == "member-launched-bare");
        Assert.Equal(["orchestrator@proj"], sent.To);
        Assert.Equal("no-consent", sent.Data.Get("cause")!.Value.AsString());
    }

    [Fact]
    public void ALaneSpawnThatFailsToWriteItsLaunchFileTellsTheLeadAndNotTheOrchestrator()
    {
        ParentLane(string.Empty);
        ConsentToMemberRules(Hierarchy);
        _ = Directory.CreateDirectory(Path.Combine(Hierarchy, "launch", "proj-s1-reviewer.settings.json"));
        var mail = new FullMail();
        _eventMail = mail;

        var (code, stdout, stderr) = Roster(SpawnIntoLane);

        Assert.True(code == 0, stdout + stderr);
        var sent = Assert.Single(mail.Sent, s => s.Event == "member-launched-bare");
        Assert.Equal("write-failed", sent.Data.Get("cause")!.Value.AsString());
        Assert.Equal(["proj-so-s1@proj"], sent.To);
    }

    [Fact]
    public void AMemberThatLaunchesWithItsRulesHasANoneCauseNoWarningAndNoEvent()
    {
        var mail = new FullMail();
        _eventMail = mail;
        ConsentToMemberRules(Hierarchy);

        var (code, stdout, stderr) = SpawnBare();

        Assert.True(code == 0, stdout + stderr);
        var permissions = JsonText.Parse(stdout).AsObject().Get("permissions")!.Value;
        Assert.Equal("none", permissions.Member("cause")!.Value.AsString());
        Assert.True(permissions.Member("remedy")!.Value.IsNull);
        Assert.True(File.Exists(permissions.Member("file")!.Value.AsString()));
        Assert.DoesNotContain("permissions: none", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(mail.Sent, s => s.Event == "member-launched-bare");
    }

    [Fact]
    public void AResumedMemberIsRelaunchedWithItsLaunchFileWhenConsentIsEffective()
    {
        var treeHierarchy = Path.Combine(Project, ".claude", "ah2");
        ConsentToMemberRules(treeHierarchy);
        var record = PendingMemberWithASnapshot(PaneKnowledge.Present);
        _panes.Harness = false;

        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch", Impl);

        Assert.True(code == 0, text);
        Assert.Equal("relaunched", StatusOfMember(result));
        Assert.True(File.Exists(Path.Combine(treeHierarchy, "launch", Impl + ".settings.json")), text);
    }

    [Fact]
    public void AResumedMemberThatOptedOutIsRelaunchedAndWritesNoLaunchFile()
    {
        OptOutOfMemberRules();
        _ = ResumeHome();
        var record = PendingMemberWithASnapshot(PaneKnowledge.Present);
        _panes.Harness = false;

        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch", Impl);

        Assert.True(code == 0, text);
        Assert.Equal("relaunched", StatusOfMember(result));
        Assert.Empty(Directory.GetFiles(_sandbox.Root, Impl + ".settings.json", SearchOption.AllDirectories));
    }
}
