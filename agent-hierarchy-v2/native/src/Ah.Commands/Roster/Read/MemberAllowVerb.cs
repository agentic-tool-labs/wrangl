using System.Text;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Roster;

namespace Ah.Commands;

/// <summary>
/// <c>ah allow on|off|list</c>: the switch for the launch-only permission rules ah writes for the members it spawns. The state is the
/// <c>member_allow</c> key of the user's config. <c>off</c> narrows and <c>on</c> widens, so <c>on</c> needs the user at a terminal or an
/// Orchestrator holding a fresh, unused Allow answer to the question tagged <see cref="ConfigModule.AllowOnSource"/>.
/// </summary>
internal static class MemberAllowVerb
{
    private const string Key = "member_allow";
    private const string MembersRefused = "members cannot change member launch rules";
    private const string WideningRefused = "widening needs the user or a fresh Allow proof (" + ConfigModule.AllowOnSource + ")";
    private static readonly string[] Flags = ["cwd", "team", "json", "orchestrator-pid", "session"];

    [Verb("roster:member-allow")]
    internal static int Allow(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, Run);

    private static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys.Where(k => !Flags.Contains(k, StringComparer.Ordinal)))
        {
            throw new RosterFailure($"allow: unrecognized flag --{key}");
        }

        var action = c.Opts.Positionals.Count == 1 ? c.Opts.Positionals[0] : null;
        return action switch
        {
            "list" => List(c),
            "off" => Off(c),
            "on" => On(c),
            _ => throw new RosterFailure("allow needs one action: on, off or list (or lint)"),
        };
    }

    private static string AsksPath(RosterContext c) => NodePath.Join(c.Dir, "asks.jsonl");

    private static bool Enabled(RosterContext c) =>
        RosterModule.MemberAllowEnabled(c.Services.Files, [c.MemberAllowProjectConfig, c.Services.Config.UserConfigPath()]);

    private static int List(RosterContext c)
    {
        var enabled = Enabled(c);
        var (state, verdict) = State(c, enabled, MemberAllowSettings(UserConfig(c, tolerant: true)));
        if (c.Opts.IsTrue("json"))
        {
            var consent = new JsonObject { ["state"] = state };
            consent["cv"] = verdict.Version is { } cv ? (double)cv : JsonValue.Null;
            consent["at"] = verdict.At is { } at ? JsonValue.FromString(at) : JsonValue.Null;
            c.Out(new JsonObject { ["enabled"] = enabled, ["consent"] = consent });
            return 0;
        }

        c.Stdout.WriteLine($"enabled: {(enabled ? "true" : "false")}");
        c.Stdout.WriteLine($"consent: {state}" + (verdict.Version is { } v ? $" (cv {v}, {verdict.At})" : string.Empty));
        return 0;
    }

    private static int Off(RosterContext c)
    {
        var (user, orchestrator) = Caller(c);
        if (!user && !orchestrator)
        {
            throw new RosterFailure(MembersRefused);
        }

        Write(c, UserConfig(c, tolerant: false), settings => settings["enabled"] = false);
        c.Stdout.WriteLine("member launch rules: off");
        return 0;
    }

    private static int On(RosterContext c)
    {
        var (user, orchestrator) = Caller(c);
        if (!user && !orchestrator)
        {
            throw new RosterFailure(MembersRefused);
        }

        var config = UserConfig(c, tolerant: false);
        var settings = MemberAllowSettings(config);
        string? proof = null;
        if (!user)
        {
            var used = settings.Get("proof_at") is { IsText: true } u ? u.AsString() : null;
            proof = ConfigModule.FreshAllowOnProof(c.Services.Files, AsksPath(c), c.Services.Clock.UtcNow, used)
                ?? throw new RosterFailure(WideningRefused);
        }

        var (state, _) = State(c, enabled: true, settings);
        var stamp = Timestamps.ToIsoString(c.Services.Clock.UtcNow);
        Write(c, config, s =>
        {
            s["enabled"] = true;
            if (state == "declined")
            {
                s["reask_at"] = stamp;
            }

            if (proof is not null)
            {
                s["proof_at"] = proof;
            }
        });
        c.Stdout.WriteLine("member launch rules: on");
        return 0;
    }

    /// <summary>Whether the caller is the user at a terminal, or the team's Orchestrator (or a sub-orchestrator it proves).</summary>
    private static (bool User, bool Orchestrator) Caller(RosterContext c)
    {
        if (c.Services.OwnerProof is not { } owner)
        {
            throw new RosterFailure("allow: the owner proof is not provided");
        }

        if (owner.IsUser())
        {
            return (true, false);
        }

        return (false, owner.Check(c.TeamHome, c.TeamFile).Allowed);
    }

    private static (string State, MemberLaunchVerdict Verdict) State(RosterContext c, bool enabled, JsonObject settings)
    {
        var onAt = settings.Get("reask_at") is { IsText: true } at ? at.AsString() : null;
        var verdict = ConfigModule.EvaluateMemberLaunchConsent(c.Services.Files, c.ConsentAsksPath, true, onAt);
        var state = !enabled ? "off" : verdict.Reason is null ? "given" : "declined";
        return (state, verdict);
    }

    private static JsonObject MemberAllowSettings(JsonObject config) =>
        config.Get(Key) is { IsMap: true } m ? m.AsObject().Clone() : new JsonObject();

    private static JsonObject UserConfig(RosterContext c, bool tolerant)
    {
        var path = c.Services.Config.UserConfigPath();
        if (!c.Services.Files.Exists(path))
        {
            return new JsonObject();
        }

        try
        {
            return JsonText.Parse(c.Services.Files.ReadText(path)) is { IsMap: true } map
                ? map.AsObject().Clone()
                : tolerant ? new JsonObject() : throw new RosterFailure($"allow: {path} is not a JSON object; fix it first");
        }
        catch (JsonParseException) when (tolerant)
        {
            return new JsonObject();
        }
        catch (JsonParseException)
        {
            throw new RosterFailure($"allow: {path} is not valid JSON; fix it first");
        }
    }

    private static void Write(RosterContext c, JsonObject config, Action<JsonObject> change)
    {
        var writer = c.Services.Writer ?? throw new RosterFailure("allow: no file writer is provided");
        var settings = MemberAllowSettings(config);
        change(settings);
        config[Key] = settings;
        var path = c.Services.Config.UserConfigPath();
        var dir = NodePath.Dirname(path);
        if (!c.Services.Files.Exists(dir))
        {
            writer.Mkdir(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        writer.WriteAtomic(path, path + ".tmp", Encoding.UTF8.GetBytes(JsonText.Stringify(config, 2) + "\n"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
