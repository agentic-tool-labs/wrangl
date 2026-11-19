using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.DaemonClient;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// What SessionStart and <c>roster doctor</c> say about the native binary's install stamp, <c>&lt;AH_HOME&gt;/install.json</c>,
/// from one decision (<see cref="Decide"/>). A stamp behind the plugin (its contract differs, else its version is older) that opts
/// into <c>auto_rebuild</c> (exactly true) is rebuilt in the background unless a rebuild for this plugin version started less than
/// 15 minutes ago (in progress) or failed within 24 hours; a started rebuild older than 15 minutes is stalled and started again.
/// The rebuild is never waited for.
/// </summary>
internal static class InstallStampNotice
{
    private static readonly double DayMs = TimeSpan.FromHours(24).TotalMilliseconds;

    private static readonly double StallMs = TimeSpan.FromMinutes(15).TotalMilliseconds;

    /// <summary>
    /// The outcome for a stamp: <c>current</c>, <c>no-stamp</c>, <c>auto-off</c>, <c>failed</c>, <c>in-progress</c>, <c>stalled</c>,
    /// <c>spawn-depth</c>, <c>no-plugin-root</c>, <c>no-installer</c> or <c>start</c>. <c>stalled</c> and <c>start</c> launch a
    /// rebuild. <see cref="Behind"/> is <c>contract</c> or <c>version</c> when the stamp is behind, with the stamp's value in
    /// <see cref="Have"/> and the plugin's in <see cref="Want"/>; <see cref="AgeMs"/> is the age of the last rebuild it rests on.
    /// </summary>
    internal sealed record Decision(string Outcome, string? Behind, string Have, string Want, string Link, double? AgeMs, string? Script);

    /// <summary>
    /// The one rule: what to do about <paramref name="stamp"/> (null when there is no <c>install.json</c>). Reads only. A
    /// <paramref name="requested"/> rebuild (<c>roster doctor --fix</c>) skips the automatic holds: <c>auto-off</c>, <c>failed</c>
    /// and <c>spawn-depth</c>.
    /// </summary>
    internal static Decision Decide(JsonObject? stamp, string pluginVersion, IAhEnvironment env, IFileReader files, DateTimeOffset now, bool requested = false)
    {
        if (stamp is null)
        {
            return new Decision("no-stamp", null, string.Empty, string.Empty, "ahd", null, null);
        }

        int? planned = int.TryParse(env.Plugin?.Contract, NumberStyles.None, CultureInfo.InvariantCulture, out var contract) ? contract : null;
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

        var link = stamp.Get("link") is { IsText: true } l && l.AsString().Length > 0 ? l.AsString()
            : stamp.Get("binary") is { IsText: true } bin ? bin.AsString() : "ahd";
        Decision Of(string outcome, double? age = null, string? script = null) => new(outcome, behindKind, a, b, link, age, script);

        if (behindKind is null)
        {
            return Of("current");
        }

        if (!requested && !(stamp.Get("auto_rebuild") is { IsBool: true } auto && auto.AsBool()))
        {
            return Of("auto-off");
        }

        var nowMs = (double)now.ToUnixTimeMilliseconds();
        double? stalledAge = null;
        if (stamp.Get("last_rebuild") is { IsMap: true } last)
        {
            var lastObject = last.AsObject();
            if (lastObject.Get("version") is { IsText: true } lv && lv.AsString() == pluginVersion
                && lastObject.Get("at") is { IsText: true } at && Timestamps.TryParseMs(at.AsString(), out var atMs))
            {
                var age = nowMs - atMs;
                var state = lastObject.Get("state") is { IsText: true } st ? st.AsString() : null;
                if (!requested && state == "failed" && age < DayMs)
                {
                    return Of("failed", age);
                }

                if (state == "started")
                {
                    if (age < StallMs)
                    {
                        return Of("in-progress", age);
                    }

                    stalledAge = age;
                }
            }
        }

        var root = env.Plugin?.Root;
        if (root is null)
        {
            return Of("no-plugin-root", stalledAge);
        }

        if (!requested && SpawnDepth(env) > 0)
        {
            return Of("spawn-depth", stalledAge);
        }

        var script = NodePath.Join(root, "scripts", "install.sh");
        if (!files.Exists(script))
        {
            return Of("no-installer", stalledAge, script);
        }

        return Of(stalledAge is null ? "start" : "stalled", stalledAge, script);
    }

