using Ah.Core.Json;

namespace Ah.Roster;

/// <summary>
/// What the launch-only permission rules of one member are generated from. Everything comes from the member's own row and its team,
/// never from a command or a hook payload.
/// </summary>
/// <param name="Member">The member's name.</param>
/// <param name="RoleClass">The class of the member's role (a name from the role classes).</param>
/// <param name="RoleName">The member's role, or null.</param>
/// <param name="WorkRoot">The absolute directory the member works in: its worktree, or the main checkout.</param>
/// <param name="MainCheckout">The absolute main checkout the work root belongs to.</param>
/// <param name="AhPath">The absolute path of <c>ah</c> that member directives print.</param>
/// <param name="PluginRoot">The plugin root; its <c>bin/ah</c> is allowed too.</param>
/// <param name="MemberTmp">The member's <c>TMPDIR</c>, or null.</param>
/// <param name="UserAllow">The allow rules of the user's and project's settings files, merged in last.</param>
/// <param name="UserSettingsProblems">Settings files that could not be read or parsed, for <c>permissions show</c>.</param>
public sealed record MemberPermissionsRequest(
    string Member,
    string RoleClass,
    string? RoleName,
    string WorkRoot,
    string MainCheckout,
    string AhPath,
    string PluginRoot,
    string? MemberTmp,
    IReadOnlyList<string>? UserAllow = null,
    IReadOnlyList<string>? UserSettingsProblems = null);

/// <summary>The rules generated for one member.</summary>
/// <param name="Permissions">The <c>permissions</c> object of the launch settings: <c>allow</c> only.</param>
/// <param name="Notes">Things <c>permissions show</c> tells the user, such as a settings file that could not be read.</param>
public sealed record MemberPermissionsResult(JsonObject Permissions, IReadOnlyList<string> Notes);

/// <summary>The rule that would allow one command, or the reason there is none.</summary>
public sealed record MemberRuleAnswer(string? Rule, string? Why);
