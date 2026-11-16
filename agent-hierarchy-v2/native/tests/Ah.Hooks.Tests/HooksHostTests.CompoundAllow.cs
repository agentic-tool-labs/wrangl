using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Hooks.Tests;

/// <summary>Fewer-turns S6: the compound-allow gate (<c>pretooluse-compound-allow</c>). Rows are the C-numbers of the spec's section 7.</summary>
public sealed partial class HooksHostTests
{
    private const string CompoundUnit = "pretooluse-compound-allow";
    private const string CompoundAh = "/ahroot/bin/ah";
    private const string SandboxedLaunch = """{"sandbox":{"enabled":true}}""";

    /// <summary>A sandboxed member session: team record, session registration, launch file and a small work tree.</summary>
    private sealed class CompoundFx
    {
        public CompoundFx(
            string? launch = SandboxedLaunch, string? project = null, string? local = null, string? home = null, bool register = true, string? outsideLinkTarget = null)
        {
            Rig = new Rig();
            Wt = Rig.Sandbox.Root + "/proj";
            Directory.CreateDirectory(Wt + "/src");
            File.WriteAllText(Wt + "/a.cs", "x");
            File.WriteAllText(Wt + "/a.txt", "x");
            File.WriteAllText(Wt + "/src/a.cs", "x");
            File.WriteAllText(Wt + "/pats", "x");
            Directory.CreateDirectory(Rig.Sandbox.Root + "/outside");
            Directory.CreateSymbolicLink(Wt + "/link", outsideLinkTarget ?? Rig.Sandbox.Root + "/outside");
            AutoTrackTests.WriteTeam(
                Rig,
                new JsonObject
                {
                    ["version"] = 1,
                    ["members"] = new JsonArray
                    {
                        new JsonObject { ["name"] = "impl", ["role"] = "implementor", ["expected_root"] = Wt },
                    },
                });
            var dir = Rig.Services.Config.HierarchyDir(Wt);
            var teamHome = Rig.Services.Roster.TeamHome(dir);
            if (launch is not null)
            {
                Directory.CreateDirectory(teamHome + "/launch");
                File.WriteAllText(teamHome + "/launch/impl.settings.json", launch);
            }

            Write(Wt + "/.claude/settings.json", project);
            Write(Wt + "/.claude/settings.local.json", local);
            Write(Rig.Sandbox.Home + "/.claude/settings.json", home);
            var advisory = StopOrchestratorLivenessTests.Advisory(Rig, []);
            if (register)
            {
                BindingAttestationTests.Register(advisory.Mailbox, "s-m", 4242, null, "impl@tm");
            }

            Svc = Rig.Services with { Advisory = advisory };
        }

        public HooksHostTests.Rig Rig { get; }

        public string Wt { get; }

        public GateServices Svc { get; }

        private static void Write(string path, string? text)
        {
            if (text is null)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public HookOutcome Run(string command, string agent = "wrangl:implementor", JsonValue? disable = null, GateServices? services = null)
        {
            var input = new JsonObject { ["command"] = command.Replace("/wt", Wt, StringComparison.Ordinal) };
            if (disable is { } d)
            {
                input["dangerouslyDisableSandbox"] = d;
            }

            var json = JsonText.Stringify(
                new JsonObject
                {
                    ["hook_event_name"] = "PreToolUse",
                    ["tool_name"] = "Bash",
                    ["agent_type"] = agent,
                    ["session_id"] = "s-m",
                    ["cwd"] = Wt,
                    ["tool_input"] = input,
                },
                0);
            return Rig.Run(CompoundUnit, json, gate: true, services: () => services ?? Svc);
        }
    }

    /// <summary>A file reader that reports one extra path as existing and passes everything else to <paramref name="inner"/>.</summary>
    private sealed class ExtraPathFiles(IFileReader inner, string extra) : IFileReader
    {
        public string ReadText(string path) => inner.ReadText(path);

        public byte[] ReadBytes(string path) => inner.ReadBytes(path);

        public FileTail ReadTail(string path, int maxBytes) => inner.ReadTail(path, maxBytes);

