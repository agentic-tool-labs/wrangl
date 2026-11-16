using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;

namespace Ah.Roster;

/// <summary>Builds a <see cref="MemberPermissionsRequest"/> from a member's own row and the files that describe its checkout.</summary>
internal static class MemberPermissionsInputs
{
    /// <summary>
    /// The request for <paramref name="member"/>. <paramref name="workRoot"/> is the member's own work root, never a path from a command.
    /// The settings files read for the user's allow rules are the project's local and shared settings under <paramref name="launchCwd"/> and the
    /// user's; a file that cannot be read or parsed counts as no rules and is named in the request's problems.
    /// </summary>
    internal static MemberPermissionsRequest Build(
        IFileReader files,
        string home,
        string launchCwd,
        string mainCheckout,
        string pluginRoot,
        string ahPath,
        string member,
        string role,
        string workRoot,
        string? memberTmp)
    {
        ArgumentNullException.ThrowIfNull(files);
        var roleClass = ConfigModule.BuiltinRoleClass(role) ?? throw new InvalidOperationException($"role {role} has no built-in class");
        var allow = new List<string>();
        var problems = new List<string>();
        foreach (var path in new[]
        {
            launchCwd.TrimEnd('/') + "/.claude/settings.local.json",
            launchCwd.TrimEnd('/') + "/.claude/settings.json",
            home.TrimEnd('/') + "/.claude/settings.json",
        })
        {
            try
            {
                if (files.Exists(path))
                {
                    allow.AddRange(TextsOf(JsonText.Parse(files.ReadText(path)).Member("permissions")?.Member("allow")));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException or InvalidOperationException)
            {
                problems.Add($"{path}: {e.Message}");
            }
        }

        return new MemberPermissionsRequest(member, roleClass, role, workRoot, mainCheckout, ahPath, pluginRoot, memberTmp, allow, problems);
    }

    /// <summary>The managed-settings paths whose presence alone makes the compound-allow gate silent (S6.8 G4.2).</summary>
    internal static readonly string[] ManagedSettingsPaths =
    [
        "/Library/Application Support/ClaudeCode/managed-settings.json",
        "/Library/Application Support/ClaudeCode/managed-settings.d",
        "/etc/claude-code/managed-settings.json",
        "/etc/claude-code/managed-settings.d",
    ];

    /// <summary>
    /// The one reader of a settings file's <c>permissions.deny</c>, <c>permissions.ask</c>, <c>sandbox.enabled</c> and
    /// <c>sandbox.excludedCommands</c>. A file that is missing reads as no rules; one that cannot be read or parsed carries its error.
    /// </summary>
    internal static SettingsScope ReadScope(IFileReader files, string path)
    {
        try
        {
            if (!files.Exists(path))
            {
                return new SettingsScope(path, false, null, null, [], [], []);
            }

            var doc = JsonText.Parse(files.ReadText(path));
            bool? sandbox = doc.Member("sandbox")?.Member("enabled") is { IsBool: true } on ? on.AsBool() : null;
            return new SettingsScope(
                path, true, null, sandbox, TextsOf(doc.Member("permissions")?.Member("deny")), TextsOf(doc.Member("permissions")?.Member("ask")), TextsOf(doc.Member("sandbox")?.Member("excludedCommands")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException or InvalidOperationException)
        {
            return new SettingsScope(path, true, e.Message, null, [], [], []);
        }
    }

    private static IReadOnlyList<string> TextsOf(JsonValue? array) =>
        array is { IsArray: true } a ? [.. a.AsArray().Where(r => r.IsText).Select(r => r.AsString())] : [];

    /// <summary>
    /// The scopes that merge into a member session's settings (the launch file, the work tree's shared and local settings, the user's), read
    /// by <see cref="ReadScope"/>, and whether any managed-settings path exists.
    /// </summary>
    internal static SessionSettings ReadSession(IFileReader files, string home, string launchFile, string workRoot)
    {
        var root = workRoot.TrimEnd('/');
        return new SessionSettings(
            [
                ReadScope(files, launchFile),
                ReadScope(files, root + "/.claude/settings.json"),
                ReadScope(files, root + "/.claude/settings.local.json"),
                ReadScope(files, home.TrimEnd('/') + "/.claude/settings.json"),
            ],
            ManagedSettingsPaths.Any(files.Exists));
    }
}
