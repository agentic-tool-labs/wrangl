using Ah.Core.Json;

namespace Ah.Launch;

/// <summary>What the composed launch-settings writer needs to decide a member's <c>--settings</c> value.</summary>
/// <param name="Member">The member's row.</param>
/// <param name="Settings">The settings every launch carries: <c>env</c> and <c>statusLine</c>.</param>
/// <param name="Context">The launch being planned.</param>
/// <param name="LaunchCwd">The directory the member starts in.</param>
public sealed record MemberLaunchSettingsRequest(JsonValue Member, JsonObject Settings, LaunchContext Context, string LaunchCwd);

/// <summary>
/// What the launch-settings writer decided: the launch-only file it wrote, or why the member launches with the inline settings. A
/// plain path (or null) converts to an outcome, so a writer that has no cause to give still compiles.
/// </summary>
public sealed record MemberLaunchOutcome
{
    public MemberLaunchOutcome(string? path, string? cause = null)
    {
        Path = path is { Length: > 0 } ? path : null;
        Cause = Path is not null ? null : cause ?? Unknown;
    }

    /// <summary>The cause when the writer threw.</summary>
    public const string WriteFailed = "write-failed";

    /// <summary>The cause when the writer was given no reason of its own for returning no file.</summary>
    public const string Unknown = "unknown";

    /// <summary>The absolute path of the launch-only file, or null when the member launches inline.</summary>
    public string? Path { get; }

    /// <summary>Why there is no file: a name from the remedy table. Null when there is a file.</summary>
    public string? Cause { get; }

    public static implicit operator MemberLaunchOutcome(string? path) => new(path);
}