        public FileRange ReadRange(string path, long offset, int maxBytes) => inner.ReadRange(path, offset, maxBytes);

        public NoFollowRead ReadNoFollow(string path, int maxBytes) => inner.ReadNoFollow(path, maxBytes);

        public FileStat? Stat(string path, bool followSymlinks = true) => inner.Stat(path, followSymlinks);

        public bool Exists(string path) => path == extra || inner.Exists(path);

        public string RealPath(string path) => inner.RealPath(path);

        public IReadOnlyList<string> List(string directory) => inner.List(directory);

        public bool IsExecutable(string path) => inner.IsExecutable(path);
    }

    [Theory]
    [InlineData("/Library/Application Support/ClaudeCode/managed-settings.json")]
    [InlineData("/Library/Application Support/ClaudeCode/managed-settings.d")]
    [InlineData("/etc/claude-code/managed-settings.json")]
    [InlineData("/etc/claude-code/managed-settings.d")]
    public void AManagedSettingsPathMakesTheSessionSettingsSaySoAndNoOtherPathDoes(string managed)
    {
        var fx = new CompoundFx();
        var real = fx.Rig.Services.Files;
        var teamHome = fx.Rig.Services.Roster.TeamHome(fx.Rig.Services.Config.HierarchyDir(fx.Wt));

        var with = Ah.Roster.RosterModule.ReadSessionSettings(new ExtraPathFiles(real, managed), fx.Rig.Sandbox.Home, teamHome, "impl", fx.Wt); // C-91
        var without = Ah.Roster.RosterModule.ReadSessionSettings(new ExtraPathFiles(real, "/not/a/managed/path"), fx.Rig.Sandbox.Home, teamHome, "impl", fx.Wt);

        Assert.True(with.ManagedPresent);
        Assert.False(without.ManagedPresent);
    }

    private static string? CompoundDecision(HookOutcome o) =>
        o.Stdout.IsEmpty ? null : Json(o).Member("hookSpecificOutput")!.Value.Member("permissionDecision")!.Value.AsString();

    // ---- allow rows ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("cd /wt && rg -n foo src")]
    [InlineData("grep -n foo a.cs | head -40")]
    [InlineData("ls src; wc -l a.cs")]
    [InlineData("sed -n '1,40p' a.cs | cat -n")]
    [InlineData("git diff HEAD~1 -- a.cs | head")]
    [InlineData("cd /wt/src && rg -n foo .")]
    [InlineData("git log --oneline -5; git show HEAD:a.cs | head")]
    [InlineData("ls src 2>/dev/null || true")]
    [InlineData("ls src\npwd")]
    [InlineData("cat src/*.cs | wc -l")]
    [InlineData("ls src/*.cs | wc -l")]
    [InlineData("cd ./src && ls")]
    [InlineData("echo \"exit=$?\"; ls")]
    [InlineData("rg foo src | head")]
    [InlineData("ls a.cs 2>/dev/null | wc -l")]
    [InlineData("echo 'a;b'; ls")]
    [InlineData("cat a.txt | head")]
    public void CompoundAllowAllowsAReadOnlyCompoundInASandboxedMember(string command)
    {
        var o = new CompoundFx().Run(command);

        Assert.Equal("allow", CompoundDecision(o));
        Assert.StartsWith("ah: read-only compound (", Reason(o), StringComparison.Ordinal);
    }

    // ---- silent rows (the nearest input that must NOT match each allow condition) ------------------------------------------

