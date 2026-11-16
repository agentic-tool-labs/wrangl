using Ah.Abstractions;

namespace Ah.Config;

public static partial class ConfigModule
{
    /// <summary>The behavioural contract text of the chain roles, assembled from the plugin's <c>contracts/</c> directory.</summary>
    public static IRoleContracts CreateRoleContracts(IConfigCore config, IFileReader files, IAhEnvironment environment, IHookErrorLog? errors = null) =>
        new RoleContracts(config, files, environment, errors);
}
