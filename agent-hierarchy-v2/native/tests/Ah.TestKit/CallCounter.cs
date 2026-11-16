using System.Reflection;

namespace Ah.TestKit;

/// <summary>Builds <see cref="CallCounter{T}"/> proxies.</summary>
public static class CallCounting
{
    /// <summary>Wraps <paramref name="target"/>; the counter is read through <paramref name="counter"/>.</summary>
    /// <typeparam name="T">The interface proxied.</typeparam>
    /// <param name="target">The object the calls are forwarded to.</param>
    /// <param name="counter">The proxy's counting side.</param>
    /// <returns>The proxy to hand to the code under test.</returns>
    public static T Wrap<T>(T target, out CallCounter<T> counter)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, CallCounter<T>>();
        counter = (CallCounter<T>)(object)proxy;
        counter.Target = target;
        return proxy;
    }
}

/// <summary>A proxy over an interface that forwards every call to a target and counts the calls by method name.</summary>
/// <typeparam name="T">The interface proxied.</typeparam>
public class CallCounter<T> : DispatchProxy
    where T : class
{
    private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);

    private readonly Dictionary<string, List<object?[]>> _arguments = new(StringComparer.Ordinal);

    internal T? Target { get; set; }

    /// <summary>When set, runs before every forwarded call; an exception it returns is thrown instead of calling the target.</summary>
    public Func<MethodInfo, object?[], Exception?>? Fault { get; set; }

    /// <summary>How many times the method called <paramref name="method"/> has run.</summary>
    /// <param name="method">The method name.</param>
    /// <returns>The call count.</returns>
    public int Count(string method)
    {
        lock (_calls)
        {
            return _calls.GetValueOrDefault(method);
        }
    }

    /// <summary>The arguments of every run of the method called <paramref name="method"/>, in call order.</summary>
    /// <param name="method">The method name.</param>
    /// <returns>One argument list per call.</returns>
    public IReadOnlyList<object?[]> ArgumentsOf(string method)
    {
        lock (_calls)
        {
            return [.. _arguments.GetValueOrDefault(method) ?? []];
        }
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        lock (_calls)
        {
            _calls[targetMethod.Name] = _calls.GetValueOrDefault(targetMethod.Name) + 1;
            if (!_arguments.TryGetValue(targetMethod.Name, out var seen))
            {
                _arguments[targetMethod.Name] = seen = [];
            }

            seen.Add(args is null ? [] : [.. args]);
        }

        if (Fault?.Invoke(targetMethod, args ?? []) is { } fault)
        {
            throw fault;
        }

        try
        {
            return targetMethod.Invoke(Target, args);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            throw e.InnerException;
        }
    }
}
