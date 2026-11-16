using Ah.Config;
using Ah.Watch;

namespace Ah.Cli.Composition;

/// <summary>The view's configuration seam: the configuration resolver's own enabled-for-dir answer.</summary>
internal sealed class ViewConfig(IConfigCore config) : IViewConfig
{
    public bool EnabledFor(string dir) => config.DirEnabled(dir);
}
