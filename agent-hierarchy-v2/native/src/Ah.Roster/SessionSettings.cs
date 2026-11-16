namespace Ah.Roster;

/// <summary>One settings file as the compound-allow gate needs it.</summary>
/// <param name="Path">The file's path.</param>
/// <param name="Exists">Whether the file exists.</param>
/// <param name="Error">Why an existing file could not be read or parsed, else null.</param>
/// <param name="SandboxEnabled"><c>sandbox.enabled</c> when it is a boolean, else null.</param>
/// <param name="Deny"><c>permissions.deny</c> rules.</param>
/// <param name="Ask"><c>permissions.ask</c> rules.</param>
/// <param name="ExcludedCommands"><c>sandbox.excludedCommands</c> entries.</param>
public sealed record SettingsScope(
    string Path,
    bool Exists,
    string? Error,
    bool? SandboxEnabled,
    IReadOnlyList<string> Deny,
    IReadOnlyList<string> Ask,
    IReadOnlyList<string> ExcludedCommands);

/// <summary>The settings scopes of a member session (the launch file first: then the work tree's shared and local settings, then the user's) and whether a managed-settings path exists.</summary>
/// <param name="Scopes">The scopes, launch file first.</param>
/// <param name="ManagedPresent">Whether any managed-settings path exists.</param>
public sealed record SessionSettings(IReadOnlyList<SettingsScope> Scopes, bool ManagedPresent);
