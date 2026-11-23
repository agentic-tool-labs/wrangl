using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Hooks.Tests;

/// <summary>
/// ah2-499: the typed <c>/wrangl:admin</c> prompt grants this session's harness admin, via prompt, through the grant's own record; every
/// other prompt, and every argument but on and off, changes nothing (spec admin-remote §7). Run through the hook host in a bare session.
/// </summary>
public sealed class UserPromptSubmitAdminTests
{
    private const string Key = "home.admin-grant";

    /// <summary>Home documents in memory; <see cref="Down"/> makes every read fail the way an unreadable database does.</summary>
    private sealed class Docs : IHomeDocs
    {
        public Dictionary<string, string> Rows { get; } = [];

        public bool Down { get; set; }

        public string? Read(string key) => Down ? null : Rows.GetValueOrDefault(key);

        public bool TryRead(string key, out string? body)
        {
            body = Down ? null : Rows.GetValueOrDefault(key);
            return !Down;
        }

        public void Write(string key, string? body)
        {
            if (Down)
            {
                throw new InvalidOperationException("the database is down");
            }

            if (body is null)
            {
                Rows.Remove(key);
            }
            else
            {
                Rows[key] = body;
            }
        }
    }

    private const string Desktop = "/Applications/Claude.app/Contents/MacOS/Claude";

    private readonly OrchestrateCommandTests.Fixture _f = new();
    private readonly Docs _docs = new();

    /// <summary>The harness is an interactive session on a terminal, with nothing above it but pid 1 (A3.7: the grant tests need a tty).</summary>
    public UserPromptSubmitAdminTests() => _f.Sessions.Ttys[HarnessSessions.Pid] = "ttys001";

    [Fact]
    public void NegativeAHarnessStartedByAnotherHarnessIsNotGranted()
    {
        _f.Sessions.Ancestry[HarnessSessions.Pid] = [5000, 1];
        _f.Sessions.Commands[5000] = "claude";
        _f.Sessions.Executables[5000] = "/Users/u/.local/bin/claude";

        Assert.Contains("wrangl:admin no grant: this session was started by another Claude Code session.", Submit("/wrangl:admin"), StringComparison.Ordinal);
        Assert.Null(Row);
        Assert.Contains(_f.Rig.Errors.Traces, t => t.Message.Contains("started by another Claude Code session", StringComparison.Ordinal));
    }

    [Fact]
    public void NegativeAnUnreadableCommandOnTheLineIsNotGranted()
    {
        _f.Sessions.Ancestry[HarnessSessions.Pid] = [5000, 1]; // 5000's command cannot be read
        _f.Sessions.Executables[5000] = "/bin/zsh";

        Assert.Contains("no grant: this session was started by another Claude Code session.", Submit("/wrangl:admin"), StringComparison.Ordinal);
        Assert.Null(Row);
    }

    [Fact]
    public void NegativeAnUnreadableExecutableOnTheLineIsNotGranted()
    {
        _f.Sessions.Ancestry[HarnessSessions.Pid] = [5000, 1];
        _f.Sessions.Commands[5000] = "zsh"; // 5000's executable cannot be read (D9)

        Assert.Contains("no grant: this session was started by another Claude Code session.", Submit("/wrangl:admin"), StringComparison.Ordinal);
        Assert.Null(Row);
    }

