#pragma warning disable xUnit1051 // the rig owns its own deadlines

using System.Net;
using System.Net.Sockets;
using Ah.Core.Json;

namespace Ah.Daemon.Tests;

/// <summary>The stable port and the looks file, on a real listener: what survives a restart, what is never overwritten, and a merge that loses nothing.</summary>
public sealed class UiLooksRouteTests
{
    private const string Route = "/ui/v1/looks";

    private static string Look(string id, string name, int blur = 2) =>
        $$$$"""{"id":"{{{{id}}}}","name":"{{{{name}}}}","mode":"dark-emissive","profiles":{"shadow":{"sliderHaloBlur":{{{{blur}}}}},"dual":{}}}""";

    private static string Save(string rev, params string[] looks) =>
        $$$"""{"rev":"{{{rev}}}","looks":{"active":{"light":"light-inky","dark":"a"},"technique":"dual","custom":[{{{string.Join(',', looks)}}}]}}""";

    private static string FileOf(UiRig rig) => Path.Combine(rig.Box.AhHome, "looks.json");

    private static async Task<(HttpStatusCode Status, JsonObject Body)> AskAsync(UiRig rig, UiGrant grant, HttpMethod method, string path, string? body = null)
    {
        using var reply = await rig.SendAsync(grant.Port, method, path, request =>
        {
            if (body is not null)
            {
                request.Content = UiRig.Json(body);

                // The server can refuse a body while it is still being written, and the write then fails with a connection reset instead of reading the 413.
                request.Headers.ExpectContinue = body.Length > UiSession.MaxBodyBytes;
            }
        });
        var bytes = await reply.Content.ReadAsByteArrayAsync();
        return (reply.StatusCode, JsonText.TryParse(bytes, out var parsed) && parsed.IsMap ? parsed.AsObject() : new JsonObject());
    }


    private static string[] Names(JsonObject body) =>
        [.. body.Get("looks")!.Value.AsObject().Get("custom")!.Value.AsArray().Select(static l => l.AsObject().Str("name")!)];

    private static async Task RestartAsync(UiRig rig) => await rig.Bridge.StopAsync("test", refuseNewLeases: false);

    [Fact]
    public async Task TwoStartsBindTheSamePortAndNothingSaysItMoved()
    {
        await using var rig = UiRig.Create();
        var first = await rig.LeaseAsync();
        Assert.Equal(0, first.MovedFrom);
        await RestartAsync(rig);

        var second = await rig.LeaseAsync();

        Assert.Equal(first.Port, second.Port);
        Assert.Equal(0, second.MovedFrom);
    }

