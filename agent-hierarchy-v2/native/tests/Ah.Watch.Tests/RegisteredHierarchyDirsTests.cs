using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Watch.Tests;

/// <summary>b432: every registration naming the same team file made the dir list repeat it, and memory-forward reran each copy.</summary>
public sealed class RegisteredHierarchyDirsTests
{
    private sealed class Layout : IViewLayout
    {
        public string AhHome => "/home/.claude/ah2";

        public string BoxDirFor(string teamFile, string member) => "/box";
    }

    private sealed class NoPlacement : IViewPlacement
    {
        public JsonObject Placement(JsonObject memberRow, string? transport) => new();
    }

    private sealed class State(int registrations) : IViewStateReader
    {
        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args)
        {
            if (domain == "known-dirs")
            {
                return new JsonObject { ["dirs"] = new JsonArray { "/repo/.claude/ah2", "/other/.claude/ah2" } };
            }

            var rows = new JsonArray();
            for (var i = 0; i < registrations; i++)
            {
                rows.Add(new JsonObject { ["registration"] = new JsonObject(), ["live"] = false, ["team_files"] = new JsonArray { "/repo/.claude/ah2/teams/t.json" } });
            }

            return new JsonObject { ["registrations"] = rows };
        }
    }

    [Fact]
    public void ADirNamedByManyRegistrationsAndKnownDirsIsListedOnce()
    {
        var source = WatchModule.CreateViewSource(new ViewSourceServices(new NullFiles(), new StreamDerivations(), new NoPlacement(), State: new State(500), Layout: new Layout()));

        Assert.Equal(["/repo/.claude/ah2", "/other/.claude/ah2"], source.RegisteredHierarchyDirs());
    }

    private sealed class Growing : IViewStateReader
    {
        internal List<string> TeamFiles { get; } = ["/repo/.claude/ah2/teams/t.json"];

        internal int RegistrationReads { get; set; }

        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args)
        {
            if (domain == "known-dirs")
            {
                return new JsonObject { ["dirs"] = new JsonArray() };
            }

            RegistrationReads++;
            var rows = new JsonArray();
            foreach (var file in TeamFiles)
            {
                rows.Add(new JsonObject { ["registration"] = new JsonObject(), ["live"] = true, ["team_files"] = new JsonArray { file } });
            }

            return new JsonObject { ["registrations"] = rows };
        }
    }

    /// <summary>b459 step 14: the list is held while the sessions token is unchanged; a new registration moves the token and shows its dir.</summary>
    [Fact]
    public void HeldDirsAreNotReadAgainAndNegativeANewRegistrationShowsItsDirOnTheNextRead()
    {
        var state = new Growing();
        long token = 1;
        var source = WatchModule.CreateViewSource(new ViewSourceServices(new NullFiles(), new StreamDerivations(), new NoPlacement(), State: state, Layout: new Layout()) { SessionsToken = () => token });

        Assert.Equal(["/repo/.claude/ah2"], source.RegisteredHierarchyDirs());
        Assert.Equal(["/repo/.claude/ah2"], source.RegisteredHierarchyDirs());
        Assert.Equal(1, state.RegistrationReads);

        state.TeamFiles.Add("/new/.claude/ah2/teams/x.json");
        token++;
        Assert.Equal(["/repo/.claude/ah2", "/new/.claude/ah2"], source.RegisteredHierarchyDirs());

        // the on-demand rebuild drops the hold
        source.ForgetHeldDirs();
        _ = source.RegisteredHierarchyDirs();
        Assert.Equal(3, state.RegistrationReads);
    }
}
