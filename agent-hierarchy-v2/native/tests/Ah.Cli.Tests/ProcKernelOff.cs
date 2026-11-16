using System.Runtime.CompilerServices;

namespace Ah.Cli.Tests;

/// <summary>These tests script <c>ps</c> for pids that do not exist, so the process table must not answer from the machine's kernel.</summary>
internal static class ProcKernelOff
{
    [ModuleInitializer]
    internal static void Init() => Environment.SetEnvironmentVariable("AH_PROC_KERNEL", "off");
}
