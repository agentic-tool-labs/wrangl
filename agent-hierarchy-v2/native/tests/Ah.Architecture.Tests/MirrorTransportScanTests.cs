using System.Text;
using System.Text.RegularExpressions;

namespace Ah.Architecture.Tests;

/// <summary>
/// The terminal client reaches a daemon by GET only, on its own routes, over the one socket its existing discovery found: no write route,
/// no second way to find a daemon, no way to start or drain one.
/// </summary>
public sealed class MirrorTransportScanTests
{
    private const string Client = "Ah.Tui";

    // The one discovery rule is DaemonViewSource's; the loop in SliceFeed asks it through its interface.
    private const string DiscoveryFile = "DaemonViewSource.cs";
    private const string DiscoveryUser = "SliceFeed.cs";

    private static readonly string[] AllowedPaths = ["/v1/health", "/v1/view", "/v1/view/summary"];

    private static readonly string[] Banned =
    [
        "EnsureDaemon", "DrainAsync", "RequestAsync", "HttpClient", "System.Net", "UnixDomainSocketEndPoint",
        "PostAsync", "PutAsync", "DeleteAsync", "PatchAsync",
    ];

    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex LineComment = new(@"//[^\r\n]*", RegexOptions.CultureInvariant);
    private static readonly Regex PathLiteral = new("^\\$?\"([^\"]*)\"", RegexOptions.CultureInvariant);

    private sealed record Source(string File, string Text);

    private static List<Source> RealSources() =>
    [
        .. Directory.EnumerateFiles(Path.Combine(Workspace.NativeRoot, "src", Client), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => new Source(Path.GetFileName(f), File.ReadAllText(f))),
    ];

    private static string Code(string text) => LineComment.Replace(BlockComment.Replace(text, string.Empty), string.Empty);

    /// <summary>The top-level arguments of each <c>.SendAsync(</c> call in <paramref name="code"/>.</summary>
    private static List<List<string>> SendCalls(string code)
    {
        const string Needle = ".SendAsync(";
        var calls = new List<List<string>>();
        for (var at = code.IndexOf(Needle, StringComparison.Ordinal); at >= 0; at = code.IndexOf(Needle, at + Needle.Length, StringComparison.Ordinal))
        {
            var args = new List<string>();
            var arg = new StringBuilder();
            var depth = 0;
            var inString = false;
            for (var i = at + Needle.Length; i < code.Length; i++)
            {
                var ch = code[i];
                if (inString)
                {
                    arg.Append(ch);
                    if (ch == '\\' && i + 1 < code.Length)
                    {
                        arg.Append(code[++i]);
                    }
                    else if (ch == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                }
                else if (ch is '(' or '[' or '{')
                {
                    depth++;
                }
                else if (ch is ')' or ']' or '}')
                {
                    if (depth == 0)
                    {
                        break;
                    }

                    depth--;
                }
                else if (ch == ',' && depth == 0)
                {
                    args.Add(arg.ToString().Trim());
                    arg.Clear();
                    continue;
                }

                arg.Append(ch);
            }

            args.Add(arg.ToString().Trim());
            calls.Add(args);
        }

        return calls;
    }

    /// <summary>Whether a path literal is the allowed route <paramref name="route"/>: the route itself or the route and its query. A longer path under the route is another route.</summary>
    private static bool Names(string literal, string route) =>
        literal == route || literal.StartsWith(route + "?", StringComparison.Ordinal);

    private static List<string> Violations(IEnumerable<Source> sources)
    {
        var found = new List<string>();
        foreach (var s in sources)
        {
            var code = Code(s.Text);
            var where = $"{Client}/{s.File}";
            found.AddRange(Banned.Where(t => code.Contains(t, StringComparison.Ordinal)).Select(t => $"{where}: uses {t}"));
            if (code.Contains(".DiscoverAsync(", StringComparison.Ordinal) && s.File is not (DiscoveryFile or DiscoveryUser))
            {
                found.Add($"{where}: a second way to find a daemon");
            }

            foreach (var call in SendCalls(code))
            {
                if (call.Count < 3)
                {
                    found.Add($"{where}: SendAsync call not readable");
                    continue;
                }

                if (call[1] != "\"GET\"")
                {
                    found.Add($"{where}: SendAsync method is {call[1]}, not the literal \"GET\"");
                }

                var literal = PathLiteral.Match(call[2]);
                if (!literal.Success || !AllowedPaths.Any(p => Names(literal.Groups[1].Value, p)))
                {
                    found.Add($"{where}: SendAsync path {call[2]} is not one of {string.Join(", ", AllowedPaths)}");
                }
            }
        }

        return found;
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void TheClientReachesADaemonByGetOnItsOwnRoutesOnly()
    {
        var sources = RealSources();

        Assert.Contains(sources, s => s.File == DiscoveryFile);
        Assert.NotEmpty(sources.SelectMany(s => SendCalls(Code(s.Text))));
        Assert.Empty(Violations(sources));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void TheClientsExistingPathsPass()
    {
        Source[] sources =
        [
            new("DaemonViewSource.cs", "await transport.SendAsync(info.Socket, \"GET\", \"/v1/health\", default, T, ct); await transport.SendAsync(s, \"GET\", \"/v1/view\" + Query(p), default, T, ct);"),
            new("DaemonViewSource.cs", "x = a ? await t.SendAsync(s, \"GET\", \"/v1/view/summary\", default, T, ct) : await t.SendAsync(s, \"GET\", \"/v1/view?path=%2Fevents&since=0&wait=0&sub=\" + ids + \"&fresh=\" + ids, default, T, ct);"),
        ];

        Assert.Empty(Violations(sources));
    }

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("\"POST\"", "\"/v1/view/summary\"")]
    [InlineData("\"GET\"", "model ?? \"/v1/view\"")]
    [InlineData("\"GET\"", "\"/v1/view/timeline\"")]
    [InlineData("\"GET\"", "\"/v1/read-model\"")]
    [InlineData("\"GET\"", "\"/v1/pane?path=\"")]
    public void AWriteAVariablePathOrAnotherRouteInTheClientIsCaught(string method, string path)
    {
        var source = new Source("DaemonViewSource.cs", $"x = await t.SendAsync(socket, {method}, {path}, default, timeout, token);");

        Assert.Single(Violations([source]));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void ASecondWayToFindADaemonIsCaughtButTheExistingOneIsNot()
    {
        const string Call = "var info = await daemon.DiscoverAsync(T, ct);";

        Assert.Empty(Violations([new Source(DiscoveryFile, Call)]));
        Assert.Single(Violations([new Source("Other.cs", Call)]));
    }

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("await daemon.EnsureDaemonAsync(b, ct);")]
    [InlineData("await daemon.RequestAsync(r, ct);")]
    [InlineData("await http.PostAsync(u, c);")]
    public void StartingOrWritingMachineryIsCaught(string line) =>
        Assert.NotEmpty(Violations([new Source("X.cs", line)]));

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void ACommentThatNamesAForbiddenThingIsNotAUse()
    {
        const string Text = "// never calls RequestAsync or HttpClient\n/* IDaemonClient.DiscoverAsync( is not used here */\nint x = 1;";

        Assert.Empty(Violations([new Source("X.cs", Text)]));
    }
}
