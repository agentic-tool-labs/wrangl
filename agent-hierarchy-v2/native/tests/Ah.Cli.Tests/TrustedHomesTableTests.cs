using System.Security.Cryptography;
using System.Text.Json;
using Ah.Cli.Composition;
using Ah.Platform;

namespace Ah.Cli.Tests;

/// <summary>
/// The <c>trusted_homes</c> rows of <c>tests/contracts/home-predicate.json</c> through the state-home gate and the trust store: the
/// table the sh twin (<c>bin/ah-trust.sh</c>) is run against too, so the two verdicts cannot drift.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class TrustedHomesTableTests
{
    private static readonly string TablePath = FindTable();

    private static string FindTable()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "contracts", "home-predicate.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("tests/contracts/home-predicate.json was not found above " + AppContext.BaseDirectory);
    }

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        using var doc = JsonDocument.Parse(File.ReadAllText(TablePath));
        foreach (var c in doc.RootElement.GetProperty("trusted_homes").EnumerateArray())
        {
            data.Add(c.GetProperty("id").GetString()!);
        }

        return data;
    }

    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheTableRowGivesItsExpectedVerdict(string id)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TablePath));
        var row = doc.RootElement.GetProperty("trusted_homes").EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);
        var rand = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var root = "/tmp/ahprobe.t" + rand;
        Directory.CreateDirectory(root, OwnerOnly);
        var outside = "/var/tmp/outside.t" + rand;
        Directory.CreateDirectory(outside, OwnerOnly);
        var io = PlatformModule.CreateIo(null);
        var files = io.Files;
        string? Sub(string? s) => s?.Replace("$ROOT", root, StringComparison.Ordinal).Replace("$OUT", outside, StringComparison.Ordinal).Replace("$RAND", rand, StringComparison.Ordinal);
        string? Real(string? s) => s?.Replace("$ROOT", files.RealPath(root), StringComparison.Ordinal).Replace("$OUT", files.RealPath(outside), StringComparison.Ordinal).Replace("$RAND", rand, StringComparison.Ordinal);
        try
        {
            var passwd = Path.Combine(root, "pw");
            Directory.CreateDirectory(passwd);
            if (row.TryGetProperty("ops", out var ops))
            {
                foreach (var op in ops.EnumerateArray())
                {
                    var path = Sub(op.GetProperty("path").GetString())!;
                    switch (op.GetProperty("op").GetString())
                    {
                        case "mkdir":
                            Directory.CreateDirectory(path);
                            break;
                        case "write":
                            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                            var content = Sub(op.GetProperty("content").GetString())!;
                            if (op.TryGetProperty("pad_to", out var pad))
                            {
                                while (System.Text.Encoding.UTF8.GetByteCount(content) < pad.GetInt32())
                                {
                                    content += "#\n";
                                }

                                content = content[..pad.GetInt32()];
                            }

                            File.WriteAllText(path, content);
                            break;
                        case "symlink":
                            File.CreateSymbolicLink(path, Sub(op.GetProperty("target").GetString())!);
                            break;
                        case "chmod":
                            File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32(op.GetProperty("mode").GetString(), 8));
                            break;
                        default:
                            throw new InvalidOperationException("unknown op in " + id);
                    }
                }
            }

            var vars = new Dictionary<string, string?>
            {
                ["AH_TEST"] = Text(row, "ah_test"),
                ["AH_HOME"] = Sub(Text(row, "ah_home")),
                ["HOME"] = outside + "/injected-home",
            };
            var uid = PlatformModule.CurrentUid();
            var env = PlatformModule.CreateEnvironment(n => vars.GetValueOrDefault(n), null, files, uid, () => passwd);

            var verdict = AhHomeGate.Check(env, files, uid);

            var expect = row.GetProperty("expect");
            Assert.Equal(expect.GetProperty("accepted").GetBoolean(), verdict.Accepted);
            var testMode = expect.TryGetProperty("test_mode", out var tm) && tm.GetBoolean();
            if (verdict.Accepted && !testMode && Text(expect, "home") is { } home)
            {
                Assert.Equal(Real(home), verdict.Home);
            }

            if (!verdict.Accepted && Text(expect, "reason") is { } reason)
            {
                Assert.Equal(reason, verdict.Reason);
            }
        }
        finally
        {
            File.SetUnixFileMode(root, OwnerOnly);
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }
}
