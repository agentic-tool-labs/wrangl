using Ah.Core.Json;

namespace Ah.Commands.Tests;

/// <summary>Issue intake: its arguments, the origin and refs it accepts, the eligibility gate, the two reads, and what it leaves on disk.</summary>
public sealed class IssueIntakeTests
{
    private static JsonObject Parse(string stdout) => JsonText.Parse(stdout).AsObject();

    private static string Error(string stdout) => Parse(stdout)["error"].AsString();

    private static JsonObject ItemOf(string stdout, int index = 0) => Parse(stdout)["items"].AsArray()[index].AsObject();

    // ---------------------------------------------------------------- arguments

    [Theory]
    [InlineData("--bogus", "usage: Unknown option '--bogus'")]
    [InlineData("-x", "usage: Unknown option '-x'")]
    [InlineData("--bogus=1", "usage: Unknown option '--bogus'")]
    [InlineData("--=x", "usage: Unknown option '--=x'")]
    [InlineData("foo", "usage: Unexpected argument 'foo'. This command does not take positional arguments")]
    [InlineData("-", "usage: Unexpected argument '-'. This command does not take positional arguments")]
    [InlineData("--cwd", "usage: Option '--cwd <value>' argument missing")]
    [InlineData("--issues", "usage: Option '--issues <value>' argument missing")]
    [InlineData("--labelled=yes", "usage: Option '--labelled' does not take an argument")]
    [InlineData("--help=1", "usage: Option '-h, --help' does not take an argument")]
    [InlineData("-hx", "usage: Unknown option '-x'")]
    public void AnArgumentProblemIsReportedAsUsageTheWayParseArgsWordsIt(string argument, string expected)
    {
        using var rig = new IntakeRig();

        Assert.Equal(expected, Error(rig.RunRaw([argument])));
    }

    [Theory]
    [InlineData("--cwd --out-dir x", "usage: Option '--cwd' argument is ambiguous.\nDid you forget to specify the option argument for '--cwd'?\nTo specify an option argument starting with a dash use '--cwd=-XYZ'.")]
    [InlineData("--issues -h", "usage: Option '--issues' argument is ambiguous.\nDid you forget to specify the option argument for '--issues'?\nTo specify an option argument starting with a dash use '--issues=-XYZ'.")]
    public void AnOptionValueThatLooksLikeAnOptionIsAmbiguous(string arguments, string expected)
    {
        using var rig = new IntakeRig();

        Assert.Equal(expected, Error(rig.RunRaw([.. arguments.Split(' ')])));
    }

    [Fact]
    public void AnInlineValueMayStartWithADashAndALoneDashIsAValue()
    {
        using var rig = new IntakeRig();
        rig.Origin = null;

        var inline = Error(rig.RunRaw(["--cwd=-x", "--out-dir=-y", "--labelled"]));
        var lone = Error(rig.RunRaw(["--cwd", "-", "--out-dir", "/o", "--labelled"]));

        Assert.Equal("origin-not-github: no origin remote", inline);
        Assert.Equal("origin-not-github: no origin remote", lone);
    }

