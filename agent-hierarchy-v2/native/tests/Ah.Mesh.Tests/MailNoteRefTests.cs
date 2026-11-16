using System.Runtime.Versioning;
using Ah.Core.Json;

namespace Ah.Mesh.Tests;

/// <summary>A note may carry a file by <c>--ref</c>, with or without text, so a note longer than its text cap has a path (b14).</summary>
[UnsupportedOSPlatform("windows")]
public sealed class MailNoteRefTests
{
    private static MailboxOutcome<JsonObject> Note(MailClaimTests.Rig rig, string id, string? reference, string? text)
    {
        var request = new JsonObject { ["to"] = "b@tm", ["from"] = "a@tm", ["id"] = id, ["kind"] = "note", ["session_id"] = "s-a", ["cwd"] = rig.Hierarchy, ["hierarchy_dir"] = rig.Hierarchy };
        if (reference is not null)
        {
            request["ref"] = reference;
        }

        if (text is not null)
        {
            request["text"] = text;
        }

        return rig.Fx.Mailbox.Send(request);
    }

    [Fact]
    public void ANoteCarriesAFileWithOrWithoutText()
    {
        var rig = MailClaimTests.Create();
        var file = rig.Hierarchy + "/addendum.md";
        File.WriteAllText(file, "a long rework list");

        var bare = Note(rig, "n1", file, null);
        var both = Note(rig, "n2", file, "see the list");

        Assert.True(bare.Ok, bare.Failure?.Message);
        Assert.True(both.Ok, both.Failure?.Message);
        var entries = rig.Fx.Mailbox.ReadEntries(rig.BoxB).Entries.Select(e => e.AsObject()).ToList();
        Assert.Equal(file, entries.Single(e => e.Get("id")!.Value.AsString() == "n1").Get("ref")!.Value.AsString());
        Assert.Equal("see the list", entries.Single(e => e.Get("id")!.Value.AsString() == "n2").Get("text")!.Value.AsString());
    }

    [Fact]
    public void ANoteRefThatIsNotAnExistingFileIsRefused()
    {
        var rig = MailClaimTests.Create();

        var missing = Note(rig, "n1", rig.Hierarchy + "/nope.md", null);
        var relative = Note(rig, "n2", "nope.md", null);
        var empty = Note(rig, "n3", null, null);
        var dir = rig.Hierarchy + "/a-dir";
        Directory.CreateDirectory(dir);
        var directory = Note(rig, "n4", dir, null);

        Assert.Equal("bad_request", missing.Failure!.Code);
        Assert.Equal("bad_request", relative.Failure!.Code);
        Assert.Equal("bad_request", directory.Failure!.Code);
        Assert.Equal("note needs --text or --ref <file>", empty.Failure!.Message);
    }

    [Fact]
    public void ABriefStillTakesExactlyOneOfRefAndText()
    {
        var rig = MailClaimTests.Create();
        var file = rig.Hierarchy + "/addendum.md";
        File.WriteAllText(file, "x");

        var outcome = rig.Fx.Mailbox.Send(new JsonObject { ["to"] = "b@tm", ["from"] = "a@tm", ["id"] = "b1", ["kind"] = "brief", ["ref"] = file, ["text"] = "t", ["cwd"] = rig.Hierarchy, ["hierarchy_dir"] = rig.Hierarchy });

        Assert.Equal("bad_request", outcome.Failure!.Code);
        Assert.Contains("exactly one of", outcome.Failure.Message, StringComparison.Ordinal);
    }
}
