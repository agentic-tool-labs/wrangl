using System.Reflection;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.TestKit;

namespace Ah.Hierarchy.Tests;

/// <summary>
/// Runs the unit tables of the hierarchy libraries against the native port. The same tables run on Node through
/// <c>tests/contract-tools/units-run.mjs</c>; a case passes only when its recorded Node result is reproduced: the value, the
/// message of a thrown error, and the files the case lists.
/// </summary>
public sealed class HierarchyUnitTableTests
{
    private static readonly string NativeRoot = typeof(HierarchyUnitTableTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "AhNativeRoot").Value!;

    private static string UnitsDirectory => Path.Combine(NativeRoot, "..", "tests", "contracts", "units");

    /// <summary>The harness clock gives each instant its own offset: a June instant prints EDT even when "now" is in EST.</summary>
    [Theory]
    [InlineData("2026-01-15T12:00:00Z", "2026-06-01T12:00:00Z", "2026-06-01T08:00:00-04:00")]
    [InlineData("2026-06-01T12:00:00Z", "2026-01-15T12:00:00Z", "2026-01-15T07:00:00-05:00")]
    public void TheHarnessClockUsesTheOffsetInForceAtTheInstantNotNow(string now, string date, string created)
    {
        var c = new UnitCase("dst-pin", JsonText.Parse(
            $$"""{"now":"{{now}}","env":{"TZ":"America/New_York"},"args":[{"id":"a"},{"$date":"{{date}}"}]}""").AsObject());

        var result = HierarchyTableFns.Dispatch("lib-hier", "responseFrontmatter", c, UnitSandbox.Create());

        Assert.Contains($"created: {created}\n", result!.Value.AsString(), StringComparison.Ordinal);
    }

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var file in UnitTable.TableFiles(NativeRoot))
        {
            var table = UnitTable.Load(file);
            if (!HierarchyTableFns.All.Contains(table.Lib + "/" + table.Fn))
            {
                continue;
            }

            foreach (var c in table.Cases)
            {
                data.Add(Path.GetRelativePath(UnitsDirectory, file), c.Id);
            }
        }

        return data;
    }

    [Fact]
    public void EveryDispatchedFunctionHasATable()
    {
        var present = UnitTable.TableFiles(NativeRoot).Select(f => UnitTable.Load(f)).Select(t => t.Lib + "/" + t.Fn).ToHashSet();
        Assert.All(HierarchyTableFns.All, f => Assert.Contains(f, present));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CaseMatchesNode(string tableFile, string caseId)
    {
        var path = Path.Combine(UnitsDirectory, tableFile);
        var table = UnitTable.Load(path);
        var c = table.Cases.Single(x => x.Id == caseId);
        var raw = JsonText.Parse(Ah.TestKit.RecordedNamespace.ReadAllBytes(path)).AsObject()["cases"].AsArray()
            .Single(x => x.AsObject()["id"].AsString() == caseId).AsObject();
        var sandbox = UnitSandbox.Create();
        sandbox.WriteFiles(c, import: false);
        JsonValue? result = null;
        string? threw = null;
        try
        {
            result = HierarchyTableFns.Dispatch(table.Lib, table.Fn, c, sandbox);
        }
        catch (Exception e)
        {
            threw = e.Message;
        }

        var failure = Verify(raw, c, sandbox, result, threw);
        Assert.True(failure is null, $"{table.Lib}/{table.Fn} / {caseId}: {failure}");
    }

    private static string Contract(UnitSandbox sandbox, string text) =>
        sandbox.Encode(text).AsString().Replace(HierarchyTableFns.AhRoot, "$AHROOT", StringComparison.Ordinal);

    private static JsonValue ContractValue(UnitSandbox sandbox, JsonValue? value)
    {
        var encoded = sandbox.Encode(value);
        return JsonText.Parse(JsonText.Stringify(encoded, 0).Replace(HierarchyTableFns.AhRoot, "$AHROOT", StringComparison.Ordinal));
    }

    /// <summary>
    /// An activity record as the recorded Node oracle wrote it: without the fields only the native writer adds (<c>state_since</c>,
    /// <c>minutes</c>, <c>working_at</c> and <c>working_since</c>), which <c>ActivityMinutesTests</c> and <c>ActivityWorkingSinceTests</c> hold.
    /// </summary>
    private static string WithoutNativeActivityFields(string text)
    {
        JsonValue parsed;
        try
        {
            parsed = JsonText.Parse(text);
        }
        catch (JsonParseException)
        {
            return text;
        }

        if (!parsed.IsMap)
        {
            return text;
        }

        var record = parsed.AsObject();
        var removed = record.Remove("state_since") | record.Remove("minutes") | record.Remove("working_at") | record.Remove("working_since");
        return removed ? JsonText.Stringify(record, 0) + (text.EndsWith('\n') ? "\n" : string.Empty) : text;
    }

    private static string? Verify(JsonObject raw, UnitCase c, UnitSandbox sandbox, JsonValue? result, string? threw)
    {
        if (c.ExpectThrows)
        {
            if (threw is null)
            {
                return "expected the call to throw";
            }

            if (raw.Get("expectError") is { IsText: true } want && Contract(sandbox, threw) != want.AsString())
            {
                return $"error message: expected {JsonText.Stringify(want, 0)}, got {JsonText.Stringify(Contract(sandbox, threw), 0)}";
            }
        }
        else
        {
            if (threw is not null)
            {
                return "the call threw: " + threw;
            }

            var encoded = ContractValue(sandbox, result);
            if (c.ExpectPrefix is not null)
            {
                if (!(encoded.IsText && encoded.AsString().StartsWith(c.ExpectPrefix, StringComparison.Ordinal)))
                {
                    return $"expected a string starting {c.ExpectPrefix}, got {JsonText.Stringify(encoded, 0)}";
                }
            }
            else if (c.Expect is { } expected && JsonText.Stringify(expected, 0) != JsonText.Stringify(encoded, 0))
            {
                return $"expected {JsonText.Stringify(expected, 0)}, got {JsonText.Stringify(encoded, 0)}";
            }
        }

        if (c.ExpectFiles is not null)
        {
            // a table names files; a message written is a row, so it is written out for the comparison
            var messages = c.ExpectFiles.Keys.Select(rel => Path.Combine(sandbox.Root, sandbox.ReplaceRoot(rel))).Where(Ah.TestKit.TestMsgStore.IsMessagePath).ToList();
            if (messages.Count > 0)
            {
                messages.Where(File.Exists).ToList().ForEach(File.Delete);
                Ah.TestKit.TestMsgStore.MirrorOut(new Ah.TestKit.FakeEnvironment(sandbox.Environment(c)).AhHome);
            }

            // an activity record is a row (per-dir-files-to-db S2), so it is written out for the comparison
            foreach (var rel in c.ExpectFiles.Keys)
            {
                HierarchyTableFns.ProjectActivity(new Ah.TestKit.FakeEnvironment(sandbox.Environment(c)).AhHome, Path.Combine(sandbox.Root, sandbox.ReplaceRoot(rel)));

                // and so is a gate row (S3)
                HierarchyTableFns.ProjectGates(new Ah.TestKit.FakeEnvironment(sandbox.Environment(c)).AhHome, Path.Combine(sandbox.Root, sandbox.ReplaceRoot(rel)));

                // and a roster row (S7)
                HierarchyTableFns.ProjectPeers(new Ah.TestKit.FakeEnvironment(sandbox.Environment(c)).AhHome, Path.Combine(sandbox.Root, sandbox.ReplaceRoot(rel)));
            }

            foreach (var rel in c.ExpectFiles.Keys)
            {
                var file = Path.Combine(sandbox.Root, sandbox.ReplaceRoot(rel));
                var actual = File.Exists(file) ? Contract(sandbox, Utf8Text.Decode(File.ReadAllBytes(file))) : null;
                // files-off-disk F3a: the status document is served from the database, so the recorded status.json must not appear
                var want = rel.EndsWith("/status.json", StringComparison.Ordinal) ? JsonValue.Null : c.ExpectFiles[rel];
                if (want.IsMap && want.AsObject().Get("$jsonl") is { IsArray: true } rows)
                {
                    if (actual is null || !JsonlEqual(actual, rows.AsArray()))
                    {
                        return $"file {rel}: expected rows {JsonText.Stringify(rows, 0)}, got {(actual is null ? "no file" : JsonText.Stringify(actual, 0))}";
                    }

                    continue;
                }

                if (want.IsMap && want.AsObject().Get("$json") is { } wantJson)
                {
                    JsonValue parsed;
                    try
                    {
                        parsed = actual is null ? JsonValue.Null : JsonText.Parse(actual);
                    }
                    catch (JsonParseException)
                    {
                        return $"file {rel}: not JSON";
                    }

                    if (parsed.IsMap && rel.Contains("/activity/", StringComparison.Ordinal))
                    {
                        _ = parsed.AsObject().Remove("state_since");
                        _ = parsed.AsObject().Remove("minutes");
                        _ = parsed.AsObject().Remove("working_at");
                        _ = parsed.AsObject().Remove("working_since");
                    }

                    if (actual is null || !WildEqual(parsed, wantJson))
                    {
                        return $"file {rel}: expected JSON {JsonText.Stringify(wantJson, 0)}, got {(actual is null ? "no file" : JsonText.Stringify(actual, 0))}";
                    }

                    continue;
                }

                if (actual is not null && rel.Contains("/activity/", StringComparison.Ordinal))
                {
                    actual = WithoutNativeActivityFields(actual);
                }

                var wantText = want.IsNull ? null : want.AsString();
                if (actual != wantText)
                {
                    return $"file {rel}: expected {JsonText.Stringify(want, 0)}, got {(actual is null ? "no file" : JsonText.Stringify(actual, 0))}";
                }
            }
        }

        return null;
    }

    private static bool JsonlEqual(string text, JsonArray expected)
    {
        var rows = new List<JsonValue>();
        foreach (var line in text.Split('\n'))
        {
            if (JsText.Trim(line).Length == 0)
            {
                continue;
            }

            try
            {
                rows.Add(JsonText.Parse(line));
            }
            catch (JsonParseException)
            {
                return false;
            }
        }

        return rows.Count == expected.Count && rows.Select((r, i) => WildEqual(r, expected[i])).All(x => x);
    }

    private static bool WildEqual(JsonValue actual, JsonValue want)
    {
        if (want.IsText && want.AsString() == "$any")
        {
            return true;
        }

        if (actual.Kind != want.Kind)
        {
            return false;
        }

        switch (want.Kind)
        {
            case JsonKind.Array:
                var a = actual.AsArray();
                var b = want.AsArray();
                return a.Count == b.Count && Enumerable.Range(0, a.Count).All(i => WildEqual(a[i], b[i]));
            case JsonKind.Map:
                var x = actual.AsObject();
                var y = want.AsObject();
                return x.Count == y.Count && x.Keys.Zip(y.Keys).All(p => p.First == p.Second && WildEqual(x[p.First], y[p.Second]));
            default:
                return JsonText.Stringify(actual, 0) == JsonText.Stringify(want, 0);
        }
    }
}
