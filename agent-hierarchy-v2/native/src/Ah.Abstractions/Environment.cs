namespace Ah.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>The local UTC offset in effect at <paramref name="instant"/>, honouring <c>TZ</c>.</summary>
    TimeSpan LocalOffset(DateTimeOffset instant);
}

public interface IRandom
{
    void Fill(Span<byte> destination);
}

public sealed record PluginInfo(string Root, string Name, string Version, string Contract)
{
    /// <summary>The name a build that embedded none uses.</summary>
    public const string DefaultName = "wrangl";
}

public interface IAhEnvironment
{
    string? GetVariable(string name);

    /// <summary>
    /// Whether this process is a test: <c>AH_TEST</c> is exactly <c>1</c> <b>and</b> the real path of <c>AH_HOME</c> lies under a
    /// directory named <c>ahprobe.&lt;alphanumerics&gt;</c> directly inside <c>/tmp</c> or <c>/private/tmp</c> that is owned by
    /// the invoking user and not writable by group or other. <c>AH_TEST=1</c> alone is never test mode. Nothing else reads <c>AH_TEST</c>.
    /// </summary>
    bool TestMode { get; }

    /// <summary>
    /// Whether the test-mode predicate holds for the given <c>AH_TEST</c> and <c>AH_HOME</c> values instead of this process's own:
    /// the same rule as <see cref="TestMode"/>, applied to the values a command line would set. False when an implementation
    /// cannot judge.
    /// </summary>
    bool TestModeHolds(string? ahTest, string? ahHome) => false;

    /// <summary>Whether this is test mode with <c>HOME</c> being the account's real home, which a daemon refuses. False when an implementation cannot judge.</summary>
    bool TestModeInRealHome => false;

    /// <summary>Whether the test limiter is switched off (<c>AH_TEST_SLOTS=0</c> or <c>AH_TEST_TOKENS=0</c>), in and out of test mode. False when an implementation cannot judge.</summary>
    bool TestLimiterOff => false;

    /// <summary>Returns the variable only in <see cref="TestMode"/>; null otherwise.</summary>
    string? GetTestOnly(string name);

    /// <summary>
    /// The user's home. Outside <see cref="TestMode"/> it is the passwd entry's home and <c>HOME</c> is never read; in test mode
    /// <c>HOME</c> is honoured. Throws <see cref="InvalidOperationException"/> when the passwd database has no home for the user
    /// outside test mode: a caller refuses, it never falls back to <c>HOME</c>.
    /// </summary>
    string HomeDirectory { get; }


    /// <summary>The state home: <c>AH_HOME</c> when set, otherwise the default under <see cref="HomeDirectory"/>.</summary>
    string AhHome { get; }

    PluginInfo? Plugin { get; }

    /// <summary>
    /// The plugin's own name, the namespace its agents and skills are known by (<c>&lt;plugin&gt;:&lt;name&gt;</c>): the name the
    /// build embedded, or <see cref="PluginInfo.DefaultName"/> when it embedded none.
    /// </summary>
    string PluginName => Plugin?.Name is { Length: > 0 } name ? name : PluginInfo.DefaultName;

    /// <summary><c>&lt;plugin&gt;:&lt;name&gt;</c> in this plugin's namespace.</summary>
    string Qualified(string name) => PluginName + ":" + name;

    /// <summary>The part after <c>&lt;plugin&gt;:</c> when <paramref name="type"/> is in this plugin's namespace; null otherwise.</summary>
    string? LocalName(string? type)
    {
        var prefix = PluginName + ":";
        return type is not null && type.StartsWith(prefix, StringComparison.Ordinal) ? type[prefix.Length..] : null;
    }
}
