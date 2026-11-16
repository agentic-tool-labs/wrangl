using System.Text;
using Ah.Abstractions;

namespace Ah.Watch.Tests;

/// <summary>Release linkage for plan items: the five read-only git argument lists, the cache rules and the offline issue links.</summary>
public sealed class PlanLinkageTests
{
    private const string X = "1111111111111111111111111111111111111111";
    private const string Y = "2222222222222222222222222222222222222222";
    private const string Repo = "/main";

    internal sealed class FakeRepo(int holdMs = 0) : IProcessRunner
    {
        private readonly object _gate = new();
        private int _running;

        internal string TagList { get; set; } = string.Empty;

        internal Dictionary<string, string> Contains { get; } = [];

        internal Dictionary<string, string> Local { get; } = [];

        internal Dictionary<string, string> Remote { get; } = [];

        internal List<string[]> Argvs { get; } = [];

        internal int MaxConcurrent { get; private set; }

        internal int WithoutFsmonitorOff { get; private set; }

        public ProcessResult Run(ProcessRequest request)
        {
            var raw = request.Argv.ToArray();
            var guarded = raw is [_, "-c", "core.fsmonitor=false", ..];
            var argv = guarded ? [raw[0], .. raw.Skip(3)] : raw;
            lock (_gate)
            {
                WithoutFsmonitorOff += guarded ? 0 : 1;
                Argvs.Add(argv);
                _running++;
                MaxConcurrent = Math.Max(MaxConcurrent, _running);
            }

            Thread.Sleep(holdMs);
            lock (_gate)
            {
                _running--;
            }

            string Answer(Dictionary<string, string> table, string sha) => table.TryGetValue(sha, out var v) ? v : string.Empty;
            return argv[1] switch
            {
                "tag" when argv[2] == "--list" => Ok(TagList),
                "tag" => Ok(Answer(Contains, argv[3])),
                "branch" when argv[2] == "-r" => Ok(Answer(Remote, argv[4])),
                "branch" => Ok(Answer(Local, argv[3])),
                "rev-parse" => argv[3].StartsWith(X, StringComparison.Ordinal) ? Ok(X + "\n") : new ProcessResult(128, null, false, false, default, default),
                _ => new ProcessResult(2, null, false, false, default, default),
            };
        }

        internal int Lookups(string sha) => Argvs.Count(a => a.Contains(sha));
    }

    private static ProcessResult Ok(string stdout) => new(0, null, false, false, Encoding.UTF8.GetBytes(stdout), default);

    private static void Settle(GitRunner runner) => Assert.True(runner.WhenIdle().Wait(TimeSpan.FromSeconds(10)), "the runner did not go idle");

    private sealed class Clock
    {
        internal double Now { get; set; } = 1_000_000;
    }

    private static (PlanLinkage Linkage, GitRunner Runner) Make(FakeRepo repo, Clock clock, bool hasRemote = true)
    {
        var runner = new GitRunner(repo, () => clock.Now);
        return (new PlanLinkage(runner, Repo, hasRemote), runner);
    }

    /// <summary>Resolves until every read the first calls started has landed: the tag list, then the lookups.</summary>
    private static IReadOnlyList<ItemLink> Resolved(PlanLinkage linkage, GitRunner runner, params string[][] items)
    {
        IReadOnlyList<ItemLink> links = [];
        for (var i = 0; i < 3; i++)
        {
            links = linkage.Resolve(items);
            Settle(runner);
        }

        return linkage.Resolve(items);
    }

    private const string Old = "ah2-2.4.0\t2026-10-15T14:00:00-04:00\n";
    private const string New = "ah2-2.4.1\t2026-10-16T21:30:00-04:00\n";

    [Fact]
    public void P6ACommitInATagIsReleasedWithTheOldestTagThatContainsIt()
    {
        var repo = new FakeRepo { TagList = Old + New };
        repo.Contains[X] = "ah2-2.4.0\nah2-2.4.1\n";
        var (linkage, runner) = Make(repo, new Clock());

        var link = Resolved(linkage, runner, [X])[0];

        Assert.Equal("released", link.ReleaseState);
        Assert.Equal(new ReleaseTag("ah2-2.4.0", "2026-10-15T18:00:00Z"), link.Release);
    }

    [Fact]
    public void NegativeACommitOnlyInTheNewerTagIsReleasedByThatTagAndOneInNoTagIsUnreleased()
    {
        var repo = new FakeRepo { TagList = Old + New };
        repo.Contains[X] = "ah2-2.4.1\n";
        var (linkage, runner) = Make(repo, new Clock());

        var links = Resolved(linkage, runner, [X], [Y]);

        Assert.Equal("ah2-2.4.1", links[0].Release!.Tag);
        Assert.Equal("unreleased", links[1].ReleaseState);
        Assert.Null(links[1].Release);
    }