    /// <summary>The lines to add to the session context (zero or one). A stamp that cannot be read or parsed throws.</summary>
    internal static IReadOnlyList<string> Lines(GateServices s, IInstallStampHost host, string pluginVersion)
    {
        var home = s.Environment.AhHome;
        var file = NodePath.Join(home, "install.json");
        var stamp = ReadStamp(s.Files, file);
        var d = Decide(stamp, pluginVersion, s.Environment, s.Files, s.Clock.UtcNow);
        switch (d.Outcome)
        {
            case "no-stamp" or "current" or "in-progress":
                return [];
            case "auto-off":
                return [$"{BehindText(d)}; run /wrangl:hierarchy install to rebuild it"];
            case "failed":
                return [$"the background rebuild of ahd failed; see {InjectedText.Quote(RebuildLog(home))}"];
            case "spawn-depth" or "no-plugin-root" or "no-installer":
                return [$"{BehindText(d)}; it was not rebuilt in the background because {Reason(d, s.Environment)}; run /wrangl:hierarchy update"];
        }

        StartRebuild(host, s.Environment, stamp!, file, d.Script!, pluginVersion, s.Clock.UtcNow);
        return [$"ahd is being rebuilt in the background for {InjectedText.Clean(pluginVersion, 40)}"];
    }

    /// <summary>
    /// <c>roster doctor --fix</c> for the <c>ahd-build</c> row: starts the rebuild through <see cref="StartRebuild"/> unless the
    /// stamp is current, a rebuild is in progress, or it cannot be fixed (no stamp, no plugin root, no installer). The automatic
    /// holds do not stop it. Returns the <c>fix</c> entry, or null when current or not installed (the row already says so).
    /// </summary>
    internal static string? Fix(IInstallStampHost host, IAhEnvironment env, IFileReader files, string pluginVersion, DateTimeOffset now)
    {
        var home = env.AhHome;
        var file = NodePath.Join(home, "install.json");
        var stamp = ReadStamp(files, file);
        var shown = Decide(stamp, pluginVersion, env, files, now).Outcome;
        var d = Decide(stamp, pluginVersion, env, files, now, requested: true);
        switch (d.Outcome)
        {
            case "current" or "no-stamp":
                return null;
            case "in-progress":
                return $"ahd-build {shown}: a rebuild started {Ago(d.AgeMs)} ago is in progress; no rebuild started";
            case "no-plugin-root" or "no-installer":
                return $"ahd-build {shown}: cannot fix: {Reason(d, env)}; no rebuild started";
        }

        StartRebuild(host, env, stamp!, file, d.Script!, pluginVersion, now);
        return $"ahd-build {shown}: rebuild started; log {InjectedText.Quote(RebuildLog(home))}; run ah roster doctor again to see the result";
    }

