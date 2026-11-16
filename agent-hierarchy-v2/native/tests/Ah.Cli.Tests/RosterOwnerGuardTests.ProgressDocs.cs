using System.Globalization;
using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary><c>ah progress list|show</c> (per-dir-files-to-db S11d): the progress documents read back from the state database, by anyone.</summary>
public sealed partial class RosterOwnerGuardTests
{
    [Fact]
    public void ShowPrintsAPlantedDocumentByteIdenticalForANonOwnerWhileItemAddStaysGuarded()
    {
        TeamOwnedBy(LivePidOfAnotherProcess().ToString(CultureInfo.InvariantCulture));
        _ = Directory.CreateDirectory(ProgressFolder);
        const string Checklist = "# s1\r\nState: open\nno trailing newline";
        Ah.TestKit.TestProgressDocs.WriteFile(_sandbox.AhHome, Path.Combine(ProgressFolder, "s1.md"), Checklist);
        Ah.TestKit.TestProgressDocs.WriteFile(_sandbox.AhHome, Path.Combine(ProgressFolder, "rollup.md"), "# rollup\n");

        var show = Run("progress", "show", "tm", "s1");
        var rollup = Run("progress", "show", "tm", "--json");
        var byKey = Run("progress", "show", "--key", Path.Combine(ProgressFolder, "s1.md"));
        var list = Run("progress", "list");
        var missing = Run("progress", "show", "tm", "s1", "--journal");
        var add = Run("progress", "item", "add", "s1", "a", "--title", "A", "--team", "tm");

        Assert.Equal((0, Checklist, string.Empty), (show.Code, show.Stdout, show.Stderr));
        Assert.Equal("# rollup\n", JsonText.Parse(rollup.Stdout).Member("body")?.AsString());
        Assert.Equal(Checklist, byKey.Stdout);
        Assert.Contains("\"s1.md\"", list.Stdout, StringComparison.Ordinal);
        // the negatives: a document that is not there is an error, and a write verb is still the owner's alone
        Assert.NotEqual(0, missing.Code);
        Assert.Equal("not-team-owner", Refusal(add.Stdout).Refused);
    }
}