    [Fact]
    public void AnItemIsReleasedByTheOldestTagThatContainsAllItsCommits()
    {
        var repo = new FakeRepo { TagList = Old + New };
        repo.Contains[X] = "ah2-2.4.0\nah2-2.4.1\n";
        repo.Contains[Y] = "ah2-2.4.1\n";
        var (linkage, runner) = Make(repo, new Clock());

        var both = Resolved(linkage, runner, [X, Y])[0];

        Assert.Equal("ah2-2.4.1", both.Release!.Tag);
    }

    [Fact]
    public void NegativeAnItemWithOneCommitMissingFromEveryTagIsUnreleased()
    {
        var repo = new FakeRepo { TagList = Old + New };
        repo.Contains[X] = "ah2-2.4.0\nah2-2.4.1\n";
        var (linkage, runner) = Make(repo, new Clock());

        Assert.Equal("unreleased", Resolved(linkage, runner, [X, Y])[0].ReleaseState);
    }

    [Fact]
    public void P7ACommitOnALocalBranchOnlyIsUnreleasedNotPushedAndNamesTheBranch()
    {
        var repo = new FakeRepo { TagList = Old };
        repo.Local[Y] = "feat/b\n";
        var (linkage, runner) = Make(repo, new Clock());

        var link = Resolved(linkage, runner, [Y])[0];

        Assert.Equal("unreleased", link.ReleaseState);
        Assert.False(link.Pushed);
        Assert.Equal(["feat/b"], link.Branches);
    }

    [Fact]
    public void NegativeACommitOnARemoteBranchIsPushedAndARepoWithNoRemoteIsNeitherPushedNorUnpushed()
    {
        var repo = new FakeRepo { TagList = Old };
        repo.Local[Y] = "feat/b\n";
        repo.Remote[Y] = "origin/feat/b\n";
        var (linkage, runner) = Make(repo, new Clock());
        var (bare, bareRunner) = Make(new FakeRepo { TagList = Old }, new Clock(), hasRemote: false);

        Assert.True(Resolved(linkage, runner, [Y])[0].Pushed);
        Assert.Null(Resolved(bare, bareRunner, [Y])[0].Pushed);
    }

    [Fact]
    public void AnItemIsPushedOnlyWhenEveryCommitIsOnARemoteBranch()
    {
        var repo = new FakeRepo { TagList = Old };
        repo.Remote[X] = "origin/b\n";
        var (linkage, runner) = Make(repo, new Clock());

        Assert.False(Resolved(linkage, runner, [X, Y])[0].Pushed);
    }

    [Fact]
    public void BranchesAreThoseOfTheLastCommitAtMostFiveAndAnItemWithNoCommitsHasNoLink()
    {
        var repo = new FakeRepo { TagList = Old };
        repo.Local[Y] = "b1\nb2\nb3\nb4\nb5\nb6\n";
        var (linkage, runner) = Make(repo, new Clock());

        var links = Resolved(linkage, runner, [X, Y], []);

        Assert.Equal(["b1", "b2", "b3", "b4", "b5"], links[0].Branches);
        Assert.Equal(new ItemLink("no commits", null, [], null), links[1]);
    }

    [Fact]
    public void P8ANewTagReleasesAnUnreleasedCommitOnceTheTagListIsReadAgainAndNotBefore()
    {
        var clock = new Clock();
        var repo = new FakeRepo { TagList = Old };
        repo.Local[Y] = "b\n";
        var (linkage, runner) = Make(repo, clock);
        Assert.Equal("unreleased", Resolved(linkage, runner, [Y])[0].ReleaseState);
        int TagLookups() => repo.Argvs.Count(a => a[1] == "tag" && a[2] == "--contains");
        var before = TagLookups();

        repo.TagList = Old + New;
        repo.Contains[Y] = "ah2-2.4.1\n";
        clock.Now += 4 * 60_000;
        Assert.Equal("unreleased", Resolved(linkage, runner, [Y])[0].ReleaseState);
        Assert.Equal(before, TagLookups());

        clock.Now += 2 * 60_000;
        var after = Resolved(linkage, runner, [Y])[0];

        Assert.Equal("released", after.ReleaseState);
        Assert.Equal("ah2-2.4.1", after.Release!.Tag);
    }