    [Theory]
    [InlineData("grep foo a.cs")] // C-7: one plain segment
    [InlineData("MSBUILDDISABLENODEREUSE=1 dotnet build > /wt/b.log 2>&1; echo \"exit=$?\"")] // C-4
    [InlineData("grep foo a.cs > out.txt; ls")] // C-8
    [InlineData("grep foo a.cs > /etc/x; ls")] // C-9
    [InlineData("cat a $(whoami); ls")] // C-10
    [InlineData("cat a `x`; ls")]
    [InlineData("grep 'foo$' a.cs | head")] // C-11
    [InlineData("cat <(ls) | head")] // C-12
    [InlineData("(cd x && ls)")] // C-13
    [InlineData("ls & ls")] // C-14
    [InlineData("sh -c 'ls; pwd'")] // C-15
    [InlineData("env X=1 ls; pwd")] // C-16
    [InlineData("timeout 5 ls; pwd")]
    [InlineData("/tmp/evil/grep x a; ls")] // C-17
    [InlineData("./grep x a; ls")]
    [InlineData("python3 x.py; ls")] // C-18
    [InlineData("awk 1 a; ls")]
    [InlineData("git -c core.pager=x log; ls")] // C-19
    [InlineData("git -C .. diff; ls")]
    [InlineData("PATH=/tmp ls; pwd")] // C-20
    [InlineData("PAGER=x git log; ls")]
    [InlineData("sed -i s/a/b/ f; ls")] // C-21
    [InlineData("sed 's/a/b/e' f; ls")]
    [InlineData("sed -n 'w /x' f; ls")]
    [InlineData("find . -name x -delete; ls")] // C-22, C-54
    [InlineData("rg --pre ./x foo; ls")] // C-23
    [InlineData("git grep -O foo; ls")] // C-24
    [InlineData("git diff --output=/x; ls")]
    [InlineData("git log --ext-diff; ls")]
    [InlineData("sort -o /x a; ls")] // C-25
    [InlineData("uniq a b; ls")]
    [InlineData("date -s now; ls")]
    [InlineData("cp a b; ls")] // C-26, C-27
    [InlineData("mv a ~/x; ls")]
    [InlineData("cat ~/.claude/settings.json | head")] // C-28
    [InlineData("cat .git/config; ls")]
    [InlineData("cp --force a b; ls")] // C-29
    [InlineData("git grep -f pats; ls")]
    [InlineData("cd /tmp && ls")] // C-30
    [InlineData("jq .x a.json | wc -l")] // C-53
    [InlineData("jq -n env; ls")]
    [InlineData("mkdir d; ls")] // C-54
    [InlineData("dotnet test; ls")]
    [InlineData("cat /etc/hosts | head")] // C-55
    [InlineData("cat ../../x; ls")]
    [InlineData("cat .git; ls")] // C-56
    [InlineData("cat sub/.git/HEAD; ls")]
    [InlineData("ls >&out; pwd")] // C-57
    [InlineData("ls 1>&2; pwd")]
    [InlineData("ls >| out; pwd")] // C-58
    [InlineData("ls &>> out; pwd")]
    [InlineData("ls 3> out; pwd")]
    [InlineData("cat < a; ls")] // C-59
    [InlineData("cat <> a; ls")]
    [InlineData("ls > /dev/null; pwd")] // C-60
    [InlineData("cat ~/x; ls")] // C-61
    [InlineData("ls ~; pwd")]
    [InlineData("cat .g*/config; ls")] // C-62
    [InlineData("cat */settings*.json; ls")]
    [InlineData("cat /wt/*; ls")]
    [InlineData("grep --file=/etc/x a; ls")] // C-63
    [InlineData("diff --to-file=.git/config a; ls")]
    [InlineData("git blame --contents=../x a; ls")]
    [InlineData("ls -l\\\na; pwd")] // C-65
    [InlineData("ls #x; pwd")] // C-66
    [InlineData("grep '#x' a; ls")]
    [InlineData("sort --compress-program=x a; ls")] // C-70
    [InlineData("rg --hostname-bin=x foo; ls")]
    [InlineData("sort -T /tmp a; ls")]
    [InlineData("cd; ls")] // C-72
    [InlineData("cd -; ls")]
    [InlineData("cd ~; ls")]
    [InlineData("cd src; ls")]
    [InlineData("ls | cd /wt; ls")] // C-73
    [InlineData("false || cd /wt; ls")]
    [InlineData("cd ./link && ls")] // C-74
    [InlineData("cat ./link/x; ls")]
    [InlineData("grep -e foo /wt/a; ls")] // C-76
    [InlineData("grep --regexp=foo a; ls")]
    [InlineData("grep -f pats a; ls")]
    [InlineData("rg -e foo; ls")]
    [InlineData("rg --file=p; ls")]
    [InlineData("grep -R foo .; ls")] // C-78
    [InlineData("grep --dereference-recursive foo .; ls")]
    [InlineData("rg -L foo; ls")]
    [InlineData("rg --follow foo; ls")]
    [InlineData("diff -r a b; ls")]
    [InlineData("grep foo /etc/passwd; ls")] // C-79
    [InlineData("echo $HOME; ls")] // C-75
    [InlineData("echo \"${?}\"; ls")]
    [InlineData("ls src/*; pwd")] // C-82
    [InlineData("cat *.json | head")] // C-83
    [InlineData("ls src/*/a.cs; pwd")] // C-84
    [InlineData("cat claude.md | head")] // C-85
    [InlineData("cd ./src | ls")] // C-97
    [InlineData("cd ./src && ls; pwd")] // G1: a later separator other than && after a cd
    [InlineData("cd ./src && ls || true")]
    [InlineData("cd /wt/src && rg -n foo . | sort | uniq -c")]
    [InlineData("git grep --untracked foo; ls")] // G2
    [InlineData("git grep --no-index foo; ls")]
    [InlineData("git grep --no-exclude-standard foo; ls")]
    [InlineData("git grep --untr foo; ls")]
    [InlineData("rg -g '*.cs' foo; ls")] // G4
    [InlineData("rg --glob=*.cs foo; ls")]
    [InlineData("rg --iglob x foo; ls")]
    [InlineData("cd ./nope && ls")] // F1: the target does not exist
    [InlineData("cd ./a.cs && ls")] // F1: the target is a file
    [InlineData("cd ./src; ls")] // F1: a failed cd would leave the old cwd
    [InlineData("grep -rn foo src | head")] // F5: recursive grep reads protected files unnamed
    [InlineData("grep --recursive foo src | head")]
    [InlineData("grep -S foo a.cs; ls")] // F2: BSD symlink-follow
    [InlineData("grep -O foo a.cs; ls")]
    [InlineData("sort --files0-from=pats; ls")] // F3
    [InlineData("sort --files0 pats; ls")]
    [InlineData("rg --hidden foo src; ls")] // F5
    [InlineData("rg -u foo src; ls")]
    [InlineData("rg --no-ignore foo src; ls")]
    [InlineData("rg --no-ignore-vcs foo src; ls")]
    [InlineData("rg -. foo src; ls")]
    [InlineData("rg --files /etc; ls")] // rg without a pattern: the first operand is a path
    [InlineData("ls /etc/*.conf; pwd")]
    [InlineData("ls src/{a,.git}; pwd")]
    public void CompoundAllowIsSilentOnTheNearestInputThatMustNotMatch(string command)
    {
        var o = new CompoundFx().Run(command);

        Assert.True(o.Stdout.IsEmpty, command + " => " + Text(o));
    }

