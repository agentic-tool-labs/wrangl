using Ah.Abstractions;

namespace Ah.Config;

public static partial class ConfigModule
{
    /// <summary>
    /// The team-conventions reader; git runs through <paramref name="runner"/>. With <paramref name="files"/> and
    /// <paramref name="userHome"/> it also knows a repository that is the user's home and reads that project directory's file.
    /// </summary>
    public static IConventions CreateConventions(IProcessRunner runner, IFileReader? files = null, Func<string?>? userHome = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        return new Conventions(runner, files, userHome);
    }
}
