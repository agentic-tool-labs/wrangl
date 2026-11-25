using Ah.Core.Json;

namespace Ah.Hierarchy.Tests;

/// <summary>
/// ah2-627 S1/S2: roster status reads a peer's permission block whose screen the watcher found clear (screen_clear_at) as working, and the
/// dispatch to it with it; a prompt mark, no mark, a question block or a pane member reads blocked as before.
/// </summary>
public sealed class StatusScreenClearTests
{
    private const long T0 = StatusEntriesFixture.T0;

    private sealed class Live : IMemberLiveness
    {
        public MemberRegistrationLiveness? ForMember(string memberName, string team) =>
            $"{memberName}@{team}" == "t-implementor@t" ? new MemberRegistrationLiveness("s-impl", true, "running") : null;

        public bool PidIdentityLive(int pid, string? recordedStart) => true;
    }

    private static string At(long ms) => StatusEntriesFixture.Iso(ms);

    private static (JsonObject Member, JsonObject? Dispatch, string Stored) Read(StatusEntriesFixture fixture, string name, string subject, string record)
    {
        var dir = fixture.StatusDirs[0];
        fixture.Activity(dir, subject, record);
        var services = fixture.Services();
        services.Core.Liveness = new Live();
        var doc = ((HierLib)services.Status).ReadStatus(dir, T0 + 60_000, dir);
        var objects = new List<JsonObject>();
        Walk(doc, objects);
        var member = objects.First(o => o.Get("name") is { IsText: true } n && n.AsString() == name && o.Get("route") is { IsText: true });
        var dispatch = objects.FirstOrDefault(o => o.Get("id") is { IsText: true } id && id.AsString() == "20260528-000002-impl" && o.Get("states") is { IsArray: true });
        var stored = JsonText.Stringify(services.Status.ReadActivityRecord(dir, JsonValue.FromString(subject)), 0);
        return (member, dispatch, stored);
    }

    private static void Walk(JsonValue v, List<JsonObject> into)
    {
        if (v.IsMap)
        {
            var o = v.AsObject();
            into.Add(o);
            foreach (var key in o.Keys)
            {
                Walk(o[key], into);
            }
        }
        else if (v.IsArray)
        {
            foreach (var child in v.AsArray())
            {
                Walk(child, into);
            }
        }
    }

    [Fact]
    public void APermissionBlockWithAScreenClearMarkReadsWorkingAndSoDoesItsDispatchAndTheRecordIsUnchanged()
    {
        var fixture = StatusEntriesFixture.Create();
        var record = $$"""{"activity":"blocked","blocked_by":"permission","at":"{{At(T0 - 40_000)}}","screen_clear_at":"{{At(T0 - 39_000)}}"}""";

        var (member, dispatch, stored) = Read(fixture, "t-implementor", "s-impl", record);

        Assert.Equal("working", member["activity"].AsString());
        Assert.True(member["blocked_by"].IsNull);
        Assert.Equal("working", dispatch!["states"].AsArray()[0].Prop("state").AsString());
        Assert.Contains("\"activity\":\"blocked\"", stored, StringComparison.Ordinal);
        Assert.Contains("\"screen_clear_at\"", stored, StringComparison.Ordinal);
    }

    [Theory]
    // negative: a prompt mark (prompt read, or screen unreadable)
    [InlineData("""{"activity":"blocked","blocked_by":"permission","screen_prompt_at":"2026-05-28T00:00:00.000Z"}""", "permission")]
    // negative: neither mark
    [InlineData("""{"activity":"blocked","blocked_by":"permission"}""", "permission")]
    // negative: both marks (the prompt mark wins, as in the UI lane)
    [InlineData("""{"activity":"blocked","blocked_by":"permission","screen_clear_at":"2026-05-28T00:00:00.000Z","screen_prompt_at":"2026-05-28T00:00:00.000Z"}""", "permission")]
    // negative: a question block keeps blocked whatever the marks
    [InlineData("""{"activity":"blocked","blocked_by":"question","question":"which?","screen_clear_at":"2026-05-28T00:00:00.000Z"}""", "question")]
    // negative: a permission notice carrying a question
    [InlineData("""{"activity":"blocked","blocked_by":"permission","question":"which?","screen_clear_at":"2026-05-28T00:00:00.000Z"}""", "permission")]
    public void NegativeABlockWithoutAClearScreenOnAPermissionPromptStillReadsBlocked(string record, string blockedBy)
    {
        var fixture = StatusEntriesFixture.Create();

        var (member, dispatch, _) = Read(fixture, "t-implementor", "s-impl", record);

        Assert.Equal("blocked", member["activity"].AsString());
        Assert.Equal(blockedBy, member["blocked_by"].AsString());
        Assert.Equal("blocked", dispatch!["states"].AsArray()[0].Prop("state").AsString());
    }

    [Fact]
    public void NegativeAPaneMembersPermissionBlockWithAScreenClearMarkStillReadsBlocked()
    {
        var fixture = StatusEntriesFixture.Create();
        var teams = Path.Combine(fixture.StatusDirs[0], "teams");
        File.WriteAllText(Path.Combine(teams, "p.json"), """{"team_id":"t-p","team":"p","members":[{"name":"p-codex","role":"implementor","kind":"codex","route":"pane"}]}""" + "\n");
        var record = $$"""{"activity":"blocked","blocked_by":"permission","at":"{{At(T0 - 40_000)}}","screen_clear_at":"{{At(T0 - 39_000)}}"}""";

        var (member, _, _) = Read(fixture, "p-codex", "pane-p-codex", record);

        Assert.Equal("pane", member["route"].AsString());
        Assert.Equal("blocked", member["activity"].AsString());
        Assert.Equal("permission", member["blocked_by"].AsString());
    }
}
