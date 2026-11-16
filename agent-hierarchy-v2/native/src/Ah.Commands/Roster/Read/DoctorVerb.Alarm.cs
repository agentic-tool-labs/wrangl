using Ah.Core.Json;
using Ah.Watch;

namespace Ah.Commands;

internal static partial class DoctorVerb
{
    /// <summary>Whether a dead Orchestrator's alarms reach the user: the stall mode, <c>alarm.command</c> in the user config, and its last run. Never red.</summary>
    private static Check AlarmRow(RosterContext c)
    {
        var files = c.Services.Files;
        var mode = StallSettings.ModeOf(c.Services.Environment);
        if (mode != StallMode.On)
        {
            return new Check("ok", $"alarm: stall mode is {mode.ToString().ToLowerInvariant()}; no user alarms");
        }

        var config = c.Services.Config.UserConfigPath();
        if (!files.Exists(config) || WatchModule.AlarmCommandOf(files.ReadText(config)) is null)
        {
            return new Check("ok", c.Services.Launch?.HerdrFor(Ah.Core.Mux.HerdrFlavour.Tui).OnPath() == true
                ? "alarm: using herdr notifications (local only)"
                : "alarm: alarm.command not set — a dead Orchestrator's alarms reach only this status");
        }

        JsonValue? last = null;
        foreach (var line in c.Services.Wake?.JournalRows() ?? [])
        {
            if (JsonText.TryParse(System.Text.Encoding.UTF8.GetBytes(line), out var row) && row.Member("delivered_to") is { IsText: true } to && to.AsString() == "alarm.command"
                && row.Member("exit") is { IsNumber: true })
            {
                last = row;
            }
        }

        return new Check("ok", last is { } run
            ? $"alarm: alarm.command set; last run {MemberPlanning.JsConvertText(run.Member("at"))} exit {MemberPlanning.JsConvertText(run.Member("exit"))}"
            : "alarm: alarm.command set; never run — try ah alarm test");
    }
}
