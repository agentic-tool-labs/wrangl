using Ah.Core.Json;
using Ah.Platform;

namespace Ah.Cli.Tests;

public sealed partial class RosterTeamVerbTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void ATrustedOrUnknownCodexCwdIsNotReportedAsPreapproved(bool trusted)
    {
        PutHerdrOnPath();
        if (trusted)
        {
            var codexHome = Path.Combine(_sandbox.Home, ".codex");
            Directory.CreateDirectory(codexHome);
            File.WriteAllText(Path.Combine(codexHome, "config.toml"), $"[projects.\"{Project}\"]\ntrust_level = \"trusted\"\n");
        }

        Ah.TestKit.TestHomeDocs.Put(_sandbox.AhHome, Ah.Roster.RosterModule.CodexTrustPreapprovalKey,
            JsonText.Stringify(new JsonObject { ["scopes"] = new JsonArray { new JsonObject { ["path"] = PlatformModule.CreateIo(null).Files.RealPath(Project), ["kind"] = "root" } } }, 0));
        WriteTeam("proj", LiveTeamJson("terminal", string.Empty));

        var (code, stdout, stderr) = Roster("spawn-ad-hoc", "implementor", "--kind", "codex", "--model", "gpt-5", "--team", "proj", "--orchestrator-pid", Self, "--dry-run");

        Assert.True(code == 0, stdout + stderr);
        Assert.Null(JsonText.Parse(stdout).Member("trust"));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("parent")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void APreapprovedCodexStreamDryRunReportsItsCoveringScope(string kind)
    {
        PutHerdrOnPath();
        var tree = MakeWorkTree("s1");
        var gitdir = Path.Combine(Project, ".git", "worktrees", "s1");
        Directory.CreateDirectory(gitdir);
        File.WriteAllText(Path.Combine(gitdir, "gitdir"), Path.Combine(tree, ".git") + "\n");
        WriteTeam("proj", TeamWithStream(tree));
        WriteGrants("enforce");
        var codexHome = Path.Combine(_sandbox.Home, ".codex");
        Directory.CreateDirectory(codexHome);
        File.WriteAllText(Path.Combine(codexHome, "config.toml"), "[projects.\"/elsewhere\"]\ntrust_level = \"trusted\"\n");
        var scope = PlatformModule.CreateIo(null).Files.RealPath(kind == "root" ? Project : _sandbox.Root);
        Ah.TestKit.TestHomeDocs.Put(_sandbox.AhHome, Ah.Roster.RosterModule.CodexTrustPreapprovalKey,
            JsonText.Stringify(new JsonObject { ["v"] = 1, ["scopes"] = new JsonArray { new JsonObject { ["path"] = scope, ["kind"] = kind } } }, 0));

        var (code, stdout, stderr) = Roster("spawn-ad-hoc", "implementor", "--kind", "codex", "--model", "gpt-5", "--team", "proj", "--orchestrator-pid", Self, "--stream", "s1", "--dry-run");

        Assert.True(code == 0, stdout + stderr);
        var result = JsonText.Parse(stdout);
        Assert.Equal("preapproved", result.Member("trust")!.Value.AsString());
        Assert.Equal(scope, result.Member("scope")!.Value.Member("path")!.Value.AsString());
        Assert.Equal(kind, result.Member("scope")!.Value.Member("kind")!.Value.AsString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void APreapprovalForASiblingOrACorruptStoreStillRefusesTheCodexSpawn(bool corrupt)
    {
        var codexHome = Path.Combine(_sandbox.Home, ".codex");
        Directory.CreateDirectory(codexHome);
        File.WriteAllText(Path.Combine(codexHome, "config.toml"), "[projects.\"/elsewhere\"]\ntrust_level = \"trusted\"\n");
        Directory.CreateDirectory(_sandbox.AhHome);
        Directory.CreateDirectory(Project + "-sibling");
        var sibling = PlatformModule.CreateIo(null).Files.RealPath(Project + "-sibling");
        Ah.TestKit.TestHomeDocs.Put(_sandbox.AhHome, Ah.Roster.RosterModule.CodexTrustPreapprovalKey, corrupt ? "{}" :
            JsonText.Stringify(new JsonObject { ["scopes"] = new JsonArray { new JsonObject { ["path"] = sibling, ["kind"] = "root" } } }, 0));

        var (code, stdout, stderr) = CodexDryRun();

        Assert.Equal(2, code);
        Assert.Contains("harness-cwd-untrusted", stdout, StringComparison.Ordinal);
        if (corrupt)
        {
            Assert.Contains("not a pre-approval store", stdout + stderr, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("codex", "stored adapter note", true)]
    [InlineData("codex", null, false)]
    [InlineData("claude", "stored adapter note", false)]
    public void DoctorReportsOnlyStoredCodexAdapterNotes(string kind, string? note, bool expected)
    {
        var member = new JsonObject { ["role"] = "implementor", ["name"] = "proj-implementor", ["kind"] = kind };
        if (note is not null)
        {
            member["adapter_note"] = note;
        }

        WriteTeam("proj", LiveTeamJson("terminal", JsonText.Stringify(member, 0)));

        var (_, stdout, _) = Roster("doctor", "--team", "proj");

        var rows = JsonText.Parse(stdout).Member("rows")!.Value.AsArray().Where(r => r.Member("name")!.Value.AsString() == "codex-adapter").ToList();
        Assert.Equal(expected ? 1 : 0, rows.Count);
        if (expected)
        {
            Assert.Equal("warn", rows[0].Member("status")!.Value.AsString());
            Assert.Equal("proj-implementor: " + note, rows[0].Member("detail")!.Value.AsString());
        }
    }
}
