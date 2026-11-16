using System.Reflection;
using Ah.Core.Json;
using Ah.Hooks.Gates;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Hooks.Tests;

/// <summary>
/// Runs the unit tables of <c>lib-ah-cli</c> and the herdr parsers of <c>lib-gate-target</c>. The same tables run on Node
/// through <c>tests/contract-tools/units-run.mjs</c>; a case passes here only when its recorded Node result is reproduced.
/// </summary>
public sealed class HooksUnitTableTests
{
    private static readonly string NativeRoot = typeof(HooksUnitTableTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "AhNativeRoot").Value!;

    private static string UnitsDirectory => Path.Combine(NativeRoot, "..", "tests", "contracts", "units-h1");

    private static IEnumerable<string> TableFiles() =>
        Directory.EnumerateFiles(UnitsDirectory, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal);

    private static readonly HashSet<string> Owned = new(StringComparer.Ordinal)
    {
        "parseAhCommand", "scriptUnderRoot", "isCloseCommand", "ROSTER_BOOL_FLAGS", "MSG_BOOL_FLAGS",
        "AHD_BOOL_FLAGS", "CLOSE_VERBS", "herdrCalls", "herdrInputCalls", "HERDR_INPUT_VERBS",
    };

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var file in TableFiles())
        {
            var table = UnitTable.Load(file);
            foreach (var c in table.Cases)
            {
                data.Add(Path.GetRelativePath(UnitsDirectory, file), c.Id);
            }
        }

        return data;
    }

    [Fact]
    public void EveryTableIsOwnedAndEveryOwnedFunctionHasATable()
    {
        var present = TableFiles().Select(f => UnitTable.Load(f).Fn).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(present.Where(p => !Owned.Contains(p)).OrderBy(p => p, StringComparer.Ordinal));
        Assert.Empty(Owned.Where(o => !present.Contains(o)).OrderBy(o => o, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CaseMatchesNode(string tableFile, string caseId)
    {
        var table = UnitTable.Load(Path.Combine(UnitsDirectory, tableFile));
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

        if (DeliberateDivergences.TryGetValue($"{table.Fn}/{c.Id}", out var native))
        {
            // The recorded Node answer stays as the case's expectation; the native answer is the ruled one.
            Assert.False(threw);
            Assert.True(c.Expect is { IsBool: true } node && node.AsBool(), $"{table.Fn} / {c.Id}: the recorded Node answer changed");
            Assert.True(result is { IsBool: true } r && r.AsBool() == native, $"{table.Fn} / {c.Id}: native must answer {native}");
            return;
        }

        Assert.True(sandbox.Verify(c, result, threw) is null, $"{table.Fn} / {c.Id}: {sandbox.Verify(c, result, threw)}");
    }

    /// <summary>
    /// Cases Node answers differently from the native rule, by ruling: a sibling directory is accepted only when it is another
    /// directory of the same plugin (same manifest name); Node accepts any sibling. The fixtures hold no manifests, so each
    /// sibling here is refused.
    /// </summary>
    private static readonly Dictionary<string, bool> DeliberateDivergences = new(StringComparer.Ordinal)
    {
        ["scriptUnderRoot/sibling-version"] = false,
        ["scriptUnderRoot/sibling-of-root-itself"] = false,
    };

    private static JsonValue Strings(IEnumerable<string> values)
    {
        var a = new JsonArray();
        foreach (var v in values)
        {
            a.Add(v);
        }

        return a;
    }

    private static JsonValue CommandJson(AhCommand? command)
    {
        if (command is null)
        {
            return JsonValue.Null;
        }

        var flags = new JsonObject();
        foreach (var f in command.Flags)
        {
            flags[f.Key] = f.Value is null ? JsonValue.True : JsonValue.FromString(f.Value);
        }

        return new JsonObject
        {
            ["script"] = command.Script,
            ["scriptPath"] = command.ScriptPath,
            ["verb"] = command.Verb is null ? JsonValue.Null : JsonValue.FromString(command.Verb),
            ["positional"] = Strings(command.Positional),
            ["flags"] = flags,
        };
    }

    private static AhCommand? CommandOf(JsonValue? value)
    {
        if (value is not { IsMap: true } m)
        {
            return null;
        }

        var o = m.AsObject();
        var flags = new List<KeyValuePair<string, string?>>();
        if (o.Get("flags") is { IsMap: true } f)
        {
            foreach (var key in f.AsObject().Keys)
            {
                var v = f.AsObject()[key];
                flags.Add(new KeyValuePair<string, string?>(key, v.IsText ? v.AsString() : null));
            }
        }

        var positional = o.Get("positional") is { IsArray: true } p ? p.AsArray().Select(x => x.AsString()).ToList() : [];
        return new AhCommand(
            o.Get("script") is { IsText: true } s ? s.AsString() : string.Empty,
            o.Get("scriptPath") is { IsText: true } sp ? sp.AsString() : string.Empty,
            o.Get("verb") is { IsText: true } v2 ? v2.AsString() : null,
            positional,
            flags);
    }

    private static JsonValue CallsJson(IEnumerable<HerdrCall> calls)
    {
        var list = new JsonArray();
        foreach (var call in calls)
        {
            list.Add(new JsonObject
            {
                ["group"] = call.Group,
                ["verb"] = call.Verb,
                ["name"] = call.Name,
                ["pane"] = call.Pane is null ? JsonValue.Null : JsonValue.FromString(call.Pane),
                ["args"] = Strings(call.Args),
            });
        }

        return list;
    }

    private static JsonValue? Dispatch(string fn, UnitCase c, UnitSandbox sandbox)
    {
        JsonValue? Arg(int i) => i < c.Args.Count ? sandbox.Decode(c.Args[i]) : null;
        string? Text(int i) => Arg(i) is { IsText: true } t ? t.AsString() : null;

        switch (fn)
        {
            case "parseAhCommand":
                return CommandJson(AhCli.Parse(Text(0)));
            case "scriptUnderRoot":
                return AhCli.ScriptUnderRoot(Text(0), Text(1), PlatformModule.CreateIo(null).Files);
            case "isCloseCommand":
                return AhCli.IsCloseCommand(CommandOf(Arg(0)));
            case "ROSTER_BOOL_FLAGS":
                return Strings(AhCli.RosterBoolFlags);
            case "MSG_BOOL_FLAGS":
                return Strings(AhCli.MsgBoolFlags);
            case "AHD_BOOL_FLAGS":
                return Strings(AhCli.AhdBoolFlags);
            case "CLOSE_VERBS":
                return Strings(AhCli.CloseVerbs);
            case "herdrInputCalls":
                return CallsJson(HerdrCalls.InputCalls(Text(0)));
            case "herdrCalls":
            {
                var verbs = Arg(2) is { IsArray: true } list ? list.AsArray().Select(v => v.AsString()).ToList() : [];
                return CallsJson(HerdrCalls.Find(Text(0), Text(1) ?? string.Empty, verbs));
            }

            case "HERDR_INPUT_VERBS":
            {
                var verbs = new JsonObject();
                foreach (var group in HerdrCalls.InputVerbs)
                {
                    verbs[group.Key] = Strings(group.Value);
                }

                return verbs;
            }

            default:
                throw new InvalidOperationException($"no dispatch for {fn}");
        }
    }
}
