using Ah.Core.Json;

namespace Ah.Watch.Tests;

/// <summary>A mail record names its message by id: the menu copies <c>ah msg show</c>, the detail shows the id, and nothing offers a file (ah-msgs-db-only §8).</summary>
public sealed class MailMessageActionTests
{
    private const string Id = "20261103-005522-12h1";
    private const string Request = "/pool/msgs/" + Id + "--impl--view-model--request.md";

    private static ModelSource Source(string? msgRef)
    {
        var src = new ModelSource { TeamName = "alpha" }.Member("impl");
        var mail = new JsonObject { ["id"] = "m1", ["kind"] = "brief", ["from"] = "orchestrator@alpha", ["to"] = "impl@alpha", ["created"] = "2026-06-02T09:00:00.000Z", ["acked"] = false };
        if (msgRef is not null)
        {
            mail["ref"] = msgRef;
        }

        src.Boxes["impl"] = [mail];
        return src;
    }

    private static string MailPath(ModelSource src) => src.Paths().Member + "/mail/m1";

    private static List<JsonObject> Actions(ModelSource src) =>
        [.. src.Build("/actions" + MailPath(src)).Obj("body")!["actions"].AsArray().Select(static a => a.AsObject())];

    private static string? Copied(ModelSource src) =>
        Actions(src).SingleOrDefault(static a => a.Str("id") == "mail.copy_show")?.Obj("invoke")?.Str("copy");

    [Theory]
    [InlineData(Request, "ah msg show " + Id)]
    [InlineData("/pool/msgs/" + Id + "--impl--view-model--response.md", "ah msg show " + Id + " response")]
    public void TheMenuCopiesTheShowCommandOfTheMessage(string msgRef, string command) =>
        Assert.Equal(command, Copied(Source(msgRef)));

    [Fact]
    public void NegativeTheMenuOffersNoFile()
    {
        var ids = Actions(Source(Request)).Select(static a => a.Str("id")).ToList();

        Assert.Contains("mail.open_text", ids);
        Assert.DoesNotContain("mail.open_file", ids);
        Assert.DoesNotContain("mail.copy_path", ids);
    }

    [Theory]
    [InlineData("/pool/notes/plain.md")]
    [InlineData("/pool/msgs/m1--impl--view-model--request.md")]
    public void NegativeARefThatIsNoMessageKeyOffersNoShowCommand(string msgRef) =>
        Assert.Null(Copied(Source(msgRef)));

    [Fact]
    public void NegativeAnEntryWithNoRefOffersNoShowCommand() =>
        Assert.Null(Copied(Source(null)));

    [Fact]
    public void TheDetailNamesTheMessageByIdNotByPath()
    {
        var src = Source(Request);

        var text = JsonText.Stringify(src.Build("/detail" + MailPath(src)), 2);

        Assert.Contains("\"message\"", text);
        Assert.Contains($"\"{Id}\"", text);
        Assert.DoesNotContain(Request, text);
        Assert.DoesNotContain("request file\"", text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AMessageTheTextReadCannotFindSaysItIsNotInTheStore(bool outside)
    {
        var src = Source(Request);
        if (outside)
        {
            src.OutsideStore.Add(Request);
        }

        var body = src.Build(MailPath(src) + "/text").Obj("body")!;

        Assert.Equal("the message is not in the store", body.Str("reason"));
    }

    [Fact]
    public void NegativeAMessageInTheStoreCarriesItsTextAndNoReason()
    {
        var src = Source(Request);
        src.Texts[Request] = "the brief";

        var body = src.Build(MailPath(src) + "/text").Obj("body")!;

        Assert.Equal("the brief", body.Str("text"));
        Assert.Null(body.Str("reason"));
    }
}
