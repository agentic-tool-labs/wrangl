using System.Text;
using Ah.Abstractions;
using Ah.Platform;
using Ah.State;
using Ah.TestKit;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("macos")]
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace Ah.Commands.Tests;

/// <summary>The one-shot <c>ahd state</c> verb: stdin in, the route's body out, and the exit code.</summary>
public sealed class StateVerbTests
{
    private sealed class Domain(params StateOp[] ops) : IStateDomain
    {
        public string Name => "store";

        public IReadOnlyList<string> StoreNames => [];

        public IReadOnlyList<StateOp> Ops { get; } = ops;

        public string StateDirectory(RequestContext context) => context.HierarchyDirectory + "/store";

        public void AfterWrite(OpCall operation, OpResult result)
        {
        }
    }

    private sealed class Rig
    {
        public Rig()
        {
            var sandbox = UnitSandbox.Create();
            Project = sandbox.Root + "/proj";
            Hierarchy = Project + "/.claude/ah2";
            Directory.CreateDirectory(Hierarchy);
            Directory.CreateDirectory(Project + "/.git");
            var io = PlatformModule.CreateIo(null);
            var environment = new FakeEnvironment(new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = sandbox.Root + "/ah" });
            Directory.CreateDirectory(sandbox.Root + "/ah");
            var domain = new Domain(
                new StateOp("put", OpKind.Write, OpStyle.Overwrite, false, null, true, (call, ctx) =>
                {
                    ctx!.Write(call.Context.HierarchyDirectory + "/a.json", call.Context.HierarchyDirectory + "/a.tmp", "{}"u8);
                    return new OpResult(true, "{\"written\":true}"u8.ToArray());
                }),
                new StateOp("fail", OpKind.Write, OpStyle.Append, false, null, true, (_, _) => new OpResult(false, default, "not allowed", "not_allowed")),
                new StateOp("crash", OpKind.Write, OpStyle.Append, false, null, true, (_, _) => throw new InvalidOperationException("kaput")),
                new StateOp("get", OpKind.Read, null, false, null, true, (_, _) => new OpResult(true, "[]"u8.ToArray())),
                new StateOp("replace", OpKind.Write, OpStyle.Replace, false, null, true, (_, _) => new OpResult(true, "{\"ok\":true}"u8.ToArray()), DigestPrecondition: true));
            Locks = io.Locks;
            var engine = StateModule.CreateEngine(io.Files, io.Writer, io.Appender, io.Locks, new FakeClock(DateTimeOffset.UnixEpoch), [domain], 4242);
            StateModule.Compose(StateModule.CreateService(
                [domain],
                engine,
                io.Files,
                PlatformModule.CurrentUid(),
                Ah.Config.ConfigModule.CreateHierarchyDirPolicy(io.Files, environment, null, () => PlatformModule.CurrentUid()),
                new RecordingHookErrorLog()));
        }

        public string Project { get; }

        public string Hierarchy { get; }

        public ILockProvider Locks { get; }

        public string Context => "{\"cwd\":\"" + Project + "\",\"hierarchy_dir\":\"" + Hierarchy + "\"}";

        public string Body(string args = "{}", string? opId = "op-1") =>
            "{\"args\":" + args + ",\"context\":" + Context + (opId is null ? string.Empty : ",\"op_id\":\"" + opId + "\"") + "}";

