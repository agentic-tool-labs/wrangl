using System.Diagnostics;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>
/// Hooks run by direct exec of the binary, as hooks.json does at cutover: the parent pid a hook records is the calling process, and
/// a registration it writes carries the start time of that process. The Node shim sits between the caller and the binary only in
/// the test lane, where the binary's parent is the shim.
/// </summary>
public sealed partial class DirectExecParentPidTests
{
    private sealed record Run(int ExitCode, string Stdout, string Stderr);

    /// <summary>A project folder that is a git checkout, so its hierarchy directory is <c>.claude/ah2</c> inside it.</summary>
    private static string Checkout(UnitSandbox sandbox)
    {
        var proj = sandbox.Root + "/proj";
        Directory.CreateDirectory(proj);
        var git = new ProcessStartInfo("git") { WorkingDirectory = proj, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "init", "-q", "-b", "main" })
        {
            git.ArgumentList.Add(arg);
        }

        using var init = Process.Start(git)!;
        init.WaitForExit(30_000);
        Assert.Equal(0, init.ExitCode);
        return proj;
    }

    private static Run RunHook(UnitSandbox sandbox, string proj, string unit, JsonObject evt, IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        var binary = Path.Combine(AppContext.BaseDirectory, "ahd.dll");
        Assert.True(File.Exists(binary), $"the test host does not carry {binary}");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = proj,
        };
        foreach (var arg in new[] { binary, "hook", unit })
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment.Clear();
        start.Environment["HOME"] = sandbox.Home;
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        start.Environment["AH_HOME"] = sandbox.Root + "/ah";
        start.Environment["AH_DAEMON"] = "off";
        start.Environment["AH_TEST"] = "1";
        start.Environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? string.Empty;
        foreach (var pair in extraEnvironment ?? new Dictionary<string, string>())
        {
            start.Environment[pair.Key] = pair.Value;
        }

        using var process = Process.Start(start)!;
        process.StandardInput.Write(JsonText.Stringify(evt, 0));
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(120_000), "the hook did not exit");
        return new Run(process.ExitCode, stdout, stderr);
    }

    [Fact]
    public void AHookRunDirectlyRecordsTheCallingProcessAsItsParent()
    {
        var sandbox = UnitSandbox.Create();
        var proj = Checkout(sandbox);
        var hierarchy = proj + "/.claude/ah2";
        Directory.CreateDirectory(hierarchy + "/msgs");
        Directory.CreateDirectory(sandbox.Home + "/.claude/ah2");
        var evt = new JsonObject
        {
            ["hook_event_name"] = "SessionEnd",
            ["session_id"] = "sess-direct",
            ["cwd"] = proj,
            ["agent_type"] = "wrangl:implementor",
            ["reason"] = "other",
        };

        var run = RunHook(sandbox, proj, "sessionend-roster", evt, new Dictionary<string, string> { ["AH_MEMBER_NAME"] = "tm-implementor" });

        Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}: {run.Stdout} {run.Stderr}");
        var peers = RosterRows(sandbox, hierarchy);
        var down = peers.Select(JsonText.Parse).Single(r => r.Member("status") is { IsText: true } s && s.AsString() == "down");
        Assert.Equal(Environment.ProcessId, (int)down.Member("pid")!.Value.AsNumber());
    }

    [Fact]
    public void AHookPathSessionStartWhoseParentIsNotAClaudeHarnessWritesNothing()
    {
        var sandbox = UnitSandbox.Create();
        var proj = Checkout(sandbox);
        var hierarchy = proj + "/.claude/ah2";
        Directory.CreateDirectory(hierarchy + "/msgs");
        Directory.CreateDirectory(hierarchy + "/teams");
        Directory.CreateDirectory(sandbox.Home + "/.claude/ah2");
        File.WriteAllText(proj + "/.claude/ah2/agent-hierarchy.json", """{"version":1}""");
        File.WriteAllText(
            hierarchy + "/teams/tm.json",
            """{"team_id":"t-tm","mesh":{"policy":"open"},"orchestrator":{"pid":1,"session_id":"sess-orch"},"members":[{"role":"implementor","name":"tm-implementor","transport_id":"p-tm-implementor"}]}""");
        var evt = new JsonObject
        {
            ["hook_event_name"] = "SessionStart",
            ["session_id"] = "sess-direct",
            ["cwd"] = proj,
            ["agent_type"] = "wrangl:implementor",
            ["source"] = "startup",
        };

        var run = RunHook(sandbox, proj, "sessionstart", evt, new Dictionary<string, string> { ["HERDR_PANE_ID"] = "p-tm-implementor" });

        Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}: {run.Stdout} {run.Stderr}");
        Assert.Empty(run.Stdout);
        Assert.False(File.Exists(sandbox.Root + "/ah/registrations/sess-direct.json"));
        Assert.Empty(RosterRows(sandbox, hierarchy));
        Assert.Null(Ah.TestKit.TestHomeDocs.Text(sandbox.Root + "/ah", Ah.TestKit.TestHomeDocs.SessionRoles));
    }

    /// <summary>The roster rows of <paramref name="hierarchy"/> in the hook runs' home (per-dir-files-to-db S7: the roster_records table).</summary>
    private static string[] RosterRows(UnitSandbox sandbox, string hierarchy) => Ah.TestKit.TestPeers.Lines(sandbox.Root + "/ah", hierarchy);

    [Theory]
    [InlineData("UserPromptSubmit", true, true)]
    [InlineData("Stop", true, true)]
    [InlineData("PostToolUse", true, false)]
    [InlineData("UserPromptSubmit", false, false)]
    public void AFalseDownIsHealedOnlyByAPromptOrStopFromTheProcessThatRegistered(string eventName, bool registeredByCaller, bool healed)
    {
        var sandbox = UnitSandbox.Create();
        var proj = Checkout(sandbox);
        var hierarchy = proj + "/.claude/ah2";
        Directory.CreateDirectory(hierarchy + "/msgs");
        Directory.CreateDirectory(sandbox.Home + "/.claude/ah2");
        Ah.TestKit.TestHomeDocs.Put(
            sandbox.Root + "/ah",
            Ah.TestKit.TestHomeDocs.SessionRoles,
            """{"version":1,"sessions":{"sess-heal":{"role":"implementor","at":"2026-01-01T00:00:00.000Z"}}}""");
        var pid = registeredByCaller ? Environment.ProcessId : 1;
        var peers = hierarchy + "/peers.jsonl";

        // the hook home's store exists, and the import pass takes this file once it is planted (files-off-disk §3)
        using (Ah.StateDb.StateDbModule.Open(sandbox.Root + "/ah", mayCreate: true))
        {
        }

        File.WriteAllLines(
            peers,
            [
                $$"""{"type":"peer","status":"up","role":"implementor","session_id":"sess-heal","pid":{{pid}},"ppid":{{pid}},"cwd":"{{proj}}","ts":"2026-01-01T00:00:00.000Z"}""",
                $$"""{"type":"peer","status":"down","role":"implementor","session_id":"sess-heal","pid":{{pid}},"cwd":"{{proj}}","ts":"2026-01-01T00:01:00.000Z"}""",
            ]);
        _ = Ah.StateDb.StateDbModule.ImportHierDirFiles(sandbox.Root + "/ah", hierarchy);
        var evt = new JsonObject { ["hook_event_name"] = eventName, ["session_id"] = "sess-heal", ["cwd"] = proj };
        if (eventName == "PostToolUse")
        {
            evt["tool_name"] = "Read";
            evt["tool_input"] = new JsonObject { ["file_path"] = "/x" };
            evt["tool_response"] = new JsonObject();
        }

        var run = RunHook(sandbox, proj, "activity", evt, new Dictionary<string, string> { ["AH_MEMBER_NAME"] = "tm-implementor" });

        Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}: {run.Stdout} {run.Stderr}");
        var rows = RosterRows(sandbox, hierarchy);
        var expected = healed ? 3 : 2;
        var errorLog = sandbox.Home + "/.claude/ah2/hierarchy/hook-errors.jsonl";
        Assert.True(
            rows.Length == expected,
            $"expected {expected} roster rows, got {rows.Length}; stderr: {run.Stderr}; hook errors: {(File.Exists(errorLog) ? File.ReadAllText(errorLog) : "none")}");
        if (healed)
        {
            var last = JsonText.Parse(rows[^1]);
            Assert.Equal("up", last.Member("status")!.Value.AsString());
            Assert.Equal(pid, (int)last.Member("pid")!.Value.AsNumber());
        }
    }
}
