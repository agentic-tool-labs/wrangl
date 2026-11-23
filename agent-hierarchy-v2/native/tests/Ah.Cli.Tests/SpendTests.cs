using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs;
using Ah.Platform;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>ah2-512: <c>ah spend add|show</c> against a real state database.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed partial class SpendTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly IStateDb _db;
    private readonly string _cwd;
    private readonly string _dir;

    public SpendTests()
    {
        _cwd = Path.Combine(_sandbox.Root, "my-cool-repo");
        _dir = Path.Combine(_cwd, ".claude", "ah2");
        Directory.CreateDirectory(Path.Combine(_dir, "teams"));
        File.WriteAllText(Path.Combine(_dir, "teams", "tm.json"), "{\"members\":[]}");
        _db = StateDbModule.Open(_sandbox.AhHome, mayCreate: true);
        Assert.Equal(StateDbAvailability.Ready, _db.Status.Availability);
    }

    public void Dispose()
    {
        _db.Dispose();
        _sandbox.Dispose();
    }

    private (int Code, string Out, string Err) Spend(params string[] args) =>
        Run((context, _, stdout, stderr) => SpendVerb.Run(context, [.. args, "--cwd", _cwd], stdout, stderr));

    /// <summary>Who the rules route proves the caller to be.</summary>
    private CutoverCaller _caller = new(CutoverCallerKind.User, "user");

    /// <summary>Whether the caller is in team <c>tm</c>, as its team file env puts it.</summary>
    private bool _inTeam;

    private (int Code, string Out, string Err) Run(Func<CliContext, ServiceGraph, TextWriter, TextWriter, int> verb)
    {
        var vars = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["WRANGL_HIERARCHY_DIR"] = _dir };
        if (_inTeam)
        {
            vars["WRANGL_TEAM_FILE"] = Path.Combine(_dir, "teams", "tm.json");
        }

        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, vars, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var daemon = new FakeDaemonClient();
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, FakeRunner.KnowingSelf(), daemon);
        var route = new RulesRoute(graph, _sandbox.AhHome, () => _db, _ => _caller, () => DateTimeOffset.UtcNow);
        daemon.Reply = request =>
        {
            Assert.Equal("/v1/rules", request.PathAndQuery);
            var response = route.HandleAsync(new RouteRequest("POST", "/v1/rules", string.Empty, new Dictionary<string, string>(), request.Body, new Dictionary<string, string>()), CancellationToken.None).GetAwaiter().GetResult();
            return new DaemonReply(response.Status, response.Body);
        };
        var context = new CliContext(env, new RecordingExec(), new FakeRunner(), "ah", _sandbox.Executable("install/ah"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(graph));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = verb(context, graph, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private List<object?[]> Query(string sql)
    {
        var reader = StateDbModule.OpenReader(_sandbox.AhHome).Reader!;
        using (reader)
        {
            return [.. reader.Query(sql, [])];
        }
    }

    [Fact]
    public void AnAddWithNoOpenItemRecordsNoItemAndExitsZero()
    {
        var added = Spend("add", "4", "Credits", "--what", "image gen");

        Assert.True(added.Code == 0, added.Err);
        Assert.Equal("counted 4 credits (total 4 credits).\n", added.Out);
        var row = Query("SELECT item, unit, micros, what FROM spend")[0];
        Assert.Null(row[0]);
        Assert.Equal("credits", row[1]);
        Assert.Equal(4_000_000L, row[2]);

        // nearest input that must not count: --what missing
        Assert.Equal(2, Spend("add", "4", "credits").Code);
        Assert.Single(Query("SELECT id FROM spend"));
    }

    [Fact]
    public void ZeroIsAUsageErrorAndANegativeCountIsACorrection()
    {
        Assert.True(Spend("add", "10", "credits", "--what", "a").Code == 0);

        var zero = Spend("add", "0", "credits", "--what", "a");
        Assert.Equal(2, zero.Code);
        Assert.Empty(zero.Out);

        // nearest decimal that must not parse: seven fractional digits
        Assert.Equal(2, Spend("add", "0.0000001", "usd", "--what", "a").Code);

        // a count past the exact range is a usage error, not a crash
        Assert.Equal(2, Spend("add", "100000000000000000000000", "credits", "--what", "a").Code);

        var corrected = Spend("add", "-2", "credits", "--what", "miscounted");
        Assert.True(corrected.Code == 0, corrected.Err);
        Assert.Equal("counted -2 credits (total 8 credits).\n", corrected.Out);
        Assert.Equal(0, Spend("add", "0.04", "usd", "--what", "a").Code);
        Assert.Contains("  8 credits\n  0.04 usd\n", Spend().Out, StringComparison.Ordinal);
    }
}
