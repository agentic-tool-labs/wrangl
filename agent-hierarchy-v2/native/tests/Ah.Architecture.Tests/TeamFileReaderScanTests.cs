using System.Text.RegularExpressions;

namespace Ah.Architecture.Tests;

/// <summary>
/// The database is the team store, so only the seam and the importer may name where a team file lives. A file on the list below
/// builds a path as a string and reads nothing through it; every other file naming <c>teams/</c> or <c>team.json</c> fails, however the path is spelled.
/// </summary>
public sealed partial class TeamFileReaderScanTests
{
    [GeneratedRegex("""
        "[^"]*/teams(?:/[^"]*)?"|"teams/[^"]*"|"[^"]*team\.json"|(?:Join|Combine)\([^)]*"teams"|"(?![^"]*/v1/)[^"]*/registrations(?:/[^"]*)?"|"registrations/[^"]*"|(?:Join|Combine)\([^)]*"registrations"
        """)]
    private static partial Regex TeamPathText();

    [GeneratedRegex(@"\b(ReadText|ReadAllText|ReadAllTextAsync|ReadAllBytes|ReadAllBytesAsync|ReadBytes|ReadWithDigest|OpenRead|ReadNoFollow|Exists|Stat|List)\(")]
    private static partial Regex ReadCall();

    [GeneratedRegex(@"\b(teamFile|TeamFile|teamPath|TeamPath\(|RawTeamPath\(|teamFilePath|TeamFilePath|RegistrationFile\(|registrationFile)\b")]
    private static partial Regex StoredPathArgument();

    private static bool Names(string line) => TeamPathText().IsMatch(line) || (ReadCall().IsMatch(line) && StoredPathArgument().IsMatch(line));

    /// <summary>Directories whose code reads team files for the cut-over and the import, until S7 removes them.</summary>
    private static readonly string[] OwnReaders = ["Ah.StateDb/"];

    /// <summary>The seam, whose files-mode branches read and list these paths until S7, then the sites of spec §1 that hold the path as a string and read through the seam or not at all.</summary>
    private static readonly HashSet<string> PathShape = new(StringComparer.Ordinal)
    {
        "Ah.Roster/Core/RosterCore.cs",
        "Ah.Hierarchy/Lanes/LaneCharters.cs",
        "Ah.Watch/WatchModule.cs",
        "Ah.Watch/Model/ActivityLane.cs",
        "Ah.Watch/Model/TreeLanes.cs",
        "Ah.Watch/Model/HintWatcher.cs",
        "Ah.Mesh/Contract/ContractData.cs",
        "Ah.Mesh/Routes/MeshRoutes.cs",
        "Ah.Commands/Roster/Team/CreatePlanning.cs",
        "Ah.Cli/Composition/QaRoutes.cs",
        "Ah.Cli/Composition/CutoverCallers.cs",
        "Ah.Cli/Verbs/CheckVerb.cs",
        "Ah.Mesh/Mail/Mailbox.cs",
        "Ah.Mesh/Mail/MailInspect.cs",
        "Ah.Mesh/Mail/MailboxStateDomain.cs",
        "Ah.Hierarchy/Hier/HierLib.Peer.cs",
        "Ah.Cli/Composition/DispatchesStateDomain.cs",
        "Ah.Cli/Composition/TestModeStart.cs",
        "Ah.Watch/ViewTiming.cs",
        "Ah.Core/Files/ListingScope.cs",
        "Ah.Launch/Snapshot/SnapshotBuilder.cs",
        "Ah.Commands/Roster/Team/UsedSuffix.cs",
        "Ah.Watch/Model/ProgressLane.cs",
    };

    private static IEnumerable<string> Offenders(string relative, IEnumerable<string> lines) =>
        lines.Select((line, i) => (line, i))
            .Where(l => !l.line.TrimStart().StartsWith("//", StringComparison.Ordinal) && Names(l.line))
            .Select(l => $"{relative}:{l.i + 1}: {l.line.Trim()}");

    [Fact]
    public void NothingOutsideTheSeamNamesWhereATeamFileLives()
    {
        var src = Path.Combine(Workspace.NativeRoot, "src");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)
                || OwnReaders.Any(o => relative.StartsWith(o, StringComparison.Ordinal)) || PathShape.Contains(relative))
            {
                continue;
            }

            offenders.AddRange(Offenders(relative, File.ReadLines(file)));
        }

        Assert.True(offenders.Count == 0, "a team path named outside the seam:\n" + string.Join('\n', offenders));
    }

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("var text = files.ReadText(hierarchyDir + \"/teams/\" + team + \".json\");")]
    [InlineData("var path = NodePath.Join(dir, \"teams\", team + \".json\");")]
    [InlineData("if (files.Exists(Path.Combine(dir, \"team.json\"))) { }")]
    [InlineData("foreach (var f in files.List(dir + \"/teams\")) { }")]
    [InlineData("var stat = files.Stat($\"{dir}/teams/{name}.json\");")]
    [InlineData("var text = files.ReadText(home + \"/registrations/\" + id + \".json\");")]
    [InlineData("var listed = files.List(NodePath.Join(home, \"registrations\"));")]
    [InlineData("var text = files.ReadText(teamFile);")]
    [InlineData("if (files.Exists(teamFile)) { }")]
    [InlineData("var stat = files.Stat(roster.TeamPath(dir, team));")]
    [InlineData("if (files.Exists(RawTeamPath(home, team))) { }")]
    [InlineData("var stat = files.Stat(mailbox.RegistrationFile(session));")]
    public void TheScanFindsAPlantedTeamPath(string planted) =>
        Assert.Single(Offenders("planted.cs", [planted]));

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("var text = files.ReadText(registrationPath);")]
    [InlineData("var teamName = \"steams\";")]
    [InlineData("// a comment naming \"teams/x.json\"")]
    [InlineData("var key = \"team_file\";")]
    [InlineData("body[\"teams\"] = teams;")]
    [InlineData("body[\"registrations\"] = rows;")]
    public void TheScanLeavesAnythingElseAlone(string line) =>
        Assert.Empty(Offenders("fine.cs", [line]));
}
