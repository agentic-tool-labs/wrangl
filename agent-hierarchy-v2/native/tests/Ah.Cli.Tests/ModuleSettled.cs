using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Ah.Cli.Composition;

namespace Ah.Cli.Tests;

/// <summary>
/// The executable composes its services (trust, roster, state and the daemon's) in a module initializer, which runs when the
/// first test touches the executable, in whichever test collection that is. A test that installs its own services in one of
/// those process-wide slots and then runs a verb must have the initializer behind it, or the real services can replace the
/// test's between the two. Running it here, when this test assembly loads, puts it behind every test, including a class added
/// later that installs a composed service.
/// </summary>
internal static class ModuleSettled
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries", Justification = "the test assembly must order the executable's own module initializer before any test")]
    internal static void Run() => RuntimeHelpers.RunModuleConstructor(typeof(DaemonComposition).Module.ModuleHandle);
}
