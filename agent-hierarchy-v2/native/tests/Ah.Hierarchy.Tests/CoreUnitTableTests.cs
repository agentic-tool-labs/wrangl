using System.Reflection;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Hierarchy.Tests;

/// <summary>
/// Runs every unit table of <c>tests/contracts/units</c> against the native core. The same tables run on Node through
/// <c>tests/contract-tools/units-run.mjs</c>; a case passes here only when its recorded Node result is reproduced.
/// </summary>
public sealed class CoreUnitTableTests
{
    private static readonly string NativeRoot = typeof(CoreUnitTableTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "AhNativeRoot").Value!;

    /// <summary>The twelve core exports and the seven near misses; tables of other streams share the folder.</summary>
    private static readonly HashSet<string> CoreFns =
    [
        "logHookError", "readHookInput", "isSubagent", "resolveConfig", "readTeam", "readMsgFile", "resolveHierarchyRole",
        "listExchanges", "hierarchyDir", "parseMsgFilename", "pidAlive", "newId",
        "mainHierarchyDir", "ownedTeams", "readPeerRecords", "teamPrefix", "teamPath", "listTeamNames", "extractMsgToken",
    ];

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var file in UnitTable.TableFiles(NativeRoot))
        {
            var table = UnitTable.Load(file);
            if (!CoreFns.Contains(table.Fn))
            {
                continue;
            }

            foreach (var c in table.Cases)
            {
                data.Add(Path.GetRelativePath(Path.Combine(NativeRoot, "..", "tests", "contracts", "units"), file), c.Id);
            }
        }

        return data;
    }

    [Fact]
    public void TablesCoverTheTwelveCoreExportsAndTheSevenNearMisses()
    {
        var present = UnitTable.TableFiles(NativeRoot).Select(f => UnitTable.Load(f).Fn).ToHashSet();
        Assert.All(CoreFns, r => Assert.Contains(r, present));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CaseMatchesNode(string tableFile, string caseId)
    {
        var table = UnitTable.Load(Path.Combine(NativeRoot, "..", "tests", "contracts", "units", tableFile));
        var c = table.Cases.Single(x => x.Id == caseId);
        var sandbox = UnitSandbox.Create();
        sandbox.WriteFiles(c);
        JsonValue? result = null;
        var threw = false;
        try
        {
            result = Dispatch(table.Fn, c, sandbox);
        }
        catch (Exception)
        {
            threw = true;
        }

        Assert.True(sandbox.Verify(c, result, threw) is null, $"{table.Fn} / {c.Id}: {sandbox.Verify(c, result, threw)}");
    }

    private static JsonValue? Dispatch(string fn, UnitCase c, UnitSandbox sandbox)
    {
        var envVars = sandbox.Environment(c);
        var env = new FakeEnvironment(envVars);
        var io = PlatformModule.CreateIo(null);
        var cwd = sandbox.WorkingDirectory(c);

        JsonValue? Arg(int i) => i < c.Args.Count ? sandbox.Decode(c.Args[i]) : null;
        string? Text(int i) => Arg(i) is { IsText: true } t ? t.AsString() : null;

        var clock = ClockFor(c, envVars);
        IConfigCore config = null!;
        IRosterCore roster = null!;
        config = ConfigModule.CreateCore(io.Files, env, () => roster, new ProcessFacts(cwd, 4242), docs: new TableHomeDocs(env));
        roster = RosterModule.CreateCore(io.Files, env, config, io.Processes, PlatformModule.CreateProcessTable(PlatformModule.CreateProcessRunner()));
        var hierarchy = HierarchyModule.CreateCore(io.Files, env, clock, new SequenceRandom(), io.Processes, TestMsgStore.Route(env.AhHome));

        switch (fn)
        {
            case "parseMsgFilename":
            {
                var parts = hierarchy.ParseMsgFilename(Text(0) ?? string.Empty);
                return parts is null
                    ? JsonValue.Null
                    : new JsonObject { ["id"] = parts.Id, ["to"] = parts.To, ["slug"] = parts.Slug, ["type"] = parts.Type };
            }

            case "extractMsgToken":
                return hierarchy.ExtractMsgToken(Text(0)) is { } token ? token : JsonValue.Null;
            case "pidAlive":
                return hierarchy.PidAlive(Arg(0) ?? JsonValue.Null);
            case "newId":
                return hierarchy.NewId();
            case "readMsgFile":
                return hierarchy.ReadMsgFile(Text(0)!);
            case "listExchanges":
                return hierarchy.ListExchanges(Text(0)!);
            case "readPeerRecords":
                return hierarchy.ReadPeerRecords();
            case "isSubagent":
                return config.IsSubagent(Arg(0) ?? JsonValue.Null);
            case "readHookInput":
            {
                var bytes = c.StdinBase64 is not null ? Convert.FromBase64String(c.StdinBase64) : Utf8Text.Encode(sandbox.ReplaceRoot(c.Stdin ?? string.Empty));
                return config.ParseHookInput(bytes, new RecordingHookErrorLog());
            }

            case "logHookError":
                return LogHookError(c, sandbox, Arg(0), Arg(1), Arg(2), clock, env);
            case "hierarchyDir":
                return config.HierarchyDir(Text(0));
            case "mainHierarchyDir":
                return config.MainHierarchyDir(Text(0)) is { } main ? main : JsonValue.Null;
            case "teamPrefix":
                return config.TeamPrefix(Text(0), Text(1));
            case "teamPath":
                return roster.TeamPath(Text(0)!, Text(1));
            case "readTeam":
                return roster.ReadTeamRecord(Text(0)!, Text(1));
            case "listTeamNames":
                return Strings(roster.ListTeamNames(Text(0)!));
            case "ownedTeams":
                return OwnedTeams(roster, Text(0)!, Arg(1));
            case "resolveHierarchyRole":
                return RoleResult(config.ResolveHierarchyRole(Arg(0) ?? JsonValue.Null, Arg(1) is { IsMap: true } r ? r.AsObject() : null));
            case "resolveConfig":
                return config.ResolveConfig(Text(0), Options(Arg(1)));
            default:
                throw new NotSupportedException("no native dispatcher for " + fn);
        }
    }

    private static FakeClock ClockFor(UnitCase c, Dictionary<string, string> env)
    {
        var instant = c.Now is not null ? DateTimeOffset.Parse(c.Now, System.Globalization.CultureInfo.InvariantCulture) : DateTimeOffset.UtcNow;
        if (c.Args.Count > 0 && c.Args[0].IsMap && c.Args[0].AsObject().Get("$date") is { IsText: true } d)
        {
            instant = DateTimeOffset.Parse(d.AsString(), System.Globalization.CultureInfo.InvariantCulture);
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(env.GetValueOrDefault("TZ", "UTC"));
        return new FakeClock(instant) { Zone = zone };
    }

    private static JsonValue Strings(IEnumerable<string?> values)
    {
        var array = new JsonArray();
        foreach (var v in values)
        {
            array.Add(v is null ? JsonValue.Null : v);
        }

        return array;
    }

    private static JsonValue OwnedTeams(IRosterCore roster, string dir, JsonValue? invoker)
    {
        if (invoker is not { IsTruthy: true } inv)
        {
            return new JsonArray();
        }

        var pid = JsConvert.AsInteger(inv.Member("pid"));
        var session = inv.Member("sessionId") is { IsText: true } s && s.AsString().Length > 0 ? s.AsString() : null;
        return Strings(roster.OwnedTeams(dir, new TeamInvoker(pid is >= int.MinValue and <= int.MaxValue ? (int)pid : null, session)));
    }

    private static JsonValue RoleResult(HierarchyRole r)
    {
        var o = new JsonObject { ["role"] = r.Role is null ? JsonValue.Null : r.Role, ["direct"] = r.Direct };
        if (r.Resolved is not null)
        {
            o["resolved"] = r.Resolved;
        }

        return o;
    }

    private static ResolveOptions Options(JsonValue? raw)
    {
        if (raw is not { IsMap: true } m)
        {
            return new ResolveOptions();
        }

        var o = m.AsObject();
        return new ResolveOptions(
            Team: o.Get("team") is { IsText: true } t && t.AsString().Length > 0 ? t.AsString() : null,
            TeamHome: o.Get("teamHome") is { IsText: true } h && h.AsString().Length > 0 ? h.AsString() : null,
            DefaultTeam: o.Get("defaultTeam") is { IsBool: true } d && d.AsBool(),
            Pid: o.Get("pid"),
            SessionId: o.Get("sessionId") is { IsText: true } s && s.AsString().Length > 0 ? s.AsString() : null,
            Roster: o.Get("roster"));
    }

    private static JsonValue? LogHookError(UnitCase c, UnitSandbox sandbox, JsonValue? hook, JsonValue? err, JsonValue? input, IClock clock, IAhEnvironment env)
    {
        var log = PlatformModule.CreateHookErrorLog(clock, env);
        string message;
        var stack = new List<string>();
        if (err is { IsMap: true } e && e.AsObject().Get("$error") is { IsMap: true } error)
        {
            message = error.AsObject()["message"].AsString();
            if (error.AsObject().Get("stack") is { IsArray: true } frames)
            {
                var text = string.Join('\n', frames.AsArray().Select(f => f.AsString()));
                if (text.Length > 0)
                {
                    stack.AddRange(text.Split('\n').Skip(1).Take(3).Select(JsText.Trim));
                }
            }
        }
        else
        {
            message = JsConvert.ToJsString(err);
        }

        string? eventName = input?.Member("hook_event_name") is { IsText: true } ev ? ev.AsString() : null;
        string? session = input?.Member("session_id") is { IsText: true } sid ? sid.AsString() : null;
        log.Log(new HookErrorRecord(hook!.Value.AsString(), message, eventName, session, stack));
        return null;
    }
}
