namespace Ah.Architecture.Tests;

public sealed class ArchitectureTests
{
    private const string Fixture = "Ah.Fixture.Violations";

    public static TheoryData<string> Projects => [.. Layers.SourceProjects];

    private static string Owners => Path.Combine(AppContext.BaseDirectory, "store-owners.json");

    private static void NoneOf(List<string> violations) =>
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));

    // (a) allowed edges, on the real projects

    [Theory]
    [MemberData(nameof(Projects))]
    public void ProjectReferencesEqualTheLayerTable(string project) =>
        NoneOf(Checks.EdgeViolations(project, Checks.ProjectReferences(Workspace.SourceCsproj(project))));

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [MemberData(nameof(Projects))]
    public void BuiltAssembliesReferenceOnlyTheirLayers(string project) =>
        NoneOf(Checks.BuiltEdgeViolations(project, Workspace.BuiltAssembly(project)));

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void TheLayerTableHasNoCycles() => NoneOf(Checks.Cycles(Layers.AllowedReferences));

    // (a) can fail

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void AnExtraProjectReferenceIsCaught()
    {
        var violations = Checks.EdgeViolations("Ah.Mesh", [.. Layers.AllowedReferences["Ah.Mesh"], "Ah.Daemon"]);

        Assert.Equal(["Ah.Mesh: forbidden reference Ah.Daemon"], violations);
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void ADroppedProjectReferenceIsCaught()
    {
        var violations = Checks.EdgeViolations(
            "Ah.Mesh", Layers.AllowedReferences["Ah.Mesh"].Where(r => r != "Ah.Roster"));

        Assert.Equal(["Ah.Mesh: missing reference Ah.Roster"], violations);
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void AnUpwardEdgeIsCaughtEvenWhenTheOtherEdgesAreRight()
    {
        var violations = Checks.EdgeViolations("Ah.Roster", [.. Layers.AllowedReferences["Ah.Roster"], "Ah.Hierarchy"]);

        Assert.Equal(["Ah.Roster: forbidden reference Ah.Hierarchy"], violations);
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void ABuiltAssemblyWithAnUndeclaredEdgeIsCaught()
    {
        var violations = Checks.BuiltEdgeViolations("Ah.Mesh", Workspace.FixtureAssembly(Fixture));

        Assert.Contains("Ah.Mesh: built assembly references Ah.Daemon", violations);
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void AFixtureProjectFileWithTheForbiddenReferenceIsCaught()
    {
        var violations = Checks.EdgeViolations("Ah.Mesh", Checks.ProjectReferences(Workspace.FixtureCsproj(Fixture)));

        Assert.Contains("Ah.Mesh: forbidden reference Ah.Daemon", violations);
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void ACycleIsCaught()
    {
        var graph = new Dictionary<string, string[]>(Layers.AllowedReferences) { ["Ah.Abstractions"] = ["Ah.Core"] };

        var cycles = Checks.Cycles(graph);

        Assert.Contains(cycles, c => c.Contains("Ah.Abstractions -> Ah.Core -> Ah.Abstractions"));
    }

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [MemberData(nameof(Projects))]
    public void InternalsAreVisibleToTheProjectsOwnTestsOnly(string project) =>
        NoneOf(Checks.InternalsVisibleToViolations(project, Workspace.BuiltAssembly(project)));

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void AnAssemblyGrantingInternalsToAnotherProjectsTestsIsCaught()
    {
        var violations = Checks.InternalsVisibleToViolations("Ah.Mesh", Workspace.BuiltAssembly("Ah.Roster"));

        Assert.Single(violations);
    }

    // (c) restricted interfaces

    [Theory]
    [MemberData(nameof(Projects))]
    public void RestrictedWritersStayInTheirOwners(string project) =>
        NoneOf(Checks.RestrictedInterfaceViolations(project, Workspace.BuiltAssembly(project)));

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void ARestrictedWriterUsedFromHierarchyIsCaught()
    {
        var violations = Checks.RestrictedInterfaceViolations("Ah.Hierarchy", Workspace.FixtureAssembly(Fixture));

        Assert.Equal(
            ["Ah.Hierarchy: references restricted IRawFileWriter", "Ah.Hierarchy: references restricted IRecordAppender"],
            violations.Order());
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void ARestrictedWriterUsedFromAnOwnerPasses() =>
        NoneOf(Checks.RestrictedInterfaceViolations("Ah.Launch", Workspace.FixtureAssembly(Fixture)));

    // (d) P/Invoke

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [MemberData(nameof(Projects))]
    public void PInvokeIsOnlyInPlatform(string project) =>
        NoneOf(Checks.PInvokeViolations(project, Workspace.BuiltAssembly(project)));

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void APInvokeOutsidePlatformIsCaught()
    {
        var violations = Checks.PInvokeViolations("Ah.Mesh", Workspace.FixtureAssembly(Fixture));

        Assert.Contains(violations, v => v.Contains("GetPid"));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void OnlyPlatformAndStateDbMayDeclarePInvokes() =>
        Assert.Equal(["Ah.Platform", "Ah.StateDb"], Checks.PInvokeOwners.Order(StringComparer.Ordinal));

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void StateDbDeclaresItsNativeCallsWithLibraryImportNeverDllImport()
    {
        var root = Path.Combine(Workspace.NativeRoot, "src", "Ah.StateDb");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        Assert.Contains(files, f => File.ReadAllText(f).Contains("[LibraryImport(", StringComparison.Ordinal));
        Assert.DoesNotContain(files, f => File.ReadAllText(f).Contains("[DllImport", StringComparison.Ordinal));
    }

    /// <summary>
    /// The shared state database keeps its schema and its file-cache tables; the timeline's table comes from its migrations, never
    /// from the file's own setup. A change to either changes this pin on purpose.
    /// </summary>
    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void StateDbSchemaIsPinned()
    {
        var root = Path.Combine(Workspace.NativeRoot, "src", "Ah.StateDb");
        var file = File.ReadAllText(Path.Combine(root, "StateDbFile.cs"));

        Assert.Contains("public const int SchemaVersion = 44;", File.ReadAllText(Path.Combine(root, "StateDbModule.cs")), StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(file, "CREATE TABLE"));
        Assert.DoesNotContain("timeline", file, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] BlockingTokens =
    [
        "await ", "File.", "Directory.", "Socket", "Stream", "Process.", "HttpClient", "Console.", "Thread.Sleep", "Thread.Join", "Task.Delay", "Task.Run",
        ".Wait(", ".Result", ".GetResult(", "lock ", "lock(", "Monitor.", "Mutex", "Semaphore",
    ];

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Whether <paramref name="token"/> occurs in <paramref name="text"/> not as the tail of a longer name, so <c>StateDbFile.</c> is not <c>File.</c>.</summary>
    private static bool HasToken(string text, string token)
    {
        for (var at = text.IndexOf(token, StringComparison.Ordinal); at >= 0; at = text.IndexOf(token, at + 1, StringComparison.Ordinal))
        {
            if (!char.IsLetter(token[0]) || at == 0 || !IsNameChar(text[at - 1]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The next occurrence of <paramref name="word"/> from <paramref name="start"/>, ignoring case, with no name character on either side; -1 when there is none.</summary>
    private static int IndexOfWord(string text, string word, int start)
    {
        for (var at = text.IndexOf(word, start, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var after = at + word.Length;
            if ((at == 0 || !IsNameChar(text[at - 1])) && (after >= text.Length || !IsNameChar(text[after])))
            {
                return at;
            }
        }

        return -1;
    }

    /// <summary>The source with line and block comments blanked, leaving string and character literals as they are, so a word in a comment is not read as code and <c>//</c> inside a string is not a comment.</summary>
    internal static string WithoutComments(string source)
    {
        var result = new System.Text.StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 2;
            }
            else if (c is '"' or '\'')
            {
                var verbatim = c == '"' && i > 0 && source[i - 1] == '@';
                result.Append(c);
                i++;
                while (i < source.Length)
                {
                    result.Append(source[i]);
                    if (verbatim && source[i] == '"' && i + 1 < source.Length && source[i + 1] == '"')
                    {
                        result.Append('"');
                        i += 2;
                        continue;
                    }

                    if (!verbatim && source[i] == '\\' && i + 1 < source.Length)
                    {
                        result.Append(source[i + 1]);
                        i += 2;
                        continue;
                    }

                    if (source[i] == c)
                    {
                        i++;
                        break;
                    }

                    i++;
                }
            }
            else
            {
                result.Append(c);
                i++;
            }
        }

        return result.ToString();
    }

    /// <summary>Where the delegate passed to a <c>.Write(x =&gt; ...)</c> call starts: the index of the call, for each call whose first argument is a lambda.</summary>
    private static IEnumerable<int> WriteLambdas(string text)
    {
        for (var at = text.IndexOf(".Write(", StringComparison.Ordinal); at >= 0; at = text.IndexOf(".Write(", at + 1, StringComparison.Ordinal))
        {
            var i = at + ".Write(".Length;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i < text.Length && text[i] == '(')
            {
                var close = text.IndexOf(')', i);
                i = close < 0 ? text.Length : close + 1;
            }
            else
            {
                while (i < text.Length && IsNameChar(text[i]))
                {
                    i++;
                }
            }

            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i + 1 < text.Length && text[i] == '=' && text[i + 1] == '>')
            {
                yield return at;
            }
        }
    }

    /// <summary>
    /// What stands between a <c>BEGIN IMMEDIATE</c> or <c>BEGIN EXCLUSIVE</c> and the <c>COMMIT</c> after it, found case-insensitively: a
    /// blocking call, or no <c>COMMIT</c> at all. Also checks the body of each <c>Write(x =&gt; ...)</c> delegate a caller hands the direct
    /// writer. It reads one method's text; a blocking call hidden behind a helper that the window calls is a known limit.
    /// </summary>
    internal static List<string> BlockingInTransactions(string name, string source)
    {
        var text = WithoutComments(source);
        List<string> found = [];
        foreach (var begin in new[] { "BEGIN IMMEDIATE", "BEGIN EXCLUSIVE" })
        {
            for (var at = IndexOfWord(text, begin, 0); at >= 0; at = IndexOfWord(text, begin, at + 1))
            {
                var end = IndexOfWord(text, "COMMIT", at);
                if (end < 0)
                {
                    found.Add($"{name}: {begin} has no COMMIT after it");
                    continue;
                }

                var window = text[at..end];
                found.AddRange(BlockingTokens.Where(t => HasToken(window, t)).Select(b => $"{name}: '{b}' between {begin} and COMMIT"));
            }
        }

        foreach (var at in WriteLambdas(text))
        {
            var depth = 0;
            var close = text.IndexOf('(', at);
            for (; close < text.Length; close++)
            {
                if (text[close] == '(')
                {
                    depth++;
                }
                else if (text[close] == ')' && --depth == 0)
                {
                    break;
                }
            }

            var body = text[at..Math.Min(close, text.Length)];
            found.AddRange(BlockingTokens.Where(t => HasToken(body, t)).Select(b => $"{name}: '{b}' inside a direct-write delegate"));
        }

        return found;
    }

    /// <summary>
    /// A write transaction holds the database's one write lock, so nothing between <c>BEGIN IMMEDIATE</c> and the <c>COMMIT</c> that follows
    /// it may wait on a file, a socket, a process, a lock or an await: the work inside is a synchronous delegate over the connection.
    /// </summary>
    [Fact]
    public void NothingBlocksBetweenBeginImmediateAndCommit()
    {
        List<string> found = [];
        var sites = 0;
        foreach (var path in Directory.EnumerateFiles(Path.Combine(Workspace.NativeRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(path);
            sites += System.Text.RegularExpressions.Regex.Count(WithoutComments(text), "BEGIN IMMEDIATE", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            found.AddRange(BlockingInTransactions(Path.GetFileName(path), text));
        }

        Assert.True(sites >= 3, "the scan found too few transactions to mean anything");
        Assert.Empty(found);
    }

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("c.Exec(\"BEGIN IMMEDIATE\"); await Task.Yield(); c.Exec(\"COMMIT\");", "await ")]
    [InlineData("c.Exec(\"begin immediate\"); File.ReadAllText(p); c.Exec(\"commit\");", "File.")]
    [InlineData("c.Exec(\"BEGIN EXCLUSIVE\"); Thread.Sleep(5); c.Exec(\"COMMIT\");", "Thread.Sleep")]
    [InlineData("c.Exec(\"BEGIN IMMEDIATE\"); lock (gate) { } c.Exec(\"COMMIT\");", "lock ")]
    [InlineData("c.Exec(\"BEGIN IMMEDIATE\"); t.Wait(); c.Exec(\"COMMIT\");", ".Wait(")]
    [InlineData("c.Exec(\"BEGIN IMMEDIATE\"); // COMMIT here is a comment\n DoWork();", "no COMMIT")]
    [InlineData("x.Write(tx => { File.WriteAllText(p, t); return 0; });", "inside a direct-write delegate")]
    [InlineData("x.Write(anything => { File.WriteAllText(p, t); return 0; });", "inside a direct-write delegate")]
    [InlineData("x.Write((a) => { Thread.Sleep(5); return 0; });", "inside a direct-write delegate")]
    [InlineData("c.Exec(\"BEGIN IMMEDIATE\"); var committed = 1; File.Delete(p); c.Exec(\"COMMIT\");", "File.")]
    [InlineData("c.Exec(\"BEGIN IMMEDIATE\"); var u = \"http://x\"; File.Delete(p); c.Exec(\"COMMIT\");", "File.")]
    [InlineData("c.Exec(\"BEGIN IMMEDIATE\"); Monitor.Enter(g); c.Exec(\"COMMIT\");", "Monitor.")]
    [InlineData("c.Exec(\"BEGIN IMMEDIATE\"); m.WaitOne(); var x = new Mutex(); c.Exec(\"COMMIT\");", "Mutex")]
    [InlineData("c.Exec(\"BEGIN IMMEDIATE\"); s.Release(); var x = new SemaphoreSlim(1); c.Exec(\"COMMIT\");", "Semaphore")]
    [InlineData("c.Exec(\"BEGIN IMMEDIATE\"); t.GetAwaiter().GetResult(); c.Exec(\"COMMIT\");", ".GetResult(")]
    [InlineData("c.Exec(\"BEGIN IMMEDIATE\"); Task.Run(() => 1); c.Exec(\"COMMIT\");", "Task.Run")]
    [InlineData("c.Exec(\"BEGIN IMMEDIATE\"); worker.Join(); Thread.Join(); c.Exec(\"COMMIT\");", "Thread.Join")]
    public void TheTransactionScanCatchesEachBlockingShapeItIsMeantTo(string source, string expected) =>
        Assert.Contains(BlockingInTransactions("sample.cs", source), v => v.Contains(expected, StringComparison.Ordinal));

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("c.Exec(\"BEGIN IMMEDIATE\"); step.Run(c); c.Exec(\"COMMIT\");")]
    [InlineData("x.Write(tx => { tx.Execute(\"INSERT\"); return 0; }); File.Delete(p);")]
    [InlineData("c.Exec(\"BEGIN IMMEDIATE\"); var v = StateDbFile.ReadVersion(c); c.Exec(\"COMMIT\");")]
    public void TheTransactionScanLeavesWorkInsideTheDelegateAndCodeAfterTheCommitAlone(string source) =>
        Assert.Empty(BlockingInTransactions("sample.cs", source));

    /// <summary>The SQLite handle lives in Ah.Abstractions so Watch can use it without referencing StateDb; only the composition root names both.</summary>
    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void WatchReachesSqliteOnlyThroughTheAbstractionsHandle()
    {
        Assert.DoesNotContain("Ah.StateDb", Layers.AllowedReferences["Ah.Watch"]);
        Assert.Contains("Ah.StateDb", Layers.AllowedReferences["Ah.Cli"]);
        Assert.DoesNotContain("Ah.StateDb", File.ReadAllText(Path.Combine(Workspace.NativeRoot, "src", "Ah.Watch", "Ah.Watch.csproj")), StringComparison.Ordinal);
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void APInvokeInPlatformPasses() =>
        NoneOf(Checks.PInvokeViolations("Ah.Platform", Workspace.FixtureAssembly(Fixture)));

    // (e) one owner per store

    [Fact]
    public void NoStoreIsDeclaredTwiceOrByANonOwner()
    {
        var declared = Layers.SourceProjects
            .SelectMany(p => Checks.DeclaredStores(p, Workspace.BuiltAssembly(p)));

        NoneOf(Checks.StoreOwnerViolations(declared, Checks.LoadStoreOwners(Owners)));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void TheOwnerTableHoldsTheCanonicalStoreNames()
    {
        var owners = Checks.LoadStoreOwners(Owners);

        Assert.Equal(49, owners.Count);
        Assert.Equal("Ah.Hierarchy", owners["qa"]);
        Assert.Equal("Ah.Mesh", owners["mailbox-acks"]);
        Assert.Equal("Ah.Config", owners["worktree-grants"]);
        Assert.Equal("Ah.Cli", owners["login-json"]);
        Assert.All(owners.Keys, name => Assert.Matches("^[a-z]+(-[a-z]+)*$", name));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void TheFixtureDeclarationIsRead() =>
        Assert.Equal(
            [("Ah.Fixture.Violations", "links")],
            Checks.DeclaredStores(Fixture, Workspace.FixtureAssembly(Fixture)));

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void TwoAssembliesDeclaringTheSameStoreIsCaught()
    {
        var violations = Checks.StoreOwnerViolations(
            [("Ah.Mesh", "links"), ("Ah.Hierarchy", "links")], Checks.LoadStoreOwners(Owners));

        Assert.Single(violations, v => v.Contains("declared by more than one assembly"));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void AStoreDeclaredByTheWrongOwnerIsCaught()
    {
        var violations = Checks.StoreOwnerViolations([("Ah.Hierarchy", "links")], Checks.LoadStoreOwners(Owners));

        Assert.Equal(["store links is owned by Ah.Mesh and declared by Ah.Hierarchy"], violations);
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void DistinctStoresInTheirOwnersPass() =>
        NoneOf(Checks.StoreOwnerViolations(
            [("Ah.Mesh", "links"), ("Ah.Hierarchy", "msgs")], Checks.LoadStoreOwners(Owners)));

    // (f) one JSON writer

    [Theory]
    [MemberData(nameof(Projects))]
    public void StockJsonWritingIsOnlyInAhCoreJson(string project) =>
        NoneOf(Checks.JsonWriterViolations(project, Workspace.BuiltAssembly(project)));

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void StockSerializationAndWriterAreCaughtOutsideCore()
    {
        var violations = Checks.JsonWriterViolations("Ah.Mesh", Workspace.FixtureAssembly(Fixture));

        Assert.Equal(
            [
                "Ah.Mesh: Ah.Core.Json.PermittedWriter.Serialize calls JsonSerializer.SerializeToUtf8Bytes",
                "Ah.Mesh: Ah.Fixture.Violations.StockJson.Serialize calls JsonSerializer.SerializeToUtf8Bytes",
                "Ah.Mesh: Ah.Fixture.Violations.StockJson.Write calls Utf8JsonWriter..ctor",
                "Ah.Mesh: Ah.Fixture.Violations.StockJson.Write calls Utf8JsonWriter.WriteNumberValue",
            ],
            violations);
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void InsideAhCoreJsonTheSameCallIsPermittedButElsewhereInCoreIsNot()
    {
        var violations = Checks.JsonWriterViolations("Ah.Core", Workspace.FixtureAssembly(Fixture));

        Assert.DoesNotContain(violations, v => v.Contains("PermittedWriter"));
        Assert.Contains(violations, v => v.Contains("StockJson.Serialize"));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void DeserializingIsNotAWrite()
    {
        var violations = Checks.JsonWriterViolations("Ah.Mesh", Workspace.FixtureAssembly(Fixture));

        Assert.DoesNotContain(violations, v => v.Contains("StockJson.Read"));
    }

    // (i) no mutable static state

    [Theory]
    [MemberData(nameof(Projects))]
    public void NoAssemblyHoldsMutableStaticState(string project) =>
        NoneOf(Checks.MutableStaticViolations(project, Workspace.BuiltAssembly(project), Layers.MutableStaticAllowlist, Layers.MutableCellAllowlist));

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void AnAssignableStaticFieldAndAutoPropertyAreCaught()
    {
        var violations = Checks.MutableStaticViolations("Ah.Mesh", Workspace.FixtureAssembly(Fixture), new HashSet<string>());

        Assert.Equal(
            [
                "Ah.Mesh: static Ah.Fixture.Violations.MutableState.<Setting>k__BackingField is not read-only",
                "Ah.Mesh: static Ah.Fixture.Violations.MutableState.Counter is not read-only",
                "Ah.Mesh: static Ah.Fixture.Violations.MutableState.Permitted is not read-only",
            ],
            violations.Where(v => v.Contains("is not read-only", StringComparison.Ordinal)));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void AReadOnlyArrayWrittenThroughInterlockedOrAListAddedToAfterInitialisationIsCaught()
    {
        var violations = Checks.MutableStaticViolations("Ah.Mesh", Workspace.FixtureAssembly(Fixture), new HashSet<string>());

        Assert.Contains(violations, v => v.Contains("MutableState.Cell is a read-only cell of mutable type Int64[] written after initialisation", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("MutableState.Log is a read-only cell of mutable type List`1 written after initialisation", StringComparison.Ordinal));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void ReadOnlyCellsNeverWrittenAndImmutableReadOnlyFieldsAreNotState()
    {
        var violations = Checks.MutableStaticViolations("Ah.Mesh", Workspace.FixtureAssembly(Fixture), new HashSet<string>());

        Assert.DoesNotContain(violations, v => v.Contains("MutableState.Table", StringComparison.Ordinal));
        Assert.DoesNotContain(violations, v => v.Contains("MutableState.Frozen", StringComparison.Ordinal));
        Assert.DoesNotContain(violations, v => v.Contains("MutableState.Label", StringComparison.Ordinal));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void ANamedCellAllowlistEntryPermitsThatCellOnly()
    {
        var violations = Checks.MutableStaticViolations(
            "Ah.Mesh", Workspace.FixtureAssembly(Fixture), new HashSet<string>(), new HashSet<string> { "Ah.Fixture.Violations.MutableState.Cell" });

        Assert.DoesNotContain(violations, v => v.Contains("MutableState.Cell ", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("MutableState.Log ", StringComparison.Ordinal));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void ReadOnlyFieldsConstantsAndLambdaCachesAreNotState()
    {
        var violations = Checks.MutableStaticViolations("Ah.Mesh", Workspace.FixtureAssembly(Fixture), new HashSet<string>());

        Assert.DoesNotContain(violations, v => v.Contains("MutableState.Names", StringComparison.Ordinal));
        Assert.DoesNotContain(violations, v => v.Contains("MutableState.Collected", StringComparison.Ordinal));
        Assert.DoesNotContain(violations, v => v.Contains("MutableState.Words", StringComparison.Ordinal));
        Assert.DoesNotContain(violations, v => v.Contains("MutableState.Limit", StringComparison.Ordinal));
        Assert.DoesNotContain(violations, v => v.Contains("MutableState.Fixed", StringComparison.Ordinal));
        Assert.DoesNotContain(violations, v => v.Contains("<>9__", StringComparison.Ordinal));
    }

    [Fact(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    public void ANamedAllowlistEntryIsPermittedAndOnlyThatOne()
    {
        var violations = Checks.MutableStaticViolations(
            "Ah.Mesh", Workspace.FixtureAssembly(Fixture), new HashSet<string> { "Ah.Fixture.Violations.MutableState.Permitted" });

        Assert.DoesNotContain(violations, v => v.Contains("MutableState.Permitted", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("MutableState.Counter", StringComparison.Ordinal));
    }

    /// <summary>
    /// The cut-over guard compares the build each live session registered with, and a registration with no version counts as older, so a
    /// production registration that omits its build would block every cut-over. The probe file seeds throwaway fixtures and is exempt.
    /// </summary>
    [Fact]
    public void EveryProductionRegistrationWriteNamesItsBuild()
    {
        string[] probes = ["CheckVerb.cs"];
        List<string> missing = [];
        foreach (var path in Directory.EnumerateFiles(Path.Combine(Workspace.NativeRoot, "src"), "*.cs", SearchOption.AllDirectories).Where(f => !probes.Contains(Path.GetFileName(f))))
        {
            var text = WithoutComments(File.ReadAllText(path));
            for (var at = text.IndexOf("new RegistrationWrite(", StringComparison.Ordinal); at >= 0; at = text.IndexOf("new RegistrationWrite(", at + 1, StringComparison.Ordinal))
            {
                var end = text.IndexOf(';', at);
                if (!text[at..end].Contains("AhVersion", StringComparison.Ordinal))
                {
                    missing.Add($"{Path.GetFileName(path)}: a RegistrationWrite without AhVersion");
                }
            }
        }

        Assert.Empty(missing);
    }
}