    [Fact]
    public void CompoundAllowIsSilentOverTheBudgetAndOnACarriageReturn()
    {
        var fx = new CompoundFx();

        Assert.True(fx.Run("ls; " + new string('x', 4100)).Stdout.IsEmpty); // C-40
        Assert.True(fx.Run("ls;\r pwd").Stdout.IsEmpty);
    }

    // ---- session preconditions --------------------------------------------------------------------------------------------

    [Fact]
    public void CompoundAllowIsSilentForTheOrchestratorAndForASessionWithNoRegistration()
    {
        var fx = new CompoundFx();

        Assert.True(fx.Run("ls | wc -l", "wrangl:orchestrator").Stdout.IsEmpty); // C-39
        Assert.True(fx.Run($"cd /wt && {CompoundAh} roster status", "wrangl:orchestrator").Stdout.IsEmpty);
        Assert.True(new CompoundFx(register: false).Run("ls | wc -l").Stdout.IsEmpty); // C-96
    }

    [Fact]
    public void ACrashInTheGateEmitsNothing()
    {
        var fx = new CompoundFx();

        var o = fx.Run("ls | wc -l", services: fx.Svc with { Roster = null! }); // C-98

        Assert.True(o.Stdout.IsEmpty);
    }

    // ---- settings scopes (rules 9 and S6.2.4) -------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"sandbox":{"enabled":true},"permissions":{"ask":["Bash(git log:*)"]}}""", null, null, "git log; ls")] // C-67
    [InlineData("""{"sandbox":{"enabled":true,"excludedCommands":["cat *"]}}""", null, null, "cat a; ls")] // C-68
    [InlineData(SandboxedLaunch, """{"permissions":{"deny":["Bash(wc:*)"]}}""", null, "wc a; ls")] // C-69
    [InlineData(SandboxedLaunch, "{ not json", null, "ls | wc -l")] // C-92
    [InlineData(SandboxedLaunch, null, null, "cat a.txt | head", """{"permissions":{"deny":["Read(./a.txt)"]}}""")] // C-86
    [InlineData(SandboxedLaunch, """{"permissions":{"deny":["Read(**/.env)"]}}""", null, "rg foo src | head")] // C-88
    [InlineData(SandboxedLaunch, null, null, "cat a.txt | head", """{"permissions":{"deny":["Read(a?.txt)"]}}""")] // C-90
    [InlineData(SandboxedLaunch, null, null, "ls | wc -l", """{"permissions":{"ask":["Read"]}}""")]
    public void CompoundAllowIsSilentWhenAnyScopeDeniesAsksOrExcludes(string launch, string? project, string? local, string command, string? home = null)
    {
        var fx = new CompoundFx(launch, project, local, home);

        Assert.True(fx.Run(command).Stdout.IsEmpty, Text(fx.Run(command)));
    }