    [Fact]
    public async Task AHeldPortFallsBackToAnotherAndTheGrantSaysFromWhich()
    {
        await using var rig = UiRig.Create();
        using var holder = rig.HoldRemembered(out var held);

        var second = await rig.LeaseAsync();

        Assert.NotEqual(held, second.Port);
        Assert.Equal(held, second.MovedFrom);
        Assert.Contains("not bound", rig.LogText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not a port")]
    [InlineData("80")]
    [InlineData("70000")]
    public async Task ARememberedPortThatIsNoPortIsNotTriedAndIsNotAMove(string junk)
    {
        await using var rig = UiRig.Create();
        _ = Directory.CreateDirectory(rig.RunDir);
        File.WriteAllText(Path.Combine(rig.RunDir, "ui-port"), junk);

        var grant = await rig.LeaseAsync();

        Assert.Equal(0, grant.MovedFrom);
        Assert.True(grant.Port > 1023);
    }

    [Fact]
    public async Task ASaveSurvivesARestartOnADifferentPort()
    {
        await using var rig = UiRig.Create();
        var grant = await rig.LeaseAsync();
        var (status, saved) = await AskAsync(rig, grant, HttpMethod.Post, Route, Save(string.Empty, Look("a", "Mine")));
        Assert.Equal(HttpStatusCode.OK, status);

        await RestartAsync(rig);
        using var holder = rig.HoldRemembered(out var held);
        var next = await rig.LeaseAsync();

        // The fallback is any free port, which can be the one just freed: restart until it is not.
        for (var tries = 0; next.Port == grant.Port && tries < 5; tries++)
        {
            await RestartAsync(rig);
            rig.Remember(held);
            next = await rig.LeaseAsync();
        }

        var (again, read) = await AskAsync(rig, next, HttpMethod.Get, Route);

        Assert.NotEqual(grant.Port, next.Port);
        Assert.Equal(HttpStatusCode.OK, again);
        Assert.Equal(["Mine"], Names(read));
        Assert.Equal(saved.Str("rev"), read.Str("rev"));
        Assert.Equal("dual", read.Get("looks")!.Value.AsObject().Str("technique"));
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("")]
    [InlineData("""{"custom":"not a list"}""")]
    [InlineData("""{"custom":[{"id":"x"}]}""")]
    public async Task ACorruptFileIsServedAsCorruptAndNeverWritten(string text)
    {
        await using var rig = UiRig.Create();
        File.WriteAllText(FileOf(rig), text);
        var grant = await rig.LeaseAsync();

        var (read, doc) = await AskAsync(rig, grant, HttpMethod.Get, Route);
        var (write, written) = await AskAsync(rig, grant, HttpMethod.Post, Route, Save(string.Empty, Look("a", "Mine")));
        var (merge, merged) = await AskAsync(rig, grant, HttpMethod.Post, Route + "/merge", $$"""{"custom":[{{Look("a", "Mine")}}]}""");

        Assert.Equal(HttpStatusCode.OK, read);
        Assert.True(doc.Get("corrupt")!.Value.AsBool());
        Assert.True(doc.Get("looks")!.Value.IsNull);
        Assert.Equal(FileOf(rig), doc.Str("path"));
        Assert.Equal(HttpStatusCode.Conflict, write);
        Assert.Equal("corrupt", written.Str("reason"));
        Assert.Equal(HttpStatusCode.Conflict, merge);
        Assert.Equal("corrupt", merged.Str("reason"));
        Assert.Equal(text, File.ReadAllText(FileOf(rig)));
        Assert.False(File.Exists(FileOf(rig) + ".bak"));
    }

    [Fact]
    public async Task NoFileIsEmptyAndNotCorrupt()
    {
        await using var rig = UiRig.Create();
        var grant = await rig.LeaseAsync();

        var (status, doc) = await AskAsync(rig, grant, HttpMethod.Get, Route);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(doc.Get("corrupt")!.Value.AsBool());
        Assert.Equal(string.Empty, doc.Str("rev"));
        Assert.Empty(Names(doc));
        Assert.False(File.Exists(FileOf(rig)));
    }

    [Fact]
    public async Task AWriteOnAnOldRevisionIsRefusedAndTheFileStays()
    {
        await using var rig = UiRig.Create();
        var grant = await rig.LeaseAsync();
        _ = await AskAsync(rig, grant, HttpMethod.Post, Route, Save(string.Empty, Look("a", "First")));
        var before = File.ReadAllText(FileOf(rig));

        var (status, doc) = await AskAsync(rig, grant, HttpMethod.Post, Route, Save(string.Empty, Look("b", "Late")));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("stale", doc.Str("reason"));
        Assert.Equal(["First"], Names(doc));
        Assert.Equal(before, File.ReadAllText(FileOf(rig)));
    }

    [Fact]
    public async Task AWriteKeepsThePreviousVersionAsTheBackupAndLeavesNoTemp()
    {
        await using var rig = UiRig.Create();
        var grant = await rig.LeaseAsync();
        var (_, first) = await AskAsync(rig, grant, HttpMethod.Post, Route, Save(string.Empty, Look("a", "First")));
        Assert.False(File.Exists(FileOf(rig) + ".bak"));
        var previous = File.ReadAllText(FileOf(rig));

        var (status, _) = await AskAsync(rig, grant, HttpMethod.Post, Route, Save(first.Str("rev")!, Look("a", "Second")));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(previous, File.ReadAllText(FileOf(rig) + ".bak"));
        Assert.False(File.Exists(FileOf(rig) + ".bak.tmp"));
        Assert.Contains("Second", File.ReadAllText(FileOf(rig)), StringComparison.Ordinal);
        Assert.False(File.Exists(FileOf(rig) + ".tmp"));
    }

    [Fact]
    public async Task ABackupUnderAnHourOldStandsAndAnOlderOneIsRefreshed()
    {
        await using var rig = UiRig.Create();
        var grant = await rig.LeaseAsync();
        var bak = FileOf(rig) + ".bak";
        var (_, one) = await AskAsync(rig, grant, HttpMethod.Post, Route, Save(string.Empty, Look("a", "One")));
        var (_, two) = await AskAsync(rig, grant, HttpMethod.Post, Route, Save(one.Str("rev")!, Look("a", "Two")));
        Assert.Contains("One", File.ReadAllText(bak), StringComparison.Ordinal);

        File.SetLastWriteTimeUtc(bak, DateTime.UtcNow - TimeSpan.FromMinutes(59));
        var (_, three) = await AskAsync(rig, grant, HttpMethod.Post, Route, Save(two.Str("rev")!, Look("a", "Three")));
        Assert.Contains("One", File.ReadAllText(bak), StringComparison.Ordinal);

        File.SetLastWriteTimeUtc(bak, DateTime.UtcNow - TimeSpan.FromMinutes(61));
        _ = await AskAsync(rig, grant, HttpMethod.Post, Route, Save(three.Str("rev")!, Look("a", "Four")));
        Assert.Contains("Three", File.ReadAllText(bak), StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - File.GetLastWriteTimeUtc(bak) < TimeSpan.FromMinutes(5));
        Assert.Contains("Four", File.ReadAllText(FileOf(rig)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMergeLosesNothingAndClashesKeepBoth()
    {
        await using var rig = UiRig.Create();
        var grant = await rig.LeaseAsync();
        _ = await AskAsync(rig, grant, HttpMethod.Post, Route, Save(string.Empty, Look("a", "Mine"), Look("b", "Other")));
        var incoming = $$"""
            {"active":{"light":"x","dark":"x"},"technique":"shadow","custom":[
              {{Look("a", "Mine")}},
              {{Look("a", "Mine", blur: 9)}},
              {{Look("c", "Other", blur: 7)}},
              {{Look("d", "New", blur: 5)}},
              {"id":"bad"}]}
            """;

        var (status, doc) = await AskAsync(rig, grant, HttpMethod.Post, Route + "/merge", incoming);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["Mine", "Other", "Mine (browser)", "Other (browser)", "New"], Names(doc));
        Assert.Equal(3, (int)doc.Get("added")!.Value.AsNumber());
        Assert.Equal(1, (int)doc.Get("already")!.Value.AsNumber());
        Assert.Equal(1, (int)doc.Get("skipped")!.Value.AsNumber());
        var ids = doc.Get("looks")!.Value.AsObject().Get("custom")!.Value.AsArray().Select(static l => l.AsObject().Str("id")).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        var looks = doc.Get("looks")!.Value.AsObject();
        Assert.Equal("a", looks.Get("active")!.Value.AsObject().Str("dark"));
        Assert.Equal("dual", looks.Str("technique"));
    }

    [Fact]
    public async Task AMergeRepeatedChangesNothingNotEvenTheFile()
    {
        await using var rig = UiRig.Create();
        var grant = await rig.LeaseAsync();
        var incoming = $$"""{"custom":[{{Look("a", "Mine")}}]}""";
        _ = await AskAsync(rig, grant, HttpMethod.Post, Route + "/merge", incoming);
        var before = File.ReadAllText(FileOf(rig));

        var (_, doc) = await AskAsync(rig, grant, HttpMethod.Post, Route + "/merge", incoming);

        Assert.Equal(0, (int)doc.Get("added")!.Value.AsNumber());
        Assert.Equal(before, File.ReadAllText(FileOf(rig)));
    }

    [Fact]
    public async Task ALookDeletedAfterAMergeDoesNotComeBack()
    {
        await using var rig = UiRig.Create();
        var grant = await rig.LeaseAsync();
        var incoming = $$"""{"custom":[{{Look("a", "Mine")}}]}""";
        var (_, merged) = await AskAsync(rig, grant, HttpMethod.Post, Route + "/merge", incoming);
        var (_, emptied) = await AskAsync(rig, grant, HttpMethod.Post, Route, Save(merged.Str("rev")!));

        var (_, doc) = await AskAsync(rig, grant, HttpMethod.Post, Route + "/merge", incoming);

        Assert.Empty(Names(doc));
        Assert.Equal(emptied.Str("rev"), doc.Str("rev"));
    }

    [Fact]
    public async Task AMergeTakesTheActiveChoiceOnlyWhenTheFileHasNone()
    {
        await using var rig = UiRig.Create();
        var grant = await rig.LeaseAsync();

        var (_, doc) = await AskAsync(rig, grant, HttpMethod.Post, Route + "/merge", """{"active":{"dark":"z"},"technique":"dual","custom":[]}""");

        var looks = doc.Get("looks")!.Value.AsObject();
        Assert.Equal("z", looks.Get("active")!.Value.AsObject().Str("dark"));
        Assert.Equal("dual", looks.Str("technique"));
    }

    [Theory]
    [InlineData("""{"rev":"","looks":"x"}""")]
    [InlineData("""{"rev":"","looks":{"custom":"x"}}""")]
    [InlineData("""{"rev":"","looks":{"custom":[{"id":"a","mode":"nope","profiles":{"shadow":{},"dual":{}}}]}}""")]
    [InlineData("""{"rev":"","looks":{"custom":[{"id":"a","mode":"dark-emissive","profiles":{"shadow":{}}}]}}""")]
    [InlineData("""{"rev":"","looks":{"custom":[],"technique":"sparkle"}}""")]
    [InlineData("""{"rev":""}""")]
    [InlineData("[]")]
    public async Task AWrongShapeIsABadRequestAndWritesNothing(string body)
    {
        await using var rig = UiRig.Create();
        var grant = await rig.LeaseAsync();

        var (status, _) = await AskAsync(rig, grant, HttpMethod.Post, Route, body);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.False(File.Exists(FileOf(rig)));
    }

    [Fact]
    public async Task TheLooksCapIsItsOwn()
    {
        await using var rig = UiRig.Create();
        var grant = await rig.LeaseAsync();
        var bigName = new string('x', 200);
        var many = Enumerable.Range(0, 40).Select(i => Look($"id{i}", bigName + i)).ToArray();
        var fits = Save(string.Empty, many);
        Assert.True(fits.Length > UiSession.MaxBodyBytes);
        var over = Save(string.Empty, Look("a", "x")) + new string(' ', UiSession.MaxLooksBytes);

        var (ok, _) = await AskAsync(rig, grant, HttpMethod.Post, Route, fits);
        var (tooBig, _) = await AskAsync(rig, grant, HttpMethod.Post, Route, over);

        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig);
    }

    [Fact]
    public async Task ARefusedBodyIsReadToItsEndSoAClientStillWritingIsNotReset()
    {
        await using var rig = UiRig.Create();
        var grant = await rig.LeaseAsync();
        var body = new byte[UiSession.MaxLooksBytes + 1024];
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, grant.Port));
        var head = $"POST {Route} HTTP/1.1\r\nHost: 127.0.0.1:{grant.Port}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n";
        _ = await socket.SendAsync(System.Text.Encoding.Latin1.GetBytes(head));

        var answer = new byte[512];
        var read = await socket.ReceiveAsync(answer);
        Assert.Contains(" 413 ", System.Text.Encoding.Latin1.GetString(answer, 0, read), StringComparison.Ordinal);

        // the server has answered and this client has not written yet: a server that closes now resets these writes
        await Task.Delay(200);
        for (var sent = 0; sent < body.Length;)
        {
            sent += await socket.SendAsync(body.AsMemory(sent));
        }
    }

    [Theory]
    [InlineData("DELETE", Route, HttpStatusCode.MethodNotAllowed)]
    [InlineData("GET", Route + "/merge", HttpStatusCode.MethodNotAllowed)]
    [InlineData("GET", Route + "/other", HttpStatusCode.NotFound)]
    public async Task OtherMethodsAndNeighbouringPathsAreRefused(string method, string path, HttpStatusCode expected)
    {
        await using var rig = UiRig.Create();
        var grant = await rig.LeaseAsync();

        var (status, _) = await AskAsync(rig, grant, new HttpMethod(method), path);

        Assert.Equal(expected, status);
    }
}
