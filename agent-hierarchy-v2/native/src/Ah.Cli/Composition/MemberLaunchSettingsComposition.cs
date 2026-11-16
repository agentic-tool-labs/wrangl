using System.Text;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Hooks;
using Ah.Launch;
using Ah.Roster;

namespace Ah.Cli.Composition;

/// <summary>
/// Composes the writer of a consented member's launch-only settings file. Without effective consent, a plugin root, or a rule set the
/// generator accepts, it writes nothing, removes a file an earlier launch left, and the member launches with the inline settings.
/// </summary>
internal sealed class MemberLaunchSettingsComposition(IAhEnvironment environment, IFileReader files, IRawFileWriter writer, IConfigCore config, IRosterCore roster, IClock clock)
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal Func<MemberLaunchSettingsRequest, MemberLaunchOutcome?> Prepare => Run;

    /// <summary>The directory a team's launch-only files live in: the one <c>permissions diagnose</c> reads.</summary>
    internal static string LaunchDir(IConfigCore config, IRosterCore roster, LaunchContext context) => NodePath.Join(TeamHome(config, roster, context), "launch");

    /// <summary>The team's home: the one <c>--team</c> pinned when the owned team is homed in another repo, else <see cref="LaunchContext.Cwd"/>'s main checkout's.</summary>
    private static string TeamHome(IConfigCore config, IRosterCore roster, LaunchContext context) =>
        roster.TeamHome(context.HierarchyDir ?? config.HierarchyDir(context.Cwd));

    private MemberLaunchOutcome Run(MemberLaunchSettingsRequest request)
    {
        var name = request.Member.Member("name") is { IsText: true } n ? n.AsString() : throw new InvalidOperationException("the member has no name");
        var launchDir = LaunchDir(config, roster, request.Context);
        var file = NodePath.Join(launchDir, name + ".settings.json");
        var meta = NodePath.Join(launchDir, name + ".meta.json");
        try
        {
            var (built, cause) = Settings(request, name);
            if (built is null)
            {
                Remove(file, meta);
                return new MemberLaunchOutcome(null, cause);
            }

            writer.Mkdir(launchDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            writer.WriteAtomic(file, file + ".tmp", Encoding.UTF8.GetBytes(JsonText.Stringify(built.Settings, 2) + "\n"), OwnerOnly);
            var stamp = new JsonObject { ["surface_version"] = built.SurfaceVersion, ["generated_at"] = Timestamps.ToIsoString(clock.UtcNow) };
            writer.WriteAtomic(meta, meta + ".tmp", Encoding.UTF8.GetBytes(JsonText.Stringify(stamp, 2) + "\n"), OwnerOnly);
            return new MemberLaunchOutcome(file);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Remove(file, meta);
            return new MemberLaunchOutcome(null, MemberLaunchOutcome.WriteFailed);
        }
    }

    private sealed record Built(JsonObject Settings, double SurfaceVersion);

    /// <summary>The settings of a consented member, or the cause it has none: no consent, no plugin root, a role that is not text, or a launch tree that is not the member's root.</summary>
    private (Built? Built, string? Cause) Settings(MemberLaunchSettingsRequest request, string name)
    {
        var cwd = request.Context.Cwd;
        // Consent belongs to the project, so a member in a linked worktree reads the main checkout's log, or the team's home when --team pins one;
        // the member_allow switch is read from that same project.
        var hierarchyDir = TeamHome(config, roster, request.Context);
        var projectConfig = request.Context.HierarchyDir is not null ? NodePath.Join(hierarchyDir, config.ConfigBasename) : config.ProjectConfigPath(JsonValue.FromString(cwd));
        string?[] configPaths = [projectConfig, config.UserConfigPath()];
        var enabled = RosterModule.MemberAllowEnabled(files, configPaths);
        if (!ConfigModule.EvaluateMemberLaunchConsent(files, NodePath.Join(hierarchyDir, "asks.jsonl"), enabled, RosterModule.MemberAllowOnAt(files, config.UserConfigPath())).Effective)
        {
            return (null, "no-consent");
        }

        if (environment.Plugin?.Root is not { Length: > 0 } pluginRoot)
        {
            return (null, "no-plugin-root");
        }

        if (request.Member.Member("role") is not { IsText: true } role)
        {
            return (null, "role-not-text");
        }

        string? tmp = null;
        _ = request.Context.MemberTmp?.TryGetValue(name, out tmp);
        var mainCheckout = config.CheckoutRoot(request.LaunchCwd) ?? cwd;
        var ownTree = config.FindGitRoot(request.LaunchCwd) ?? cwd;
        // A lane spawn names the tree it launches in (the stream's), and nothing else has recorded a root for the member yet.
        var rowRoot = request.Context.LaunchCwd is not null
            ? request.LaunchCwd
            : request.Member.Member("work_root") is { IsText: true } assigned && assigned.AsString().Length > 0 ? assigned.AsString() : ownTree;
        if (rowRoot.TrimEnd('/') != request.LaunchCwd.TrimEnd('/'))
        {
            return (null, "root-mismatch");
        }

        var permissions = RosterModule.BuildMemberPermissionsRequest(
            files,
            environment.HomeDirectory,
            request.LaunchCwd,
            mainCheckout,
            pluginRoot,
            config.AhCommandPath(),
            name,
            role.AsString(),
            request.LaunchCwd,
            tmp);
        var generated = HooksModule.GenerateMemberPermissions(permissions);
        var settings = new JsonObject();
        foreach (var key in request.Settings.Keys)
        {
            settings[key] = request.Settings[key];
        }

        settings["permissions"] = generated.Permissions;

        var surface = JsonText.Parse(files.ReadText(NodePath.Join(pluginRoot, "policy/member-surface.json")));
        var callerAutoMode = request.Settings.Keys.Contains("autoMode") ? (JsonValue?)request.Settings["autoMode"] : null;
        if (MemberAutoMode.Compose(surface.Member("autoMode"), callerAutoMode, new MemberAutoMode.Tokens(request.LaunchCwd, tmp, config.AhCommandPath(), pluginRoot)) is { } autoMode)
        {
            settings["autoMode"] = autoMode;
        }

        return (new Built(settings, surface.Member("version") is { IsNumber: true } v ? v.AsNumber() : 0), null);
    }

    private void Remove(string file, string meta)
    {
        foreach (var path in new[] { file, meta })
        {
            try
            {
                _ = writer.Unlink(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a file that cannot be removed is read by diagnose as stale
            }
        }
    }
}
