using System.Reflection;
using Ah.Config;

namespace Ah.Hooks.Tests;

/// <summary>The real Stop texts for tests that run a Stop hook: the texts are pure, so the services they are built over are placeholders that are never called.</summary>
internal static class StopDirective
{
    internal static IDirectiveText Instance { get; } = ConfigModule.CreateDirectiveText(
        DispatchProxy.Create<IConfigCore, Placeholder>(),
        DispatchProxy.Create<IRoleContracts, Placeholder>(),
        () => throw new NotSupportedException());

#pragma warning disable CA1852 // DispatchProxy needs a type it can derive from
    public class Placeholder : DispatchProxy
#pragma warning restore CA1852
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException();
    }
}