    [Fact]
    public void HelpPrintsTheUsageTextInTheNativeFormAndExitsZero()
    {
        using var rig = new IntakeRig();

        var (code, stdout) = rig.Run("--help");

        Assert.Equal(0, code);
        Assert.StartsWith("usage: ah issue-intake --cwd <abs> --out-dir <abs> (--issues <ref,ref,...> | --labelled)\n", stdout, StringComparison.Ordinal);
        Assert.EndsWith("a run-level failure prints {\"error\": \"<code>: <detail>\"}, exit 1.\n", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AShortHelpAfterOtherOptionsStillPrintsTheUsage()
    {
        using var rig = new IntakeRig();

        var (code, stdout) = rig.Run("--labelled", "-h");

        Assert.Equal(0, code);
        Assert.StartsWith("usage: ", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--cwd=/x --labelled", "usage: --cwd and --out-dir are required")]
    [InlineData("--out-dir=/x --labelled", "usage: --cwd and --out-dir are required")]
    [InlineData("--cwd= --out-dir=/x --labelled", "usage: --cwd and --out-dir are required")]
    [InlineData("--cwd=/x --out-dir=/y", "usage: give exactly one of --issues and --labelled")]
    [InlineData("--cwd=/x --out-dir=/y --issues=1 --labelled", "usage: give exactly one of --issues and --labelled")]
    [InlineData("--cwd=/x --out-dir=/y --issues=", "usage: give exactly one of --issues and --labelled")]
    public void TheRequiredOptionsAndTheOneSourceAreChecked(string arguments, string expected)
    {
        using var rig = new IntakeRig();

        Assert.Equal(expected, Error(rig.RunRaw([.. arguments.Split(' ')])));
    }

    // ---------------------------------------------------------------- origin and conventions

    [Theory]
    [InlineData("https://github.com/alice/proj.git", "alice/proj")]
    [InlineData("https://github.com/alice/proj", "alice/proj")]
    [InlineData("https://github.com/alice/proj/", "alice/proj")]
    [InlineData("https://github.com/alice/proj.git/", "alice/proj")]
    [InlineData("HTTPS://GitHub.com/Alice/Proj.GIT/", "Alice/Proj")]
    [InlineData("https://tok@github.com/alice/proj.git", "alice/proj")]
    [InlineData("https://github.com@github.com/alice/proj", "alice/proj")]
    [InlineData("git@github.com:alice/proj.git", "alice/proj")]
    [InlineData("ssh://git@github.com/alice/proj/", "alice/proj")]
    [InlineData("https://github.com/alice/a.git.git", "alice/a.git")]
    [InlineData("https://github.com/alice/.git", "alice/.git")]
    public void AGitHubOriginYieldsItsOwnerAndRepo(string origin, string repo)
    {
        using var rig = new IntakeRig { Origin = origin };
        rig.Issues[1] = IntakeIssue.Eligible();

        var (code, stdout) = rig.Run("--issues", "1");

        Assert.Equal(0, code);
        Assert.Equal(repo, Parse(stdout)["repo"].AsString());
    }

    [Theory]
    [InlineData("https://gitlab.com/alice/proj.git")]
    [InlineData("https://github.com/alice")]
    [InlineData("https://github.com/alice/")]
    [InlineData("https://github.com/alice/proj/extra")]
    [InlineData("https://github.com/alice/proj//")]
    [InlineData("https://github.com/")]
    [InlineData("http://github.com/alice/proj")]
    [InlineData("https://a@b@github.com/alice/proj")]
    [InlineData("https://x/y@github.com/alice/proj")]
    [InlineData("https://@github.com/alice/proj")]
    [InlineData("git@github.com:alice")]
    [InlineData("git@gitlab.com:alice/proj.git")]
    [InlineData("ssh://git@github.com:alice/proj")]
    [InlineData("/srv/git/proj.git")]
    public void AnOriginThatIsNotAGitHubRepoIsRefusedNamingIt(string origin)
    {
        using var rig = new IntakeRig { Origin = origin };

        var (code, stdout) = rig.Run("--issues", "1");

        Assert.Equal(1, code);
        Assert.Equal("origin-not-github: " + origin, Error(stdout));
        Assert.Empty(rig.GhArgv);
    }

    [Fact]
    public void NoOriginAtAllIsNamedAsSuch()
    {
        using var rig = new IntakeRig { Origin = null };

        Assert.Equal("origin-not-github: no origin remote", Error(rig.Run("--issues", "1").Stdout));
    }

    [Fact]
    public void ConventionsAbsentInvalidAndWithoutALabelAreEachRefused()
    {
        using var absent = new IntakeRig { ConventionsAbsent = true };
        using var invalid = new IntakeRig { Conventions = "{\"version\":1,\"issues\":{\"bogus\":true}}" };
        using var unlabelled = new IntakeRig { Conventions = "{\"version\":1,\"issues\":{\"trusted_actors\":[\"alice\"]}}" };
        using var emptyLabel = new IntakeRig { Conventions = "{\"version\":1,\"issues\":{\"trigger_label\":\"\"}}" };

        Assert.Equal("conventions-absent: no .claude/ah2/ah-conventions.json on origin's default branch", Error(absent.Run("--issues", "1").Stdout));
        Assert.StartsWith("conventions-invalid: ", Error(invalid.Run("--issues", "1").Stdout), StringComparison.Ordinal);
        Assert.Equal("no-trigger-label: set issues.trigger_label in .claude/ah2/ah-conventions.json", Error(unlabelled.Run("--issues", "1").Stdout));
        Assert.StartsWith("conventions-invalid: ", Error(emptyLabel.Run("--issues", "1").Stdout), StringComparison.Ordinal);
    }

    [Fact]
    public void GhNotInstalledAndNotAuthenticatedAreDistinctFailures()
    {
        using var missing = new IntakeRig { GhMissing = true };
        using var unauthenticated = new IntakeRig { AuthOk = false };

        Assert.Equal("gh-unavailable: gh is not installed", Error(missing.Run("--issues", "1").Stdout));
        Assert.Equal("gh-unavailable: `gh auth status --hostname github.com` failed", Error(unauthenticated.Run("--issues", "1").Stdout));
    }

    [Fact]
    public void AuthIsCheckedForGitHubDotComAndBeforeAnyIssueIsRead()
    {
        using var rig = new IntakeRig();
        rig.Issues[1] = IntakeIssue.Eligible();

        _ = rig.Run("--issues", "1");

        Assert.Equal(["auth", "status", "--hostname", "github.com"], rig.GhArgv[0]);
        Assert.Equal(["api", "graphql", "--input", "-"], rig.GhArgv[1]);
    }

    // ---------------------------------------------------------------- trusted actors

    [Fact]
    public void WithoutTrustedActorsTheUserOwnerIsTheOnlyOneTrusted()
    {
        using var rig = new IntakeRig();
        var issue = IntakeIssue.Eligible();
        issue.Events[0] = ("LabeledEvent", "2026-09-20T10:00:00Z", "bob", "ready");
        rig.Issues[1] = issue;

        var result = Parse(rig.Run("--issues", "1").Stdout);

        Assert.Equal("alice", result["trusted_actors"].AsArray().Single().AsString());
        Assert.Equal("untrusted-labeler", ItemOf(rig.Run("--issues", "1").Stdout)["reason"].AsString());
    }

    [Fact]
    public void AnOrganizationOwnerNeedsListedTrustedActors()
    {
        using var rig = new IntakeRig { OwnerType = "Organization", OwnerLogin = "acme" };

        Assert.Equal(
            "org-needs-trusted-actors: acme is an organization; set issues.trusted_actors in .claude/ah2/ah-conventions.json",
            Error(rig.Run("--issues", "1").Stdout));
    }

    [Fact]
    public void ListedTrustedActorsAreComparedWithoutCaseAndSkipTheOwnerLookup()
    {
        using var rig = new IntakeRig
        {
            Conventions = "{\"version\":1,\"issues\":{\"trigger_label\":\"ready\",\"trusted_actors\":[\"ALICE\",\"Bob\"]}}",
            OwnerType = "Organization",
        };
        var issue = IntakeIssue.Eligible();
        issue.Events[0] = ("LabeledEvent", "2026-09-20T10:00:00Z", "bOb", "ready");
        rig.Issues[1] = issue;

        var result = Parse(rig.Run("--issues", "1").Stdout);

        Assert.True(result["items"].AsArray()[0].AsObject()["eligible"].AsBool());
        Assert.Equal(["ALICE", "Bob"], result["trusted_actors"].AsArray().Select(a => a.AsString()).ToArray());
        Assert.DoesNotContain(rig.GhRequests, r => r.Contains("IntakeOwner", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnreadableOwnerFailsTheRunAndLogsGhsFirstErrorLine()
    {
        using var rig = new IntakeRig { OwnerUnreadable = true };

        var (code, stdout) = rig.Run("--issues", "1");

        Assert.Equal(1, code);
        Assert.Equal("intake-failed: reading the repo owner failed", Error(stdout));
        var row = Assert.Single(rig.Errors);
        Assert.Equal("issue-intake", row.Hook);
        Assert.Equal("reading the repo owner failed: gh exit 1: owner boom", row.Message);
        Assert.True(row.Stack is null or { Count: 0 });
    }

    // ---------------------------------------------------------------- refs

    [Fact]
    public void RefsKeepTheGivenOrderAndDropDuplicatesAcrossForms()
    {
        using var rig = new IntakeRig();
        rig.Issues[1] = IntakeIssue.Eligible();
        rig.Issues[6] = IntakeIssue.Eligible();

        var result = Parse(rig.Run("--issues", " 6, #1 ,https://github.com/alice/proj/issues/6,1,99,06").Stdout);

        var items = result["items"].AsArray().Select(i => (int)i.AsObject()["issue"].AsNumber()).ToArray();
        Assert.Equal([6, 1, 99], items);
        Assert.Equal("not-found", result["items"].AsArray()[2].AsObject()["reason"].AsString());
    }

    [Fact]
    public void AnOtherRepoRefIsNeverQueriedAndNeverTouchesTheSnapshotOfTheSameNumber()
    {
        using var rig = new IntakeRig();
        var closed = IntakeIssue.Eligible();
        closed.State = "CLOSED";
        rig.Issues[1] = closed;
        Directory.CreateDirectory(rig.Out);
        File.WriteAllText(rig.SnapshotPath(1), "stale");

        var other = Parse(rig.Run("--issues", "https://github.com/OTHER/repo/issues/1").Stdout)["items"].AsArray()[0].AsObject();

        Assert.Equal("other-repo", other["reason"].AsString());
        Assert.True(File.Exists(rig.SnapshotPath(1)));
        Assert.DoesNotContain(rig.GhRequests, r => r.Contains("query IntakeMeta", StringComparison.Ordinal));

        var local = Parse(rig.Run("--issues", "HTTPS://GITHUB.COM/alice/PROJ/issues/1/").Stdout)["items"].AsArray()[0].AsObject();

        Assert.Equal("closed", local["reason"].AsString());
        Assert.False(File.Exists(rig.SnapshotPath(1)));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1,abc")]
    [InlineData("#")]
    [InlineData("1.5")]
    [InlineData("https://github.com/alice/proj/issues/x")]
    [InlineData("https://github.com/alice/proj/pull/1")]
    [InlineData("https://github.com/alice/issues/1")]
    [InlineData("github.com/alice/proj/issues/1")]
    public void ARefThatIsNotAnIssueRefIsAUsageFailure(string list)
    {
        using var rig = new IntakeRig();

        var (code, stdout) = rig.Run("--issues", list);

        Assert.Equal(1, code);
        var shown = list.Split(',').Select(s => s.Trim()).First(s => s.Length > 0 && s != "1");
        Assert.Equal("usage: not an issue ref: " + shown, Error(stdout));
    }

    [Fact]
    public void AnEmptyListOfRefsIsAnEmptyRun()
    {
        using var rig = new IntakeRig();

        var result = Parse(rig.Run("--issues", ",, ,").Stdout);

        Assert.Empty(result["items"].AsArray());
    }

    [Fact]
    public void LabelledIssuesAreListedAcrossPagesInAscendingOrder()
    {
        using var rig = new IntakeRig();
        rig.LabelledPages.Add([10, 6]);
        rig.LabelledPages.Add([1]);
        foreach (var n in new[] { 1, 6, 10 })
        {
            rig.Issues[n] = IntakeIssue.Eligible();
        }

        var result = Parse(rig.Run("--labelled").Stdout);

        Assert.Equal([1, 6, 10], result["items"].AsArray().Select(i => (int)i.AsObject()["issue"].AsNumber()).ToArray());
    }

    // ---------------------------------------------------------------- the gate

    [Fact]
    public void AClosedIssueAndOneWithoutTheLabelAreNotEligible()
    {
        using var rig = new IntakeRig();
        var closed = IntakeIssue.Eligible();
        closed.State = "CLOSED";
        var unlabelled = IntakeIssue.Eligible();
        unlabelled.Labels.Clear();
        unlabelled.Labels.Add("bug");
        rig.Issues[1] = closed;
        rig.Issues[2] = unlabelled;

        var result = Parse(rig.Run("--issues", "1,2").Stdout);

        Assert.Equal("closed", result["items"].AsArray()[0].AsObject()["reason"].AsString());
        Assert.Equal("no-trigger-label", result["items"].AsArray()[1].AsObject()["reason"].AsString());
        Assert.DoesNotContain(rig.GhRequests, r => r.Contains("query IntakeFetch", StringComparison.Ordinal));
    }

    [Fact]
    public void TheLatestTriggerLabelEventDecidesWhoLabelled()
    {
        using var rig = new IntakeRig();
        var issue = IntakeIssue.Eligible();
        issue.Events.Clear();
        issue.Events.Add(("LabeledEvent", "2026-09-20T10:00:00Z", "mallory", "ready"));
        issue.Events.Add(("LabeledEvent", "2026-09-20T12:00:00Z", "alice", "ready"));
        issue.Events.Add(("LabeledEvent", "2026-09-20T13:00:00Z", "mallory", "other"));
        rig.Issues[1] = issue;
        var reversed = IntakeIssue.Eligible();
        reversed.Events.Clear();
        reversed.Events.Add(("LabeledEvent", "2026-09-20T12:00:00Z", "alice", "ready"));
        reversed.Events.Add(("LabeledEvent", "2026-09-20T10:00:00Z", "mallory", "ready"));
        rig.Issues[2] = reversed;

        var result = Parse(rig.Run("--issues", "1,2").Stdout);

        Assert.True(result["items"].AsArray()[0].AsObject()["eligible"].AsBool());
        Assert.True(result["items"].AsArray()[1].AsObject()["eligible"].AsBool());
    }

    [Fact]
    public void WhenTwoLabelEventsShareASecondTheLaterListedOneDecides()
    {
        using var rig = new IntakeRig();
        var laterIsAlice = IntakeIssue.Eligible();
        laterIsAlice.Events.Clear();
        laterIsAlice.Events.Add(("LabeledEvent", "2026-09-20T10:00:00Z", "mallory", "ready"));
        laterIsAlice.Events.Add(("LabeledEvent", "2026-09-20T10:00:00Z", "alice", "ready"));
        var laterIsMallory = IntakeIssue.Eligible();
        laterIsMallory.Events.Clear();
        laterIsMallory.Events.Add(("LabeledEvent", "2026-09-20T10:00:00Z", "alice", "ready"));
        laterIsMallory.Events.Add(("LabeledEvent", "2026-09-20T10:00:00Z", "mallory", "ready"));
        rig.Issues[1] = laterIsAlice;
        rig.Issues[2] = laterIsMallory;

        var result = Parse(rig.Run("--issues", "1,2").Stdout);

        Assert.True(result["items"].AsArray()[0].AsObject()["eligible"].AsBool());
        Assert.Equal("untrusted-labeler", result["items"].AsArray()[1].AsObject()["reason"].AsString());
    }

    [Fact]
    public void ARenameInTheSameSecondAsTheLabelIsNotAfterIt()
    {
        using var rig = new IntakeRig();
        var issue = IntakeIssue.Eligible();
        issue.Events.Add(("RenamedTitleEvent", "2026-09-20T10:00:00Z", null, null));
        rig.Issues[1] = issue;

        Assert.True(ItemOf(rig.Run("--issues", "1").Stdout)["eligible"].AsBool());
    }

    [Fact]
    public void ACommentEditedAtTheSecondOfTheLabelIsKeptAndOneEditedBeforeIt()
    {
        using var rig = new IntakeRig();
        var issue = IntakeIssue.Eligible();
        issue.Comments.Add(new IntakeComment("IC_a", "alice", "2026-09-20T11:00:00Z", "<p>at</p>") { LastEditedAt = "2026-09-20T10:00:00Z" });
        issue.Comments.Add(new IntakeComment("IC_b", "alice", "2026-09-20T11:10:00Z", "<p>before</p>") { LastEditedAt = "2026-09-20T09:59:59Z" });
        rig.Issues[1] = issue;

        _ = rig.Run("--issues", "1");

        var text = File.ReadAllText(rig.SnapshotPath(1));
        Assert.Contains("| at\n", text, StringComparison.Ordinal);
        Assert.Contains("| before\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingIssueIsRecognisedFromGhsWordsWhenNoJsonCameBack()
    {
        using var an = new IntakeRig { MetaErrorOnly = "gh: Could not resolve to an Issue with the number of 1.\n" };
        using var a = new IntakeRig { MetaErrorOnly = "COULD NOT RESOLVE TO A ISSUE" };
        using var other = new IntakeRig { MetaErrorOnly = "HTTP 502\n" };

        Assert.Equal("not-found", ItemOf(an.Run("--issues", "1").Stdout)["reason"].AsString());
        Assert.Equal("not-found", ItemOf(a.Run("--issues", "1").Stdout)["reason"].AsString());
        Assert.Equal("intake-failed: reading issue #1 failed", Error(other.Run("--issues", "1").Stdout));
    }

    [Fact]
    public void ALabelEventOfAnotherLabelDoesNotCountAndAMissingOneFailsClosed()
    {
        using var rig = new IntakeRig();
        var other = IntakeIssue.Eligible();
        other.Events.Clear();
        other.Events.Add(("LabeledEvent", "2026-09-20T10:00:00Z", "alice", "bug"));
        var none = IntakeIssue.Eligible();
        none.Events.Clear();
        var ghost = IntakeIssue.Eligible();
        ghost.Events[0] = ("LabeledEvent", "2026-09-20T10:00:00Z", null, "ready");
        rig.Issues[1] = other;
        rig.Issues[2] = none;
        rig.Issues[3] = ghost;

        var result = Parse(rig.Run("--issues", "1,2,3").Stdout);

        Assert.All(result["items"].AsArray(), i => Assert.Equal("untrusted-labeler", i.AsObject()["reason"].AsString()));
    }

    [Theory]
    [InlineData("2026-09-20T10:00:01Z", false)]
    [InlineData("2026-09-20T10:00:00Z", true)]
    [InlineData("2026-09-19T09:00:00Z", true)]
    public void AnEditAfterTheLabelMakesTheIssueIneligibleButAnEditAtTheSecondDoesNot(string editedAt, bool eligible)
    {
        using var rig = new IntakeRig();
        var issue = IntakeIssue.Eligible();
        issue.LastEditedAt = editedAt;
        rig.Issues[1] = issue;

        var item = ItemOf(rig.Run("--issues", "1").Stdout);

        Assert.Equal(eligible, item["eligible"].AsBool());
        if (!eligible)
        {
            Assert.Equal("edited-after-label", item["reason"].AsString());
        }
    }

    [Fact]
    public void ARenameAfterTheLabelMakesTheIssueIneligible()
    {
        using var rig = new IntakeRig();
        var issue = IntakeIssue.Eligible();
        issue.Events.Add(("RenamedTitleEvent", "2026-09-20T10:00:01Z", null, null));
        var before = IntakeIssue.Eligible();
        before.Events.Insert(0, ("RenamedTitleEvent", "2026-09-20T09:59:59Z", null, null));
        rig.Issues[1] = issue;
        rig.Issues[2] = before;

        var result = Parse(rig.Run("--issues", "1,2").Stdout);

        Assert.Equal("edited-after-label", result["items"].AsArray()[0].AsObject()["reason"].AsString());
        Assert.True(result["items"].AsArray()[1].AsObject()["eligible"].AsBool());
    }

    // ---------------------------------------------------------------- comments and the second read

    [Fact]
    public void OnlyTrustedCommentsNotEditedAfterTheLabelAreFetchedAndShown()
    {
        using var rig = new IntakeRig();
        var issue = IntakeIssue.Eligible();
        issue.Comments.Add(new IntakeComment("IC_a", "mallory", "2026-09-20T11:00:00Z", "<p>evil</p>"));
        issue.Comments.Add(new IntakeComment("IC_b", "alice", "2026-09-20T11:30:00Z", "<p>good</p>"));
        issue.Comments.Add(new IntakeComment("IC_c", "alice", "2026-09-20T12:00:00Z", "<p>late</p>") { LastEditedAt = "2026-09-20T10:00:01Z" });
        issue.Comments.Add(new IntakeComment("IC_d", "ALICE", "2026-09-20T12:30:00Z", "<p>edited at the label</p>") { LastEditedAt = "2026-09-20T10:00:00Z" });
        rig.Issues[1] = issue;

        var item = ItemOf(rig.Run("--issues", "1").Stdout);

        Assert.True(item["eligible"].AsBool());
        var text = File.ReadAllText(rig.SnapshotPath(1));
        Assert.Contains("COMMENT alice 2026-09-20T11:30:00Z:\n| good\n", text, StringComparison.Ordinal);
        Assert.Contains("COMMENT ALICE 2026-09-20T12:30:00Z:\n| edited at the label\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", text, StringComparison.Ordinal);
        Assert.DoesNotContain("late", text, StringComparison.Ordinal);
        var fetch = Assert.Single(rig.GhRequests, r => r.Contains("query IntakeFetch", StringComparison.Ordinal));
        Assert.Contains("\"ids\":[\"IC_b\",\"IC_d\"]", fetch, StringComparison.Ordinal);
        Assert.DoesNotContain("IC_a", fetch, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("issue-edited")]
    [InlineData("issue-gone")]
    [InlineData("comment-edited")]
    [InlineData("comment-missing")]
    [InlineData("label-removed")]
    [InlineData("untrusted-relabel")]
    public void AnIssueThatChangesBetweenTheReadsIsEditedDuringIntakeAndLeavesNoFile(string change)
    {
        using var rig = new IntakeRig();
        var issue = IntakeIssue.Eligible();
        var comment = new IntakeComment("IC_a", "alice", "2026-09-20T11:00:00Z", "<p>c</p>");
        issue.Comments.Add(comment);
        switch (change)
        {
            case "issue-edited":
                issue.ChangedOnFetch = true;
                issue.FetchLastEditedAt = "2026-09-19T09:00:01Z";
                break;
            case "issue-gone":
                issue.GoneOnFetch = true;
                break;
            case "comment-edited":
                comment.ChangedOnFetch = true;
                comment.FetchLastEditedAt = "2026-09-20T10:00:00Z";
                break;
            case "comment-missing":
                comment.MissingOnFetch = true;
                break;
            case "label-removed":
                issue.FetchEvents = [];
                break;
            default:
                issue.FetchEvents = [.. issue.Events, ("LabeledEvent", "2026-09-20T10:30:00Z", "mallory", "ready")];
                break;
        }

        rig.Issues[1] = issue;

        var (code, stdout) = rig.Run("--issues", "1");

        Assert.Equal(0, code);
        Assert.Equal("edited-during-intake", ItemOf(stdout)["reason"].AsString());
        Assert.False(File.Exists(rig.SnapshotPath(1)));
    }

    [Fact]
    public void ACommentThatCannotBeFetchedAgainIsAChangeEvenWhenItNeverHadAnEditTime()
    {
        using var rig = new IntakeRig();
        var issue = IntakeIssue.Eligible();
        issue.Comments.Add(new IntakeComment("IC_a", "alice", "2026-09-20T11:00:00Z", "<p>c</p>") { OmitLastEditedAt = true, MissingOnFetch = true });
        rig.Issues[1] = issue;

        var item = ItemOf(rig.Run("--issues", "1").Stdout);

        Assert.Equal("edited-during-intake", item["reason"].AsString());
    }

    [Fact]
    public void ATrustedRelabelBetweenTheReadsIsStillEligibleWithTheFirstReadsHeader()
    {
        using var rig = new IntakeRig();
        var issue = IntakeIssue.Eligible();
        issue.FetchEvents = [.. issue.Events, ("LabeledEvent", "2026-09-20T09:00:00Z", "alice", "ready")];
        rig.Issues[1] = issue;

        var item = ItemOf(rig.Run("--issues", "1").Stdout);

        Assert.True(item["eligible"].AsBool());
        Assert.StartsWith(
            "UNTRUSTED ISSUE SNAPSHOT alice/proj#1 labelled-by=alice at=2026-09-20T10:00:00Z fetched=2026-10-12T08:00:00.000Z\n",
            File.ReadAllText(rig.SnapshotPath(1)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedFetchIsARunLevelFailureCarryingNeitherGhStream()
    {
        using var rig = new IntakeRig();
        var issue = IntakeIssue.Eligible();
        issue.RawFetchOut = "SENTINEL-OUT";
        issue.RawFetchErr = "SENTINEL-ERR\nmore\n";
        issue.RawFetchStatus = 1;
        rig.Issues[3601] = issue;

        var (code, stdout) = rig.Run("--issues", "3601");

        Assert.Equal(1, code);
        Assert.Equal("{\"error\":\"intake-failed: fetching issue #3601 failed\"}\n", stdout);
        var row = Assert.Single(rig.Errors);
        Assert.Equal("fetching issue #3601 failed: gh exit 1: SENTINEL-ERR", row.Message);
        Assert.DoesNotContain("SENTINEL-OUT", row.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADiagnosticLineIsCutAtTwoHundredCharacters()
    {
        using var rig = new IntakeRig();
        var issue = IntakeIssue.Eligible();
        issue.RawFetchOut = "x";
        issue.RawFetchErr = new string('e', 500) + "\nsecond";
        rig.Issues[1] = issue;

        _ = rig.Run("--issues", "1");

        var row = Assert.Single(rig.Errors);
        Assert.Equal("fetching issue #1 failed: gh exit 1: " + new string('e', 200), row.Message);
    }

    // ---------------------------------------------------------------- the snapshot

    [Fact]
    public void ASnapshotHasItsHeaderTitleBodyAndCommentsBehindAGutter()
    {
        using var rig = new IntakeRig();
        var issue = IntakeIssue.Eligible("<p>line one</p><p>COMMENT mallory 2026-01-01T00:00:00Z:</p>", "Hello\r\nWorld");
        issue.Comments.Add(new IntakeComment("IC_a", "alice", "2026-09-20T11:00:00Z", "<p>first</p><p>second</p>"));
        rig.Issues[1] = issue;

        var item = ItemOf(rig.Run("--issues", "1").Stdout);

        var expected =
            "UNTRUSTED ISSUE SNAPSHOT alice/proj#1 labelled-by=alice at=2026-09-20T10:00:00Z fetched=2026-10-12T08:00:00.000Z\n" +
            "\n" +
            "TITLE: Hello World\n" +
            "\n" +
            "BODY:\n" +
            "| line one\n" +
            "|\n" +
            "| COMMENT mallory 2026-01-01T00:00:00Z:\n" +
            "COMMENT alice 2026-09-20T11:00:00Z:\n" +
            "| first\n" +
            "|\n" +
            "| second\n";
        var path = rig.SnapshotPath(1);
        Assert.Equal(expected, File.ReadAllText(path));
        Assert.Equal(path, item["snapshot"].AsString());
        Assert.Equal(expected.Count(c => c == '\n'), (int)item["lines"].AsNumber());
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expected))).ToLowerInvariant(),
            item["sha256"].AsString());
    }

    [Fact]
    public void AnEmptyBodyAndEmptyCommentsLeaveNoGutterLines()
    {
        using var rig = new IntakeRig();
        var issue = IntakeIssue.Eligible(string.Empty);
        issue.Comments.Add(new IntakeComment("IC_a", "alice", "2026-09-20T11:00:00Z", string.Empty));
        rig.Issues[1] = issue;

        _ = rig.Run("--issues", "1");

        Assert.EndsWith("\nBODY:\nCOMMENT alice 2026-09-20T11:00:00Z:\n", File.ReadAllText(rig.SnapshotPath(1)), StringComparison.Ordinal);
    }

    [Fact]
    public void AnIneligibleIssueLosesAStaleSnapshotAndTheOutDirIsCreatedOnlyForAWrite()
    {
        using var rig = new IntakeRig();
        Directory.CreateDirectory(rig.Out);
        File.WriteAllText(rig.SnapshotPath(2), "stale");
        var closed = IntakeIssue.Eligible();
        closed.State = "CLOSED";
        rig.Issues[2] = closed;

        var (code, stdout) = rig.Run("--issues", "2");

        Assert.Equal(0, code);
        Assert.Equal("closed", ItemOf(stdout)["reason"].AsString());
        Assert.False(File.Exists(rig.SnapshotPath(2)));
    }

    [Fact]
    public void ANestedOutDirIsCreatedAndAnUnwritableOneFailsTheRun()
    {
        using var rig = new IntakeRig();
        rig.Issues[1] = IntakeIssue.Eligible();
        var nested = Path.Combine(rig.Out, "a", "b");

        var ok = rig.RunWith(nested, "--issues", "1");
        var blocker = Path.Combine(rig.Out, "file");
        File.WriteAllText(blocker, "x");
        var blocked = rig.RunWith(Path.Combine(blocker, "sub"), "--issues", "1");

        Assert.Equal(0, ok.Code);
        Assert.True(File.Exists(Path.Combine(nested, "issue-1.md")));
        Assert.Equal(1, blocked.Code);
        Assert.Equal("intake-failed: writing snapshot for #1 failed", Error(blocked.Stdout));
        Assert.Contains(rig.Errors, e => e.Hook == "issue-intake");
    }

    [Theory]
    [InlineData(0x061C)]
    [InlineData(0x200B)]
    [InlineData(0x200F)]
    [InlineData(0x2028)]
    [InlineData(0x202E)]
    [InlineData(0x2060)]
    [InlineData(0x206F)]
    [InlineData(0xFEFF)]
    [InlineData(0xE0000)]
    [InlineData(0xE007F)]
    [InlineData(0xE0100)]
    [InlineData(0xE01EF)]
    public void ACodePointInAHiddenRangeIsHiddenContentInTheBodyAndTheTitle(int codePoint)
    {
        using var rig = new IntakeRig();
        var text = char.ConvertFromUtf32(codePoint);
        rig.Issues[1] = IntakeIssue.Eligible("<p>a" + text + "b</p>");
        rig.Issues[2] = IntakeIssue.Eligible("<p>b</p>", "a" + text + "b");

        var result = Parse(rig.Run("--issues", "1,2").Stdout);

        Assert.All(result["items"].AsArray(), i => Assert.Equal("hidden-content", i.AsObject()["reason"].AsString()));
    }

    [Theory]
    [InlineData(0x061B)]
    [InlineData(0x061D)]
    [InlineData(0x200A)]
    [InlineData(0x2010)]
    [InlineData(0x2027)]
    [InlineData(0x202F)]
    [InlineData(0x205F)]
    [InlineData(0x2070)]
    [InlineData(0xFEFE)]
    [InlineData(0xFF00)]
    [InlineData(0xDFFFF)]
    [InlineData(0xE0080)]
    [InlineData(0xE00FF)]
    [InlineData(0xE01F0)]
    public void ACodePointJustOutsideAHiddenRangeIsKept(int codePoint)
    {
        using var rig = new IntakeRig();
        rig.Issues[1] = IntakeIssue.Eligible("<p>a" + char.ConvertFromUtf32(codePoint) + "b</p>");

        var item = ItemOf(rig.Run("--issues", "1").Stdout);

        Assert.True(item["eligible"].AsBool());
    }

    [Theory]
    [InlineData("\u0001")]
    [InlineData("\u007F")]
    public void AControlCharacterOutsideTheHiddenSetIsKeptVerbatim(string control)
    {
        using var rig = new IntakeRig();
        rig.Issues[1] = IntakeIssue.Eligible("<p>a" + control + "b</p>");

        var item = ItemOf(rig.Run("--issues", "1").Stdout);

        Assert.True(item["eligible"].AsBool());
        Assert.Contains("| a" + control + "b\n", File.ReadAllText(rig.SnapshotPath(1)), StringComparison.Ordinal);
    }

    [Fact]
    public void ADetailsOrSummaryStartTagMakesTheIssueHiddenContentEvenInsideADroppedElementButAnEndTagDoesNot()
    {
        using var rig = new IntakeRig();
        rig.Issues[1] = IntakeIssue.Eligible("<p>a</p><details>x</details>");
        rig.Issues[2] = IntakeIssue.Eligible("<template><details>x</details></template>ok");
        rig.Issues[3] = IntakeIssue.Eligible("</details>closing only");

        var result = Parse(rig.Run("--issues", "1,2,3").Stdout);

        Assert.Equal("hidden-content", result["items"].AsArray()[0].AsObject()["reason"].AsString());
        Assert.Equal("hidden-content", result["items"].AsArray()[1].AsObject()["reason"].AsString());
        Assert.True(result["items"].AsArray()[2].AsObject()["eligible"].AsBool());
    }
}
