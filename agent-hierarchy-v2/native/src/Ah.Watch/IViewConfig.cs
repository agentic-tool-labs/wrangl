namespace Ah.Watch;

/// <summary>The one configuration question a view build asks, answered by the configuration resolver.</summary>
public interface IViewConfig
{
    /// <summary>Whether the repository <paramref name="dir"/> belongs to has ah2 enabled in its configuration.</summary>
    /// <param name="dir">A directory inside the repository.</param>
    /// <returns>True when the repository is configured and enabled.</returns>
    bool EnabledFor(string dir);
}