    /// <summary>
    /// The one way a rebuild starts, for SessionStart and <c>roster doctor --fix</c>: records <c>last_rebuild</c> as started in the
    /// stamp, then starts the installer detached with <c>AH_SPAWN_DEPTH</c> one deeper.
    /// </summary>
    private static void StartRebuild(IInstallStampHost host, IAhEnvironment env, JsonObject stamp, string file, string script, string pluginVersion, DateTimeOffset now)
    {
        var home = env.AhHome;
        var updated = new JsonObject();
        foreach (var key in stamp.Keys)
        {
            updated[key] = stamp[key];
        }

        updated["last_rebuild"] = new JsonObject
        {
            ["version"] = pluginVersion,
            ["at"] = Timestamps.ToIsoString(now),
            ["state"] = "started",
        };
        // One line, "key":value: the form install.sh writes and its sed readers (stamp_raw_field, fail_step) match.
        host.WriteStamp(file, JsonText.Stringify(updated, 0) + "\n");
        host.StartRebuild(
            script,
            home,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["HOME"] = env.HomeDirectory,
                ["PATH"] = env.GetVariable("PATH"),
                ["AH_HOME"] = home,
                ["AH_SPAWN_DEPTH"] = (SpawnDepth(env) + 1).ToString(CultureInfo.InvariantCulture),
            });
    }

    /// <summary>
    /// The <c>ahd-build</c> row of <c>roster doctor</c>: the stamp's version and contract against the plugin's, and for anything
    /// but <c>current</c> the reason and the fix. <c>current</c> and <c>in-progress</c> are ok, the rest warn. Reads only.
    /// </summary>
    internal static (string Level, string Detail) DoctorRow(IAhEnvironment env, IFileReader files, string pluginVersion, DateTimeOffset now)
    {
        var home = env.AhHome;
        var file = NodePath.Join(home, "install.json");
        var stamp = ReadStamp(files, file);
        var d = Decide(stamp, pluginVersion, env, files, now);
        if (d.Outcome == "no-stamp")
        {
            return ("warn", $"not installed: no {InjectedText.Quote(file)}; run /wrangl:hierarchy install");
        }

        static string Field(JsonObject o, string key) => o.Get(key) switch
        {
            { IsText: true } t => InjectedText.Clean(t.AsString(), 40),
            { IsNumber: true } n => JsonText.Stringify(n, 0),
            _ => "?",
        };

        var versions = $"stamp {Field(stamp!, "version")} contract {Field(stamp!, "contract")}, plugin {InjectedText.Clean(pluginVersion, 40)} contract {InjectedText.Clean(env.Plugin?.Contract ?? "?", 40)}";
        return d.Outcome switch
        {
            "current" => ("ok", versions),
            "in-progress" => ("ok", $"{versions}; a background rebuild started {Ago(d.AgeMs)} ago is in progress"),
            "failed" => ("warn", $"{versions}; {Reason(d, env)}; see {InjectedText.Quote(RebuildLog(home))}; run /wrangl:hierarchy update or ah roster doctor --fix"),
            _ => ("warn", $"{versions}; {Reason(d, env)}; run /wrangl:hierarchy update or ah roster doctor --fix"),
        };
    }

    /// <summary>The outcome in plain words.</summary>
    private static string Reason(Decision d, IAhEnvironment env) => d.Outcome switch
    {
        "auto-off" => "auto_rebuild is off, so it is not rebuilt in the background",
        "failed" => $"the background rebuild failed {Ago(d.AgeMs)} ago",
        "stalled" => $"the background rebuild started {Ago(d.AgeMs)} ago never finished; the next session start starts it again",
        "spawn-depth" => $"this process was started by another ah process (AH_SPAWN_DEPTH={SpawnDepth(env).ToString(CultureInfo.InvariantCulture)})",
        "no-plugin-root" => "the plugin root is not known",
        "no-installer" => $"the install script {InjectedText.Quote(d.Script ?? "scripts/install.sh")} is missing",
        _ => "no background rebuild has started yet; the next session start starts one",
    };

    private static string BehindText(Decision d) => d.Behind == "contract"
        ? $"ahd in {InjectedText.Quote(d.Link)} is built for contract {d.Have}; this plugin needs {d.Want}"
        : $"ahd in {InjectedText.Quote(d.Link)} is older than the plugin ({d.Have} < {d.Want})";

    private static string Ago(double? ms) => Age.Format((ms ?? double.NaN) / 1000);

    private static string RebuildLog(string home) => NodePath.Join(home, "logs", "rebuild.log");

    /// <summary>The stamp, or null when the file does not exist. A stamp that cannot be read or is not a JSON object throws.</summary>
    private static JsonObject? ReadStamp(IFileReader files, string file)
    {
        if (!files.Exists(file))
        {
            return null;
        }

        return JsonText.Parse(files.ReadText(file)) is { IsMap: true } document
            ? document.AsObject()
            : throw new InvalidDataException("install.json is not a JSON object");
    }

    /// <summary>The spawn depth marker, read strictly: anything but a plain non-negative integer counts as 0.</summary>
    private static int SpawnDepth(IAhEnvironment env) =>
        int.TryParse(env.GetVariable("AH_SPAWN_DEPTH"), NumberStyles.None, CultureInfo.InvariantCulture, out var depth) ? depth : 0;
}
