using Ah.Core.Json;

namespace Ah.Watch.Tests;

/// <summary>A member's last report is the newest answer it gave to an exchange addressed to it.</summary>
public sealed partial class TreeLanesTests
{
    private static string ReportAt(string to, string from, string? toName, string? fromName, string at, bool withdrawn = false) =>
        Msg(to, from, toName, fromName, "default", open: false).TrimEnd('}') + ",\"response_created\":\"" + at + "\",\"withdrawn\":" + (withdrawn ? "true" : "false") + "}";

    private static JsonValue LastReportOf(TreeReader reader, string member)
    {
        var (model, _) = Seeded(reader);
        using var owner = model;
        return JsonText.Parse(Body(model, MemberId(member))).Member("last_report_at")!.Value;
    }

    [Fact]
    public void AnOpenExchangeDoesNotHideAnOlderReport()
    {
        var reader = Truth();
        reader.Exchanges["a"] = ReportAt("developer", "orchestrator", "dev", null, "2026-06-01T11:10:00Z");
        reader.Exchanges["b"] = Msg("developer", "orchestrator", "dev", null, "default");
        reader.Exchanges["c"] = ReportAt("developer", "orchestrator", "dev", null, "2026-06-01T11:30:00Z");

        Assert.Equal("2026-06-01T11:30:00Z", LastReportOf(reader, "dev").AsString());
    }

    [Fact]
    public void NegativeAnAnswerTheMemberGotIsNotItsReport()
    {
        var reader = Truth();
        reader.Exchanges["a"] = ReportAt("tester", "developer", "qa", "dev", "2026-06-01T11:30:00Z");

        Assert.True(LastReportOf(reader, "dev").IsNull);
    }

    [Fact]
    public void NegativeAWithdrawnExchangeIsNoReport()
    {
        var reader = Truth();
        reader.Exchanges["a"] = ReportAt("developer", "orchestrator", "dev", null, "2026-06-01T11:30:00Z", withdrawn: true);

        Assert.True(LastReportOf(reader, "dev").IsNull);
    }

    [Fact]
    public void NegativeAnOpenExchangeToTheMemberIsNoReport()
    {
        var reader = Truth();
        reader.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "default");

        Assert.True(LastReportOf(reader, "dev").IsNull);
    }
}
