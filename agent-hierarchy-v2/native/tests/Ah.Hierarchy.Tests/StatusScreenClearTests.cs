using Ah.Abstractions;
using Ah.Core.Json;
using Ah.TestKit;

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

    /// <summary>The fixture's services with an in-process state writer, so a routed write (<c>activity.record</c>) lands.</summary>
    private static HierarchyServices Writing(StatusEntriesFixture fixture)
    {
        var env = new FakeEnvironment(new Dictionary<string, string> { ["HOME"] = fixture.Home, ["TZ"] = "UTC" }, new PluginInfo(HierarchyTableFns.AhRoot, "wrangl", "0.0.0", "1"));
        var writer = new InProcessStateWriter { AhHome = env.AhHome };
        var services = fixture.Services(writer: writer);
        writer.Domains = services.StateDomains;
        services.Core.Liveness = new Live();
        return services;
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

    // ah2-636 A1: a permission block with no screen mark is blocked and not confirmed; with a prompt mark it is confirmed.
    // negative: a prompt mark older than the record's `at` (an earlier block's) does not confirm (U4).
    [Theory]
    [InlineData(null, false)]
    [InlineData(-39_000L, true)]
    [InlineData(-41_000L, false)]
    public void APermissionBlockSaysWhetherTheWatcherSawItsPrompt(long? promptOffset, bool confirmed)
    {
        var fixture = StatusEntriesFixture.Create();
        var mark = promptOffset is { } p ? $",\"screen_prompt_at\":\"{At(T0 + p)}\"" : string.Empty;
        var record = $$"""{"activity":"blocked","blocked_by":"permission","at":"{{At(T0 - 40_000)}}"{{mark}}}""";

        var (member, _, _) = Read(fixture, "t-implementor", "s-impl", record);

        Assert.Equal("blocked", member["activity"].AsString());
        Assert.Equal("permission", member["blocked_by"].AsString());
        Assert.Equal(confirmed, member["blocked_confirmed"].AsBool());
    }

    // ah2-636 A2 (U4): a screen_clear_at written before the hook's latest write is an earlier block's and does not read the new block as working.
    [Fact]
    public void AScreenClearMarkOlderThanTheBlockDoesNotReadItWorking()
    {
        var fixture = StatusEntriesFixture.Create();
        var record = $$"""{"activity":"blocked","blocked_by":"permission","at":"{{At(T0 - 40_000)}}","screen_clear_at":"{{At(T0 - 41_000)}}"}""";

        var (member, dispatch, _) = Read(fixture, "t-implementor", "s-impl", record);

        Assert.Equal("blocked", member["activity"].AsString());
        Assert.Equal("permission", member["blocked_by"].AsString());
        Assert.False(member["blocked_confirmed"].AsBool());
        Assert.Equal("blocked", dispatch!["states"].AsArray()[0].Prop("state").AsString());
    }

    // ah2-636 A4 (U5): a second permission Notification onto a record still carrying the first block's screen_clear_at is a new block.
    [Fact]
    public void ASecondPermissionNotificationRestampsTheBlockAndDropsTheOldScreenMark()
    {
        var fixture = StatusEntriesFixture.Create();
        var dir = fixture.StatusDirs[0];
        fixture.Activity(dir, "s-impl", $$"""{"activity":"blocked","blocked_by":"permission","at":"{{At(T0 - 40_000)}}","screen_clear_at":"{{At(T0 - 39_000)}}"}""");
        var services = Writing(fixture);

        Assert.True(services.Status.RecordActivityRouted(dir, JsonValue.FromString("s-impl"), new JsonObject { ["activity"] = "blocked", ["blocked_by"] = "permission" }, hook: true));

        var stored = services.Status.ReadActivityRecord(dir, JsonValue.FromString("s-impl"));
        Assert.Equal(At(T0), stored.Member("at")!.Value.AsString());
        Assert.Null(stored.Member("screen_clear_at"));
        var objects = new List<JsonObject>();
        Walk(((HierLib)services.Status).ReadStatus(dir, T0 + 60_000, dir), objects);
        var member = objects.First(o => o.Get("name") is { IsText: true } n && n.AsString() == "t-implementor" && o.Get("route") is { IsText: true });
        Assert.Equal("blocked", member["activity"].AsString());
        Assert.False(member["blocked_confirmed"].AsBool());
    }

    // negative (U5): an identical write that is not a session's permission write is still skipped: the record keeps its at and marks.
    [Theory]
    [InlineData("pane-p-codex", "permission", null)]
    [InlineData("s-impl", "question", "which?")]
    public void NegativeAnIdenticalNonPermissionNotificationWriteIsStillSkipped(string subject, string blockedBy, string? note)
    {
        var fixture = StatusEntriesFixture.Create();
        var dir = fixture.StatusDirs[0];
        var noteJson = note is null ? "null" : $"\"{note}\"";
        fixture.Activity(dir, subject, $$"""{"activity":"blocked","blocked_by":"{{blockedBy}}","note":{{noteJson}},"at":"{{At(T0 - 40_000)}}","screen_clear_at":"{{At(T0 - 39_000)}}"}""");
        var services = Writing(fixture);
        var fields = new JsonObject { ["activity"] = "blocked", ["blocked_by"] = blockedBy };
        if (note is not null)
        {
            fields["note"] = note;
        }

        Assert.False(services.Status.RecordActivityRouted(dir, JsonValue.FromString(subject), fields, hook: true));

        var stored = services.Status.ReadActivityRecord(dir, JsonValue.FromString(subject));
        Assert.Equal(At(T0 - 40_000), stored.Member("at")!.Value.AsString());
        Assert.NotNull(stored.Member("screen_clear_at"));
        // the route itself writes: a changed state lands
        Assert.True(services.Status.RecordActivityRouted(dir, JsonValue.FromString(subject), new JsonObject { ["activity"] = "working" }, hook: true));
    }

    // negative (U1): the field is absent on a question block, a pane member and a permission block read working.
    [Theory]
    [InlineData("t-implementor", "s-impl", """{"activity":"blocked","blocked_by":"question","question":"which?"}""")]
    [InlineData("t-implementor", "s-impl", """{"activity":"blocked","blocked_by":"permission","question":"which?"}""")]
    [InlineData("t-implementor", "s-impl", """{"activity":"blocked","blocked_by":"permission","at":"2026-05-28T00:00:00.000Z","screen_clear_at":"2026-05-28T00:00:00.000Z"}""")]
    [InlineData("p-codex", "pane-p-codex", """{"activity":"blocked","blocked_by":"permission"}""")]
    public void NegativeNoBlockedConfirmedOffAnUnclearedPeerPermissionBlock(string name, string subject, string record)
    {
        var fixture = StatusEntriesFixture.Create();
        File.WriteAllText(Path.Combine(fixture.StatusDirs[0], "teams", "p.json"), """{"team_id":"t-p","team":"p","members":[{"name":"p-codex","role":"implementor","kind":"codex","route":"pane"}]}""" + "\n");

        var (member, _, _) = Read(fixture, name, subject, record);

        Assert.Null(member.Get("blocked_confirmed"));
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
