using Ah.Abstractions;

namespace Ah.Cli;

/// <summary>What a verb needs from the machine. The composition root supplies it; tests supply fakes.</summary>
internal sealed record CliContext(
    IAhEnvironment Env,
    IExec Exec,
    IProcessRunner Runner,
    string Argv0,
    string ProcessPath,
    string? PasswdHome,
    uint Uid,
    Func<ReadOnlyMemory<byte>>? ReadStdin = null,
    Lazy<Composition.ServiceGraph>? Services = null,
    Func<Composition.DecisionCaller?>? DecisionCaller = null,
    Func<bool>? UiAnswerCaller = null,
    bool StdoutIsTerminal = false)
{
    /// <summary><paramref name="path"/> with its final link followed, or <paramref name="path"/> itself when it is no link or cannot be read.</summary>
    internal static string RealPath(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (IOException)
        {
            return path;
        }
    }
}
