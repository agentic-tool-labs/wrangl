namespace Ah.Cli.Login;

internal static class LoginState
{
    /// <summary><c>on</c>, <c>off</c>, <c>broken</c> or <c>unsupported</c>.</summary>
    internal static string Detect(CliContext context, LoginItem? item) =>
        item is null ? "unsupported" : Describe(item, out _);

    internal static string Detect(CliContext context) => Detect(context, LoginItem.For(context));

    /// <summary>The state, and the reason when it is broken.</summary>
    internal static string Describe(LoginItem item, out string? reason)
    {
        reason = null;
        string text;
        try
        {
            text = File.ReadAllText(item.UnitPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "off";
        }

        if (!item.OwnedBy(text))
        {
            reason = $"{item.UnitPath} is not ahd's";
            return "broken";
        }

        reason = item.BrokenReason(text, item.Loaded());
        return reason is null ? "on" : "broken";
    }
}
