using Ah.Abstractions;

namespace Ah.Launch;

/// <summary>Which terminal multiplexer this command runs inside.</summary>
public interface IMuxTransports
{
    /// <summary>
    /// <c>herdr</c> when <c>HERDR_ENV=1</c> or <c>HC_TERMINAL_ID</c> is non-empty, else <c>tmux</c> when <c>tmux list-sessions</c>
    /// succeeds, else <c>terminal</c>.
    /// </summary>
    string Detect();

    /// <summary>The herdr flavour this session's env gives (<see cref="Ah.Core.Mux.HerdrFlavour.FromEnv"/>), null when not herdr.</summary>
    string? Flavour();
}

/// <summary>The transport detector over <paramref name="Mux"/>.</summary>
public sealed record MuxTransports(IAhEnvironment Environment, IMuxRunner Mux) : IMuxTransports
{
    public string Detect()
    {
        if (Flavour() is not null)
        {
            return "herdr";
        }

        try
        {
            return Mux.Run("tmux", ["list-sessions"], 0).Ok ? "tmux" : "terminal";
        }
        catch (InvalidOperationException)
        {
            return "terminal";
        }
    }

    public string? Flavour() =>
        Ah.Core.Mux.HerdrFlavour.FromEnv(Environment.GetVariable("HERDR_ENV"), Environment.GetVariable("HC_TERMINAL_ID"));
}