    [Fact]
    public void NegativeAnUnreleasedCommitIsNotReadAgainWhileTheTagListIsUnchanged()
    {
        var clock = new Clock();
        var repo = new FakeRepo { TagList = Old };
        var (linkage, runner) = Make(repo, clock);
        _ = Resolved(linkage, runner, [Y]);
        var containsCalls = repo.Argvs.Count(a => a[1] == "tag" && a[2] == "--contains");

        clock.Now += 20 * 60_000;
        _ = Resolved(linkage, runner, [Y]);

        Assert.Equal(containsCalls, repo.Argvs.Count(a => a[1] == "tag" && a[2] == "--contains"));
    }

    [Fact]
    public void AReleasedCommitIsNeverAskedAboutTagsAgain()
    {
        var clock = new Clock();
        var repo = new FakeRepo { TagList = Old };
        repo.Contains[X] = "ah2-2.4.0\n";
        var (linkage, runner) = Make(repo, clock);
        _ = Resolved(linkage, runner, [X]);
        var containsCalls = repo.Argvs.Count(a => a[1] == "tag" && a[2] == "--contains");

        repo.TagList = Old + New;
        clock.Now += 3_600_000;
        var link = Resolved(linkage, runner, [X])[0];

        Assert.Equal(containsCalls, repo.Argvs.Count(a => a[1] == "tag" && a[2] == "--contains"));
        Assert.Equal("ah2-2.4.0", link.Release!.Tag);
    }

    [Fact]
    public void P9FortyUnreadCommitsLookUpThirtyTwoAndServeEightAsReadingGitWithAtMostTwoProcessesAtOnce()
    {
        var repo = new FakeRepo(holdMs: 5) { TagList = Old };
        var (linkage, runner) = Make(repo, new Clock());
        var shas = Enumerable.Range(0, 40).Select(i => i.ToString("x40")).ToList();
        IReadOnlyList<IReadOnlyList<string>> items = [.. shas.Select(s => (IReadOnlyList<string>)[s])];

        _ = linkage.Resolve(items);
        Settle(runner);
        _ = linkage.Resolve(items);
        Settle(runner);
        var looked = shas.Count(s => repo.Lookups(s) > 0);
        var maxAtOnce = repo.MaxConcurrent;
        var links = linkage.Resolve(items);

        Assert.Equal(32, looked);
        Assert.Equal(32, links.Count(l => l.ReleaseState == "unreleased"));
        Assert.Equal(8, links.Count(l => l.ReleaseState == GitRunner.ReadingReason));
        Assert.InRange(maxAtOnce, 1, GitRunner.MaxConcurrent);

        Settle(runner);
        _ = linkage.Resolve(items);
        Settle(runner);
        Assert.All(linkage.Resolve(items), l => Assert.Equal("unreleased", l.ReleaseState));
    }

    [Fact]
    public void NegativeThirtyTwoUnreadCommitsLeaveNoneReadingGit()
    {
        var repo = new FakeRepo { TagList = Old };
        var (linkage, runner) = Make(repo, new Clock());
        var items = Enumerable.Range(0, 32).Select(i => (IReadOnlyList<string>)[i.ToString("x40")]).ToArray();

        _ = linkage.Resolve(items);
        Settle(runner);
        _ = linkage.Resolve(items);
        Settle(runner);

        Assert.DoesNotContain(linkage.Resolve(items), l => l.ReleaseState == GitRunner.ReadingReason);
    }

    [Fact]
    public void P14OnlyTheFiveLinkageArgumentListsRunInTheTreeAndABadShaIsRefusedBeforeAnyArgv()
    {
        var repo = new FakeRepo { TagList = Old };
        var (linkage, runner) = Make(repo, new Clock());
        _ = Resolved(linkage, runner, [X]);
        _ = runner.Verify(Repo, X);
        Settle(runner);

        string[][] allowed =
        [
            ["git", "rev-parse", "--verify", X + "^{commit}"],
            ["git", "tag", "--list", "ah2-*", "--sort=creatordate", "--format=%(refname:short)%09%(creatordate:iso-strict)"],
            ["git", "tag", "--contains", X, "--list", "ah2-*"],
            ["git", "branch", "--contains", X, "--format=%(refname:short)"],
            ["git", "branch", "-r", "--contains", X, "--format=%(refname:short)"],
        ];
        Assert.Equal(5, repo.Argvs.Count);
        Assert.Equal(0, repo.WithoutFsmonitorOff);
        Assert.All(repo.Argvs, a => Assert.Contains(allowed, ok => ok.SequenceEqual(a)));

        var before = repo.Argvs.Count;
        foreach (var bad in new[] { "--upload-pack=x", "-1234567", "ABCDEF1", "abcdef", new string('a', 41), "abc def1", string.Empty })
        {
            Assert.Equal(GitRunner.InvalidShaReason, runner.Verify(Repo, bad).Reason);
            Assert.Equal(GitRunner.InvalidShaReason, runner.TagsIn(Repo, bad, "").Reason);
            Assert.Equal(GitRunner.InvalidShaReason, runner.BranchesIn(Repo, bad).Reason);
            Assert.Equal(GitRunner.InvalidShaReason, runner.RemoteBranchesIn(Repo, bad).Reason);
        }

        Settle(runner);
        Assert.Equal(before, repo.Argvs.Count);
    }

