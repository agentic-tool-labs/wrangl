namespace Ah.Commands;

/// <summary>
/// herdr refuses to move a pane out of a zoomed tab, and a split into one lands under the zoom. A caller that must act on such a
/// tab turns the zoom off first and puts it back afterwards.
/// </summary>
internal static class PaneZoom
{
    /// <summary>The tab that holds a pane and the pane of it that is zoomed; each null when unknown or not zoomed.</summary>
    internal sealed record TabState(string? Tab, string? Zoomed);

    /// <summary>Reads the live layout of the tab that holds <paramref name="pane"/>.</summary>
    internal static TabState Read(RosterContext c, string pane)
    {
        try
        {
            var layout = c.Services.Launch!.Herdr.Invoke(["pane", "layout", "--pane", pane]).Member("result")?.Member("layout");
            var zoomed = layout?.Member("zoomed") is { IsTruthy: true } && MemberPlanning.Text(layout.Value.Member("focused_pane_id")) is { Length: > 0 } focused
                ? focused
                : null;
            return new TabState(MemberPlanning.Text(layout?.Member("tab_id")), zoomed);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new TabState(null, null);
        }
    }

    /// <summary>The zoomed pane of the tab that holds <paramref name="pane"/>, or null when the tab is not zoomed or cannot be read.</summary>
    internal static string? ZoomedIn(RosterContext c, string pane) => Read(c, pane).Zoomed;

    /// <summary>Zooms <paramref name="zoomed"/> off; null when it worked, else herdr's reason.</summary>
    internal static string? Off(RosterContext c, string zoomed)
    {
        try
        {
            c.Services.Launch!.Herdr.Invoke(["pane", "zoom", zoomed, "--off"]);
            return null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return error.Message;
        }
    }

    /// <summary>
    /// Puts the zoom back and says what happened. Never throws. A pane that was itself moved is left unzoomed: it no longer sits in
    /// the tab that was zoomed.
    /// </summary>
    internal static string Back(RosterContext c, string zoomed, string? movedPane)
    {
        if (zoomed == movedPane)
        {
            return "skipped (moved pane was zoomed)";
        }

        try
        {
            c.Services.Launch!.Herdr.Invoke(["pane", "zoom", zoomed, "--on"]);
            return "restored";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return $"failed: {error.Message}";
        }
    }
}