    [Theory]
    [InlineData("cat a.txt | head", """{"permissions":{"deny":["Read(~/.ssh/**)"]}}""")] // C-87
    [InlineData("rg foo src | head", """{"permissions":{"deny":["Read(~/.ssh/**)"]}}""")] // C-89
    [InlineData("ls | wc -l", """{"permissions":{"deny":["Bash(git push:*)","Bash(git push *)"]}}""")] // F4: a trailing wildcard is still judged
    [InlineData("wc a.txt | head", """{"permissions":{"deny":["Bash(wcx:*)"]}}""")] // a deny for another program
    public void CompoundAllowStaysAllowedWhenTheRuleCannotReachTheTree(string command, string home)
    {
        var fx = new CompoundFx(home: home);

        Assert.Equal("allow", CompoundDecision(fx.Run(command)));
    }

    // ---- S6.9: inner-`*` rules are judged per command ----------------------------------------------------------------------

    private const string NineDenies =
        "\"Bash(git push* -d *)\",\"Bash(git push* -f*)\",\"Bash(git push*+*)\",\"Bash(git push*--all*)\",\"Bash(git push*--delete*)\","
        + "\"Bash(git push*--force*)\",\"Bash(git push*--mirror*)\",\"Bash(git push*main*)\",\"Bash(sh *..*)\"";

    private static string Home(string deny = "", string ask = "", string excluded = "") =>
        "{\"permissions\":{\"deny\":[" + NineDenies + deny + "],\"ask\":[" + ask + "]},\"sandbox\":{\"excludedCommands\":[" + excluded + "]}}";

