using System.Globalization;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.DaemonClient;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// What SessionStart says about the native binary's install stamp, <c>&lt;AH_HOME&gt;/install.json</c>. Nothing here runs
/// without the file. A stamp behind the plugin (its contract differs, else its version is older) gives one line; when the stamp
/// opts into <c>auto_rebuild</c> (exactly true) and no rebuild started for this plugin version in the last 24 hours, the
/// rebuild is recorded as started and launched in the background, and a rebuild that failed within 24 hours gives one failure
/// line and no new start. The rebuild is never waited for, and a missing install script starts nothing.
/// </summary>
internal static class InstallStampNotice
{
    private static readonly double DayMs = TimeSpan.FromHours(24).TotalMilliseconds;

    /// <summary>The lines to add to the session context (zero or one). A stamp that cannot be read or parsed throws.</summary>
    internal static IReadOnlyList<string> Lines(GateServices s, IInstallStampHost host, string pluginVersion)
    {
        var home = s.Environment.AhHome;
        var file = NodePath.Join(home, "install.json");
        if (!s.Files.Exists(file))
        {
            return [];
        }

        if (JsonText.Parse(s.Files.ReadText(file)) is not { IsMap: true } document)
        {
            throw new InvalidDataException("install.json is not a JSON object");
        }

        var stamp = document.AsObject();
        int? planned = int.TryParse(s.Environment.Plugin?.Contract, NumberStyles.None, CultureInfo.InvariantCulture, out var contract) ? contract : null;
        string? behindKind = null;
        string a = string.Empty;
        string b = string.Empty;
        if (planned is { } wanted && stamp.Get("contract") is { IsNumber: true } sc && sc.AsNumber() == Math.Floor(sc.AsNumber()) && sc.AsNumber() != wanted)
        {
            behindKind = "contract";
            a = ((long)sc.AsNumber()).ToString(CultureInfo.InvariantCulture);
            b = wanted.ToString(CultureInfo.InvariantCulture);
        }
        else if (stamp.Get("version") is { IsText: true } sv && DaemonClientModule.CompareVersions(sv.AsString(), pluginVersion) < 0)
        {
            behindKind = "version";
            a = InjectedText.Clean(sv.AsString(), 40);
            b = InjectedText.Clean(pluginVersion, 40);
        }

        if (behindKind is null)
        {
            return [];
        }

        var link = stamp.Get("link") is { IsText: true } l && l.AsString().Length > 0 ? l.AsString()
            : stamp.Get("binary") is { IsText: true } bin ? bin.AsString() : "ahd";
        if (!(stamp.Get("auto_rebuild") is { IsBool: true } auto && auto.AsBool()))
        {
            return
            [
                behindKind == "contract"
                    ? $"ahd in {InjectedText.Quote(link)} is built for contract {a}; this plugin needs {b}; run /wrangl:hierarchy install to rebuild it"
                    : $"ahd in {InjectedText.Quote(link)} is older than the plugin ({a} < {b}); run /wrangl:hierarchy install to rebuild it",
            ];
        }

        var nowMs = (double)s.Clock.UtcNow.ToUnixTimeMilliseconds();
        var recent = false;
        var failed = false;
        if (stamp.Get("last_rebuild") is { IsMap: true } last)
        {
            var lastObject = last.AsObject();
            recent = lastObject.Get("version") is { IsText: true } lv && lv.AsString() == pluginVersion
                && lastObject.Get("at") is { IsText: true } at && Timestamps.TryParseMs(at.AsString(), out var atMs) && nowMs - atMs < DayMs;
            failed = recent && lastObject.Get("state") is { IsText: true } state && state.AsString() == "failed";
        }

        if (failed)
        {
            return [$"the background rebuild of ahd failed; see {InjectedText.Quote(NodePath.Join(home, "logs", "rebuild.log"))}"];
        }

        if (recent)
        {
            return [];
        }

        var root = s.Environment.Plugin?.Root;
        if (root is null || SpawnDepth(s) > 0)
        {
            return [];
        }

        var script = NodePath.Join(root, "scripts", "install.sh");
        if (!s.Files.Exists(script))
        {
            return [];
        }

        var updated = new JsonObject();
        foreach (var key in stamp.Keys)
        {
            updated[key] = stamp[key];
        }

        updated["last_rebuild"] = new JsonObject
        {
            ["version"] = pluginVersion,
            ["at"] = Timestamps.ToIsoString(s.Clock.UtcNow),
            ["state"] = "started",
        };
        host.WriteStamp(file, JsonText.Stringify(updated, 2) + "\n");
        host.StartRebuild(
            script,
            home,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["HOME"] = s.Environment.HomeDirectory,
                ["PATH"] = s.Environment.GetVariable("PATH"),
                ["AH_HOME"] = home,
                ["AH_SPAWN_DEPTH"] = (SpawnDepth(s) + 1).ToString(CultureInfo.InvariantCulture),
            });
        return [$"ahd is being rebuilt in the background for {InjectedText.Clean(pluginVersion, 40)}"];
    }

    /// <summary>The spawn depth marker, read strictly: anything but a plain non-negative integer counts as 0.</summary>
    private static int SpawnDepth(GateServices s) =>
        int.TryParse(s.Environment.GetVariable("AH_SPAWN_DEPTH"), NumberStyles.None, CultureInfo.InvariantCulture, out var depth) ? depth : 0;
}