    [Fact]
    public void NegativeTheSevenAndFortyCharacterBoundariesAreShasAndSixAndFortyOneAreNot()
    {
        Assert.True(GitRunner.IsSha("abcdef0"));
        Assert.True(GitRunner.IsSha(new string('a', 40)));
        Assert.False(GitRunner.IsSha("abcdef"));
        Assert.False(GitRunner.IsSha(new string('a', 41)));
    }

    [Fact]
    public void VerifyAnswersTheShaForACommitThatExistsAndNotFoundForOneThatDoesNot()
    {
        var repo = new FakeRepo();
        var runner = new GitRunner(repo, () => 1_000_000);

        _ = runner.Verify(Repo, X);
        _ = runner.Verify(Repo, Y);
        Settle(runner);

        Assert.Equal(X, runner.Verify(Repo, X).Value);
        var missing = runner.Verify(Repo, Y);
        Assert.Null(missing.Value);
        Assert.Equal("not found", missing.Reason);
    }

    [Fact]
    public void ReleasesListTheMatchingTagsNewestFirstAndSayWhyWhenThereAreNone()
    {
        var repo = new FakeRepo { TagList = Old + New + "garbage line\n" };
        var (linkage, runner) = Make(repo, new Clock());
        _ = linkage.Releases();
        Settle(runner);

        var (releases, reason) = linkage.Releases();

        Assert.Null(reason);
        Assert.Equal(["ah2-2.4.1", "ah2-2.4.0"], releases.Select(r => r.Tag));
        Assert.Equal("2026-10-17T01:30:00Z", releases[0].Date);
        Assert.Equal(GitRunner.ReadingReason, new PlanLinkage(new GitRunner(new FakeRepo(), () => 1), Repo, true).Releases().Reason);
    }

    [Theory]
    [InlineData("https://github.com/example/agent-tools.git", "https://github.com/example/agent-tools/issues/77")]
    [InlineData("https://github.com/example/agent-tools", "https://github.com/example/agent-tools/issues/77")]
    [InlineData("git@github.com:example/agent-tools.git", "https://github.com/example/agent-tools/issues/77")]
    [InlineData("ssh://git@github.com/example/agent-tools.git", "https://github.com/example/agent-tools/issues/77")]
    [InlineData("https://gitlab.com/example/agent-tools.git", null)]
    [InlineData("https://github.com.evil.example/example/repo.git", null)]
    [InlineData("/srv/git/agent-tools.git", null)]
    [InlineData(null, null)]
    public void IssueUrlIsBuiltOfflineFromAGitHubOriginAndIsNullForAnythingElse(string? origin, string? expected) =>
        Assert.Equal(expected, PlanLinkage.IssueUrl(origin, 77));

    [Fact]
    public void TheOriginUrlAndTheRemotePresenceAreReadFromAGitConfigText()
    {
        const string config = "[core]\n\tbare = false\n[remote \"upstream\"]\n\turl = https://github.com/other/repo.git\n[remote \"origin\"]\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n\turl = git@github.com:example/repo.git\n[branch \"main\"]\n\turl = nope\n";

        Assert.Equal("git@github.com:example/repo.git", PlanLinkage.OriginUrl(config));
        Assert.True(PlanLinkage.HasRemote(config));
    }

    [Fact]
    public void NegativeAConfigWithNoRemoteHasNoOriginUrlAndNoRemote()
    {
        const string config = "[core]\n\tbare = false\n[branch \"main\"]\n\turl = x\n";

        Assert.Null(PlanLinkage.OriginUrl(config));
        Assert.False(PlanLinkage.HasRemote(config));
        Assert.Null(PlanLinkage.OriginUrl("[remote \"upstream\"]\n\turl = https://github.com/a/b.git\n"));
    }
}