    [Theory]
    [InlineData("git log --oneline -5 | head", "", "", "")] // C-99
    [InlineData("grep -n foo src/a.cs | head -20", "", "", "")] // C-100
    [InlineData("ls src | wc -l; pwd", "", "", "")] // C-101
    [InlineData("grep -n foo a.cs | head", ",\"Bash(grep*secret*)\"", "", "")] // C-105
    [InlineData("git log -3 | head", "", "", "\"git push*\"")] // C-110
    [InlineData("ls | wc -l", ",\"Bash(ls*wcx*)\"", "", "")] // nearest miss of C-106: anchored, literal
    [InlineData("ls | head", ",\"Bash(*heads*)\"", "", "")] // nearest miss of C-107
    [InlineData("cat a.txt | head", ",\"Bash(cat a.tx)\"", "", "")] // nearest miss of C-108: no `*`, so exact
    [InlineData("git log -3 | head", "", "", "\"*logs*\"")] // nearest miss of C-111
    [InlineData("ls | sort", "", "\"Bash(sorts*)\"", "")] // nearest miss of C-112
    [InlineData("LANG=C ls src | wc -l", ",\"Bash(ls*srcx*)\"", "", "")] // nearest miss of the assignment-stripped candidate
    [InlineData("ls\nwc -l", "", "", "")] // C-118
    [InlineData("LANG=C git log -3 | head", "", "", "")] // C-119
    [InlineData("ls\nwc -l", ",\"Bash(ls*wcx*)\"", "", "")] // nearest miss of C-115
    [InlineData("LANG=C ls | wc -l", ",\"Bash(ls*wcx*)\"", "", "")] // nearest miss of C-116
    [InlineData("ls | LANG=C wc -l", ",\"Bash(ls | wcx*)\"", "", "")] // nearest miss of C-117
    [InlineData("cat a.t''xt | head", "", "", "")] // review F1-F3: each quoted, escaped or redirected form allows with no rule hit
    [InlineData("c\\at a.txt | head", "", "", "")]
    [InlineData("LANG=\"a\\\"b\" cat a.txt | head", "", "", "")]
    [InlineData("cat a.txt 2>/dev/null | head", ",\"Bash(cat*a.cs)\"", "", "")]
    [InlineData("LANG=C grep \"secret\" a.cs | head", ",\"Bash(grep \\\"secrets\\\"*)\"", "", "")] // nearest miss of the raw-from-program row
    public void S69AnInnerStarRuleThatMatchesNoCandidateLeavesTheCompoundAllowed(string command, string deny, string ask, string excluded)
    {
        var fx = new CompoundFx(home: Home(deny, ask, excluded));

        Assert.Equal("allow", CompoundDecision(fx.Run(command)));
    }

    [Theory]
    [InlineData("git push --force origin x; ls", "", "", "")] // C-102
    [InlineData("ls; git push origin main", "", "", "")] // C-103
    [InlineData("grep -n secret a.cs | head", ",\"Bash(grep*secret*)\"", "", "")] // C-104
    [InlineData("ls | wc -l", ",\"Bash(ls*wc*)\"", "", "")] // C-106
    [InlineData("ls | head", ",\"Bash(*head*)\"", "", "")] // C-107
    [InlineData("cat a.txt | head", ",\"Bash(cat a.txt)\"", "", "")] // C-108
    [InlineData("cat a.txt | head", ",\"Bash(cat:*)\"", "", "")] // C-109
    [InlineData("git log -3 | head", "", "", "\"*log*\"")] // C-111
    [InlineData("ls | sort", "", "\"Bash(sort*)\"", "")] // C-112
    [InlineData("ls | wc -l", ",\"Read(**)\"", "", "")] // C-114
    [InlineData("LANG=C ls src | wc -l", ",\"Bash(ls*src*)\"", "", "")] // the assignment-stripped candidate (no word-prefix match)
    [InlineData("ls | head", ",\"Bash(*hea:*)\"", "", "")] // a legacy `:*` reads as `*` after a leading `*`
    [InlineData("ls\nwc -l", ",\"Bash(ls*wc*)\"", "", "")] // C-115: `*` crosses `\n`
    [InlineData("LANG=C ls | wc -l", ",\"Bash(ls*wc*)\"", "", "")] // C-116: assignment-stripped tail
    [InlineData("ls | LANG=C wc -l", ",\"Bash(ls | wc*)\"", "", "")] // C-117: a later segment's assignment stripped
    [InlineData("cat a.t''xt | head", ",\"Bash(cat*a.txt*)\"", "", "")] // review F1: de-quoted candidate
    [InlineData("c\\at a.txt | head", ",\"Bash(cat*a.txt*)\"", "", "")] // review F1: escaped program word
    [InlineData("LANG=\"a\\\"b\" cat a.txt | head", ",\"Bash(cat*a.txt*)\"", "", "")] // review F2: assignment stripped by the tokenizer
    [InlineData("cat a.txt 2>/dev/null | head", ",\"Bash(cat*a.txt)\"", "", "")] // review F3: redirect deleted, not spaced
    [InlineData("LANG=C grep \"secret\" a.cs | head", ",\"Bash(grep \\\"secret\\\"*)\"", "", "")] // delta review: raw text from the program word
    public void S69AnInnerStarRuleThatMatchesACandidateSilencesTheCompound(string command, string deny, string ask, string excluded)
    {
        var fx = new CompoundFx(home: Home(deny, ask, excluded));

        Assert.True(fx.Run(command).Stdout.IsEmpty, Text(fx.Run(command)));
    }