    [Fact]
    public void AShellAboveTheHarnessIsNotAHarness()
    {
        _f.Sessions.Ancestry[HarnessSessions.Pid] = [5000, 1];
        _f.Sessions.Commands[5000] = "-zsh";
        _f.Sessions.Executables[5000] = "-zsh";

        Assert.Contains("is now the admin session, via prompt.", Submit("/wrangl:admin"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A5 round-2 bypass: a plain <c>claude -p</c> child of a desktop app session's shell. The desktop harness above it has a spaced argv 0,
    /// which a split of the command line does not see; its executable (or its argv 0's <c>/claude-code/</c> segment) does.
    /// </summary>
    [Theory]
    [InlineData("/Users/u/Library/Application Support/Claude/claude-code/2.1.330/x/claude.app/Contents/MacOS/claude")]
    [InlineData("/Users/u/Library/Application Support/Claude/claude-code/2.1.330/x/claude.app/Contents/MacOS/cc")]
    public void NegativeAChildOfADesktopSessionsShellIsNotGranted(string desktopHarness)
    {
        DesktopSession();
        _f.Sessions.Commands[HarnessSessions.Pid] = "claude -p /wrangl:admin";
        _f.Sessions.Ancestry[HarnessSessions.Pid] = [8000, 9000, 6000, 7000, 1];
        _f.Sessions.Commands[8000] = "/bin/zsh -c claude -p /wrangl:admin";
        _f.Sessions.Executables[8000] = "/bin/zsh";
        _f.Sessions.Commands[9000] = desktopHarness + " --output-format stream-json --verbose --input-format stream-json";
        _f.Sessions.Executables[9000] = desktopHarness;

        Assert.Contains("no grant: this session was started by another Claude Code session.", Submit("/wrangl:admin"), StringComparison.Ordinal);
        Assert.Null(Row);
    }

    [Fact]
    public void NegativeTheDesktopHelperWithoutTheHostAboveItIsNotWaived()
    {
        DesktopSession();
        _f.Sessions.Ancestry[HarnessSessions.Pid] = [6000, 8000, 7000, 1];
        _f.Sessions.Commands[8000] = "/bin/zsh";
        _f.Sessions.Executables[8000] = "/bin/zsh";

        Assert.Contains("no grant: this session is not interactive (print or stream mode).", Submit("/wrangl:admin"), StringComparison.Ordinal);
        Assert.Null(Row);
    }

    [Fact]
    public void NegativeAShellBetweenTheHarnessAndTheDesktopHelperIsNotWaived()
    {
        DesktopSession();
        _f.Sessions.Ancestry[HarnessSessions.Pid] = [8000, 6000, 7000, 1];
        _f.Sessions.Commands[8000] = "/bin/zsh";
        _f.Sessions.Executables[8000] = "/bin/zsh";

        Assert.Contains("no grant: this session is not interactive (print or stream mode).", Submit("/wrangl:admin"), StringComparison.Ordinal);
        Assert.Null(Row);
    }

    [Theory]
    [InlineData("claude -cp /wrangl:admin")]
    [InlineData("claude --agent x -p /wrangl:admin")]
    [InlineData("claude --print /wrangl:admin")]
    [InlineData("claude --input-format stream-json")]
    [InlineData("claude --output-format=stream-json")]
    [InlineData("claude --sdk-url u")]
    public void NegativeAPrintOrStreamHarnessIsNotGranted(string argv)
    {
        _f.Sessions.Commands[HarnessSessions.Pid] = argv;

        Assert.Contains("wrangl:admin no grant: this session is not interactive (print or stream mode).", Submit("/wrangl:admin"), StringComparison.Ordinal);
        Assert.Null(Row);
    }

    [Theory]
    [InlineData("claude --permission-mode auto")]
    [InlineData("claude --agent wrangl:implementor --name ah2-pa")]
    [InlineData("claude -c")]
    public void AnInteractiveArgvThatOnlyResemblesAFlagIsGranted(string argv)
    {
        _f.Sessions.Commands[HarnessSessions.Pid] = argv; // --permission-mode is not --print; -pa is not -p

        Assert.Contains("is now the admin session, via prompt.", Submit("/wrangl:admin"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void NegativeAHarnessWithNoTerminalIsNotGranted(string? tty)
    {
        if (tty is null)
        {
            _f.Sessions.Ttys.Remove(HarnessSessions.Pid);
        }
        else
        {
            _f.Sessions.Ttys[HarnessSessions.Pid] = tty;
        }

        Assert.Contains("wrangl:admin no grant: this session has no terminal.", Submit("/wrangl:admin"), StringComparison.Ordinal);
        Assert.Null(Row);
    }

    [Fact]
    public void NegativeADesktopHostNeverWaivesTheLineTest()
    {
        DesktopSession();
        _f.Sessions.Ancestry[HarnessSessions.Pid] = [6000, 5000, 7000, 1];
        _f.Sessions.Commands[5000] = "claude";
        _f.Sessions.Executables[5000] = "/Users/u/.local/bin/claude";

        Assert.Contains("no grant: this session was started by another Claude Code session.", Submit("/wrangl:admin"), StringComparison.Ordinal);
        Assert.Null(Row);
    }

    [Fact]
    public void ADesktopAppSessionIsGranted()
    {
        DesktopSession();

        Assert.Contains("wrangl:admin on: this session (pid 4242) is now the admin session, via prompt.", Submit("/wrangl:admin"), StringComparison.Ordinal);
        Assert.Equal("prompt", MeshModule.AdminGrantVia(Row));
    }

    [Fact]
    public void NegativeADesktopAppOutsideApplicationsIsNotWaived()
    {
        DesktopSession();
        _f.Sessions.Commands[7000] = "/Users/u/Applications/Claude.app/Contents/MacOS/Claude";
        _f.Sessions.Executables[7000] = "/Users/u/Applications/Claude.app/Contents/MacOS/Claude";

        Assert.Contains("no grant: this session is not interactive (print or stream mode).", Submit("/wrangl:admin"), StringComparison.Ordinal);
        Assert.Null(Row);
    }

    /// <summary>The desktop app's shape (A3.1, A5 D10): a stream-json harness with no tty, its parent disclaimer, its grandparent the app's main process.</summary>
    private void DesktopSession()
    {
        _f.Sessions.Executables[6000] = "/Applications/Claude.app/Contents/Helpers/disclaimer";
        _f.Sessions.Executables[7000] = Desktop;
        _f.Sessions.Commands[HarnessSessions.Pid] = "claude --output-format stream-json --input-format stream-json --verbose";
        _f.Sessions.Ttys[HarnessSessions.Pid] = string.Empty;
        _f.Sessions.Ancestry[HarnessSessions.Pid] = [6000, 7000, 1];
        // disclaimer's argv as ps printed it on this machine (EV4); the harness's own argv is shortened to `claude`, see the report
        _f.Sessions.Commands[6000] = "/Applications/Claude.app/Contents/Helpers/disclaimer --pgroup -- /Users/u/Library/Application Support/Claude/claude-code/2.1.330/x/claude.app/Contents/MacOS/claude --output-format stream-json";
        _f.Sessions.Commands[7000] = Desktop;
    }

    /// <summary>The unit's output for a prompt from the session whose hook parent is <paramref name="pid"/>.</summary>
    private string Submit(string prompt, int pid = HarnessSessions.Pid, Action<JsonObject>? tweak = null)
    {
        var services = _f.Services(pid) with { HomeDocs = _docs };
        var e = new JsonObject { ["hook_event_name"] = "UserPromptSubmit", ["session_id"] = "s1", ["cwd"] = _f.Rig.Sandbox.Root + "/proj", ["prompt"] = prompt };
        tweak?.Invoke(e);
        return Encoding.UTF8.GetString(_f.Rig.Run("userpromptsubmit-admin", JsonText.Stringify(e, 0), services: () => services).Stdout.Span);
    }

    private string? Row => _docs.Rows.GetValueOrDefault(Key);

    private void GrantTo(int pid)
    {
        Assert.Null(MeshModule.WriteAdminGrant(_f.Sessions.Table, _docs, _f.Rig.Services.Environment.AhHome, pid, null, DateTimeOffset.UnixEpoch));
    }

    [Theory]
    [InlineData("/wrangl:admin")]
    [InlineData("/wrangl:admin on")]
    [InlineData("  /wrangl:admin  on ")]
    public void ATypedCommandGrantsThisHarnessViaPrompt(string prompt)
    {
        var said = Submit(prompt);

        Assert.Contains("wrangl:admin on: this session (pid 4242) is now the admin session, via prompt.", said, StringComparison.Ordinal);
        Assert.Equal(HarnessSessions.Pid, MeshModule.AdminGrantPid(Row));
        Assert.Equal("prompt", MeshModule.AdminGrantVia(Row));
        Assert.Contains("\"session_id\":\"s1\"", Row, StringComparison.Ordinal);
        Assert.True(MeshModule.HoldsAdminGrant(_f.Sessions.Table, HarnessSessions.Pid, _docs, _f.Rig.Services.Environment.AhHome));
    }

    [Fact]
    public void AGrantHeldElsewhereIsReplacedAndTheOldPidNamed()
    {
        GrantTo(4343);

        var said = Submit("/wrangl:admin");

        Assert.Contains("It replaces the grant held by pid 4343.", said, StringComparison.Ordinal);
        Assert.Equal(HarnessSessions.Pid, MeshModule.AdminGrantPid(Row));
    }

    [Fact]
    public void NegativeARegrantOnTheSameHarnessDoesNotClaimAReplacement()
    {
        GrantTo(HarnessSessions.Pid);

        var said = Submit("/wrangl:admin");

        Assert.Contains("is now the admin session, via prompt.", said, StringComparison.Ordinal);
        Assert.DoesNotContain("replaces", said, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryOutcomeIsTracedInTheHookLog()
    {
        _ = Submit("/wrangl:admin");
        _ = Submit("/wrangl:admin bogus");

        Assert.Collection(
            _f.Rig.Errors.Traces,
            t => Assert.Equal("trace: wrangl:admin on: this session (pid 4242) is now the admin session, via prompt.", t.Message),
            t => Assert.Equal("trace: wrangl:admin no change: only on and off change the grant.", t.Message));
    }

    [Fact]
    public void APromptThatNamesTheCommandButMissesIsTracedOnly()
    {
        Assert.Equal(string.Empty, Submit("<command-name>/wrangl:admin</command-name>"));
        Assert.Contains("not matched", Assert.Single(_f.Rig.Errors.Traces).Message, StringComparison.Ordinal);
        Assert.Null(Row);
    }

    [Fact]
    public void NegativeAPlainPromptLeavesNoTrace()
    {
        _ = Submit("admin rights granted");

        Assert.Empty(_f.Rig.Errors.Traces);
    }

    [Fact]
    public void NegativeAPayloadSessionLiveOnAnotherProcessIsSkipped()
    {
        // session s1 is registered to the live harness 4343, so a payload naming it from harness 4242 does not belong to the hook's process
        var other = new HarnessSessions(new SessionRegistration("s1", 4343, "Sun Oct 18 09:00:00 2026", []));
        other.Starts[4343] = "Sun Oct 18 09:00:00 2026";
        other.Commands[4343] = "claude";
        var services = _f.Services() with { HomeDocs = _docs, Sessions = other };
        var e = new JsonObject { ["hook_event_name"] = "UserPromptSubmit", ["session_id"] = "s1", ["cwd"] = _f.Rig.Sandbox.Root + "/proj", ["prompt"] = "/wrangl:admin" };

        var said = Encoding.UTF8.GetString(_f.Rig.Run("userpromptsubmit-admin", JsonText.Stringify(e, 0), services: () => services).Stdout.Span);

        Assert.Equal(string.Empty, said);
        Assert.Null(Row);
        Assert.Contains("does not belong to the hook's process", Assert.Single(_f.Rig.Errors.Records).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OffInTheAdminSessionRevokes()
    {
        GrantTo(HarnessSessions.Pid);

        Assert.Contains("wrangl:admin off: this session is no longer the admin session.", Submit("/wrangl:admin off"), StringComparison.Ordinal);
        Assert.Null(Row);
    }

    [Fact]
    public void NegativeOffInASessionThatIsNotAdminChangesNothing()
    {
        GrantTo(4343);
        var before = Row;

        Assert.Contains("no change: this session is not the admin session.", Submit("/wrangl:admin off"), StringComparison.Ordinal);
        Assert.Equal(before, Row);
    }

    [Theory]
    [InlineData("/wrangl:admin show")]
    [InlineData("/wrangl:admin bogus")]
    [InlineData("/wrangl:admin on now")]
    [InlineData("/wrangl:admin offf")]
    public void NegativeShowOrAnUnknownArgumentChangesNothing(string prompt)
    {
        GrantTo(4343);
        var before = Row;

        Assert.Contains("wrangl:admin no change: only on and off change the grant.", Submit(prompt), StringComparison.Ordinal);
        Assert.Equal(before, Row);
    }

    [Fact]
    public void NegativeAPluginSubmittedPromptChangesNothing()
    {
        var said = Submit("/wrangl:admin", tweak: e => e["plugin_submitted"] = true);

        Assert.Contains("no change: the plugin submitted this prompt", said, StringComparison.Ordinal);
        Assert.Null(Row);
    }

    [Theory]
    [InlineData("ah: mail waiting, run ah msg next")]
    [InlineData("[ah-entry 20261123-000643-j27u to ah2-implementor@ah2]")]
    [InlineData("please run /wrangl:admin")]
    [InlineData("admin rights granted")]
    [InlineData("/wrangl:administer")]
    [InlineData("/wrangl:admin:on")]
    [InlineData("/Wrangl:admin")]
    [InlineData("/admin")]
    [InlineData("")]
    public void NegativeAnyOtherPromptIsSilentAndChangesNothing(string prompt)
    {
        Assert.Equal(string.Empty, Submit(prompt));
        Assert.Null(Row);
    }

    [Fact]
    public void NegativeAnUnresolvedHarnessWritesNothingAndSaysWhy()
    {
        Assert.Contains("no grant: this session's Claude Code process cannot be identified.", Submit("/wrangl:admin", pid: 9999), StringComparison.Ordinal);
        Assert.Null(Row);
    }

    [Fact]
    public void NegativeAnUnreadableDatabaseWritesNothingAndSaysWhy()
    {
        _docs.Down = true;

        Assert.Contains("no grant: the state database cannot be read.", Submit("/wrangl:admin"), StringComparison.Ordinal);
        Assert.Contains("no change: the state database cannot be read.", Submit("/wrangl:admin off"), StringComparison.Ordinal);
        _docs.Down = false;
        Assert.Null(Row);
    }
}