        public static (int Code, string Stdout, string Stderr) Run(string stdin, params string[] argv)
        {
            CommandsModule.Compose(() => new MemoryStream(Encoding.UTF8.GetBytes(stdin)));
            var verb = CommandsModule.Registry.Verbs.Single(v => v.Name == "state");
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var code = verb.Run(argv, stdout, stderr);
            return (code, stdout.ToString(), stderr.ToString());
        }
    }

    [Fact]
    public void ASuccessfulWritePrintsTheRouteBodyAndExitsZero()
    {
        var rig = new Rig();
        var (code, stdout, _) = Rig.Run(rig.Body(), "store", "put");

        Assert.Equal(0, code);
        Assert.Equal("{\"result\":{\"written\":true}}\n", stdout);
        Assert.True(File.Exists(rig.Hierarchy + "/a.json"));
    }

    [Fact]
    public void ACwdFlagIsAcceptedAndTheBodysContextStillDecides()
    {
        var rig = new Rig();
        var plain = Rig.Run(rig.Body(opId: "plain"), "store", "put");
        File.Delete(rig.Hierarchy + "/a.json");
        var withCwd = Rig.Run(rig.Body(opId: "with-cwd"), "store", "put", "--cwd", "/somewhere/else");
        File.Delete(rig.Hierarchy + "/a.json");
        var leading = Rig.Run(rig.Body(opId: "leading"), "--cwd", "/somewhere/else", "store", "put");

        Assert.Equal(plain, withCwd);
        Assert.Equal(plain, leading);
        Assert.Equal(0, withCwd.Code);
        Assert.True(File.Exists(rig.Hierarchy + "/a.json"));
    }

    [Theory]
    [InlineData("--cwd")]
    [InlineData("--cwd", "--hook")]
    public void ACwdFlagWithoutAValueExitsTwoBeforeTheBodyIsRead(params string[] tail)
    {
        var rig = new Rig();
        var (code, stdout, _) = Rig.Run(rig.Body(), ["store", "put", .. tail]);

        Assert.Equal(2, code);
        Assert.Equal("{\"error\":{\"code\":\"bad_request\",\"message\":\"--cwd needs a value\"}}\n", stdout);
        Assert.False(File.Exists(rig.Hierarchy + "/a.json"));
    }

    [Fact]
    public void ACwdValueIsNotAPositionalSoAThirdWordIsStillRefused()
    {
        var rig = new Rig();
        var (code, stdout, _) = Rig.Run(rig.Body(), "store", "put", "extra", "--cwd", "/x");

        Assert.Equal(2, code);
        Assert.Contains("usage: ahd state", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherUnknownOptionIsStillRefused()
    {
        var rig = new Rig();
        var (code, stdout, _) = Rig.Run(rig.Body(), "store", "put", "--dir", "/x");

        Assert.Equal(2, code);
        Assert.Contains("unknown option --dir", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ARepeatedOperationIdAnswersTheSameAndRunsOnce()
    {
        var rig = new Rig();
        var first = Rig.Run(rig.Body(opId: "same"), "store", "put");
        File.Delete(rig.Hierarchy + "/a.json");
        var again = Rig.Run(rig.Body(opId: "same"), "store", "put");

        Assert.Equal(first, again);
        Assert.False(File.Exists(rig.Hierarchy + "/a.json"));
    }

    [Fact]
    public void AnOpsOwnFailureExitsOneWithTheOpFailedBody()
    {
        var rig = new Rig();
        var (code, stdout, _) = Rig.Run(rig.Body(), "store", "fail");

        Assert.Equal(1, code);
        Assert.Equal("{\"error\":{\"code\":\"op_failed\",\"message\":\"not allowed\",\"error_code\":\"not_allowed\"}}\n", stdout);
    }

    [Fact]
    public void AnUnexpectedFailureExitsSeventyWithTheInternalBody()
    {
        var rig = new Rig();
        var (code, stdout, _) = Rig.Run(rig.Body(), "store", "crash");

        Assert.Equal(70, code);
        Assert.Equal("{\"error\":{\"code\":\"internal\",\"message\":\"internal error\"}}\n", stdout);
    }

    [Fact]
    public void ADigestConflictIsASuccess()
    {
        var rig = new Rig();
        File.WriteAllText(rig.Hierarchy + "/doc.json", "{}");
        var (code, stdout, _) = Rig.Run(rig.Body("{\"path\":\"" + rig.Hierarchy + "/doc.json\",\"expect\":\"absent\"}"), "store", "replace");

        Assert.Equal(0, code);
        Assert.Equal("{\"result\":{\"conflict\":true}}\n", stdout);
    }

    [Theory]
    [InlineData("store", "nope")]
    [InlineData("elsewhere", "put")]
    [InlineData("store", "get")]
    public void AnUnknownDomainOrOpOrAReadOpExitsTwo(string domain, string op)
    {
        var rig = new Rig();
        var (code, stdout, _) = Rig.Run(rig.Body(), domain, op);

        Assert.Equal(2, code);
        Assert.Contains("\"error\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ABodyWithoutAnOpIdOrWithAnUnknownKeyOrNotJsonExitsTwo()
    {
        var rig = new Rig();
        Assert.Equal(2, Rig.Run(rig.Body(opId: null), "store", "put").Code);
        Assert.Equal(2, Rig.Run("{\"args\":{},\"ctx\":{},\"op_id\":\"x\"}", "store", "put").Code);
        Assert.Equal(2, Rig.Run("not json", "store", "put").Code);
        Assert.Equal(2, Rig.Run("[1]", "store", "put").Code);
    }

    [Fact]
    public void ABodyOverTheCapExitsTwoAndTheCapIsAcceptedExactly()
    {
        var rig = new Rig();
        var padding = new string('x', DefaultCap);
        var over = Rig.Run("{\"args\":{\"p\":\"" + padding + "\"}}", "store", "put");
        Assert.Equal(2, over.Code);
        Assert.Contains("\"too_large\"", over.Stdout, StringComparison.Ordinal);

        var body = rig.Body();
        var pad = new string(' ', DefaultCap - Encoding.UTF8.GetByteCount(body));
        var exact = Rig.Run(body + pad, "store", "put");
        Assert.Equal(0, exact.Code);
    }

    [Theory]
    [InlineData]
    [InlineData("store")]
    [InlineData("store", "put", "extra")]
    [InlineData("store", "put", "--budget", "100")]
    [InlineData("store", "put", "--hook", "--budget", "0")]
    [InlineData("store", "put", "--hook", "--budget", "soon")]
    [InlineData("store", "put", "--nope")]
    public void UsageErrorsExitTwo(params string[] argv)
    {
        var rig = new Rig();
        Assert.Equal(2, Rig.Run(rig.Body(), argv).Code);
    }

    [Fact]
    public void ALockThatDoesNotClearWithinTheHookBudgetExitsSeventyFive()
    {
        var rig = new Rig();
        Directory.CreateDirectory(rig.Hierarchy + "/store");
        using var held = rig.Locks.TryAcquire(rig.Hierarchy + "/store/.domain.lock");
        Assert.NotNull(held);

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var (code, _, _) = Rig.Run(rig.Body(), "store", "put", "--hook", "--budget", "200");

        Assert.Equal(75, code);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), $"took {timer.Elapsed}");
    }

    private const int DefaultCap = 65_536;
}
