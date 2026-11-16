using System.Text;
using Ah.Abstractions;

namespace Ah.Cli.Login;

/// <summary>One platform's login item: a one-shot starter that runs <c>ahd login run</c> at login.</summary>
internal abstract class LoginItem(CliContext context)
{
    protected CliContext Context { get; } = context;

    /// <summary>The service manager's command line tool.</summary>
    internal abstract string Tool { get; }

    internal abstract string UnitPath { get; }

    internal abstract byte[] Render(string binary);

    /// <summary>Whether an existing file at <see cref="UnitPath"/> is ours to replace or remove.</summary>
    internal abstract bool OwnedBy(string existingText);

    internal abstract bool Loaded();

    /// <summary>Null when the service manager answers; otherwise the message to print.</summary>
    internal abstract string? SessionProblem();

    internal abstract IReadOnlyList<string[]> StartCommands(bool loaded, bool unitChanged);

    internal abstract IReadOnlyList<string[]> StopCommands();

    internal abstract string? BrokenReason(string unitText, bool loaded);

    internal static LoginItem? For(CliContext context) =>
        OperatingSystem.IsMacOS() ? new LaunchdItem(context)
        : OperatingSystem.IsLinux() ? new SystemdItem(context)
        : null;

    protected int Run(params string[] argv) => Context.Runner.Run(new ProcessRequest(argv)).ExitCode ?? 1;

    protected static string Xml(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    protected static string Unxml(string value) =>
        value.Replace("&lt;", "<", StringComparison.Ordinal).Replace("&gt;", ">", StringComparison.Ordinal).Replace("&amp;", "&", StringComparison.Ordinal);
}

internal sealed class LaunchdItem(CliContext context) : LoginItem(context)
{
    internal const string Label = "io.github.agent-tools.ahd";

    internal override string Tool => "launchctl";

    internal override string UnitPath =>
        Path.Combine(Context.Env.HomeDirectory, "Library", "LaunchAgents", Label + ".plist");

    private string Domain => $"gui/{Context.Uid}";

    private string Target => $"{Domain}/{Label}";

    internal override byte[] Render(string binary)
    {
        var log = Path.Combine(Context.Env.AhHome, "logs", "login.log");
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n<dict>\n");
        sb.Append("  <key>Label</key>\n  <string>").Append(Xml(Label)).Append("</string>\n");
        sb.Append("  <key>ProgramArguments</key>\n  <array>\n");
        sb.Append("    <string>").Append(Xml(binary)).Append("</string>\n");
        sb.Append("    <string>login</string>\n    <string>run</string>\n  </array>\n");
        sb.Append("  <key>RunAtLoad</key>\n  <true/>\n");
        sb.Append("  <key>StandardOutPath</key>\n  <string>").Append(Xml(log)).Append("</string>\n");
        sb.Append("  <key>StandardErrorPath</key>\n  <string>").Append(Xml(log)).Append("</string>\n");
        sb.Append("  <key>EnvironmentVariables</key>\n  <dict>\n");
        sb.Append("    <key>HOME</key>\n    <string>").Append(Xml(Context.Env.HomeDirectory)).Append("</string>\n");
        sb.Append("    <key>PATH</key>\n    <string>").Append(Xml(Context.Env.GetVariable("PATH") ?? string.Empty)).Append("</string>\n");
        sb.Append("  </dict>\n</dict>\n</plist>\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    internal override bool OwnedBy(string existingText) => StringAfterKey(existingText, "Label") == Label;

    internal override bool Loaded() => Run("launchctl", "print", Target) == 0;

    internal override string? SessionProblem() => null;

    internal override IReadOnlyList<string[]> StartCommands(bool loaded, bool unitChanged) =>
        loaded && unitChanged ? [["launchctl", "bootout", Target], ["launchctl", "bootstrap", Domain, UnitPath]]
        : loaded ? []
        : [["launchctl", "bootstrap", Domain, UnitPath]];

    internal override IReadOnlyList<string[]> StopCommands() => [["launchctl", "bootout", Target]];

    internal override string? BrokenReason(string unitText, bool loaded)
    {
        var binary = StringAfterKey(unitText, "ProgramArguments", skipTags: "<array>");
        if (binary is null || !File.Exists(binary))
        {
            return $"the plist names a binary that does not exist ({binary ?? "none"})";
        }

        if (!loaded)
        {
            return "the job is not loaded while the plist exists";
        }

        return null;
    }

    /// <summary>The text of the first <c>&lt;string&gt;</c> element after <c>&lt;key&gt;name&lt;/key&gt;</c>, unescaped.</summary>
    private static string? StringAfterKey(string text, string key, string? skipTags = null)
    {
        var at = text.IndexOf($"<key>{key}</key>", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var from = at;
        if (skipTags is not null)
        {
            var skip = text.IndexOf(skipTags, from, StringComparison.Ordinal);
            if (skip < 0)
            {
                return null;
            }

            from = skip;
        }

        var open = text.IndexOf("<string>", from, StringComparison.Ordinal);
        var close = open < 0 ? -1 : text.IndexOf("</string>", open, StringComparison.Ordinal);
        return open < 0 || close < 0 ? null : Unxml(text[(open + "<string>".Length)..close]);
    }
}

internal sealed class SystemdItem(CliContext context) : LoginItem(context)
{
    internal const string UnitName = "ahd-login.service";

    internal const string Marker = "# ahd login item (managed by `ahd login on`; removed by `ahd login off`)";

    internal override string Tool => "systemctl";

    internal override string UnitPath =>
        Path.Combine(Context.Env.HomeDirectory, ".config", "systemd", "user", UnitName);

    internal override byte[] Render(string binary) => Encoding.UTF8.GetBytes(
        Marker + "\n"
        + "[Unit]\nDescription=Start the ahd daemon at login\n\n"
        + "[Service]\nType=oneshot\nExecStart=" + binary + " login run\n\n"
        + "[Install]\nWantedBy=default.target\n");

    internal override bool OwnedBy(string existingText) =>
        existingText.Split('\n').Any(line => line.TrimEnd('\r') == Marker);

    internal override bool Loaded() => Run("systemctl", "--user", "is-enabled", UnitName) == 0;

    internal override string? SessionProblem() =>
        Run("systemctl", "--user", "show-environment") == 0
            ? null
            : "no systemd --user session; start ahd from your shell profile with: ahd start";

    internal override IReadOnlyList<string[]> StartCommands(bool loaded, bool unitChanged) =>
        loaded && !unitChanged
            ? []
            : [["systemctl", "--user", "daemon-reload"], ["systemctl", "--user", "enable", "--now", UnitName]];

    internal override IReadOnlyList<string[]> StopCommands() => [["systemctl", "--user", "disable", "--now", UnitName]];

    internal override string? BrokenReason(string unitText, bool loaded)
    {
        const string prefix = "ExecStart=";
        var line = unitText.Split('\n').FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        var binary = line is null ? null : line[prefix.Length..].Replace(" login run", string.Empty, StringComparison.Ordinal).Trim();
        if (binary is null || !File.Exists(binary))
        {
            return $"the unit names a binary that does not exist ({binary ?? "none"})";
        }

        return loaded ? null : "the unit is not enabled while it exists";
    }
}