    [Fact]
    public void S69GlobMatchesKeepsDollarBeforeOneFinalNewline()
    {
        Assert.True(Ah.Hooks.Surface.MemberPermissionsGenerator.GlobMatches("ls", "ls\n")); // C-120
        Assert.False(Ah.Hooks.Surface.MemberPermissionsGenerator.GlobMatches("ls", "ls\n\n")); // nearest miss: anchored, not Multiline
        Assert.True(Ah.Hooks.Surface.MemberPermissionsGenerator.GlobMatches("a*b", "a\nb"));
    }

    [Theory]
    [InlineData("""{"permissions":{"allow":["Bash(git push origin *:refs/heads/ah/*)","Bash(ls * wc)"]}}""")] // C-113
    [InlineData("""{"permissions":{"deny":["Bash(git * push)"]}}""")] // was F4's global silence
    [InlineData("""{"permissions":{"ask":["Bash(*x:*)"]}}""")]
    [InlineData("""{"sandbox":{"excludedCommands":["docker * run"]}}""")] // was G3's global silence
    [InlineData("""{"sandbox":{"excludedCommands":["*git"]}}""")]
    public void S69ARuleThatCannotMatchThisCommandNoLongerSilencesEveryCompound(string home)
    {
        var fx = new CompoundFx(home: home);

        Assert.Equal("allow", CompoundDecision(fx.Run("ls | wc -l")));
    }

    // ---- ah-simplify S1: a compound ah call is allowed alongside read-only segments, never denied ----------------------------

    [Fact]
    public void ACompoundThatHoldsAnAhCallIsAllowedAndOneWithAnUnjudgedSegmentIsSilent()
    {
        var fx = new CompoundFx();

        Assert.Equal("allow", CompoundDecision(fx.Run($"cd /wt && {CompoundAh} msg list")));
        Assert.Equal("allow", CompoundDecision(fx.Run($"{CompoundAh} msg list | head")));
        Assert.True(fx.Run($"cd /wt && {CompoundAh} msg list && rm x").Stdout.IsEmpty); // nearest miss: rm is not judged
        Assert.True(fx.Run($"sh -c '{CompoundAh} msg list'").Stdout.IsEmpty); // nearest miss: nested
    }

    [Fact]
    public void APlainAhCallIsLeftToTheAhCliGate()
    {
        var fx = new CompoundFx();

        Assert.True(fx.Run($"{CompoundAh} msg list --cwd /wt").Stdout.IsEmpty); // C-34
        Assert.True(fx.Run($"{CompoundAh} msg send x@y --kind note --text '>'").Stdout.IsEmpty);
    }

    // ---- the chain: a deny elsewhere still wins ------------------------------------------------------------------------------

    [Fact]
    public void TheChainKeepsTheWorktreeWriteGatesDenyOverTheAllow()
    {
        var fx = new CompoundFx();
        var json = JsonText.Stringify(
            new JsonObject
            {
                ["hook_event_name"] = "PreToolUse",
                ["tool_name"] = "Bash",
                ["agent_type"] = "wrangl:implementor",
                ["session_id"] = "s-m",
                ["cwd"] = fx.Wt,
                ["tool_input"] = new JsonObject { ["command"] = "cd /tmp && ls" },
            },
            0);

        var o = fx.Rig.Run("pretooluse-chain", json, gate: true, services: () => fx.Svc);

        Assert.NotEqual("allow", CompoundDecision(o)); // C-30: the compound-allow unit is silent and the chain decides as today
    }
}
