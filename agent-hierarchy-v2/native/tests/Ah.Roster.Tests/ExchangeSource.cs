using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Roster.Tests;

/// <summary>An <see cref="IOpenExchangeSource"/> over the hierarchy core's exchange listing, for testing the stream rules.</summary>
internal sealed class ExchangeSource(PlatformIo io, IAhEnvironment env, IClock clock) : IOpenExchangeSource
{
    private readonly IHierarchyCore _core = HierarchyModule.CreateCore(io.Files, env, clock, new SequenceRandom(), io.Processes, TestMsgStore.Route(env.AhHome));

    public IReadOnlyList<OpenExchange> OpenExchanges(string dir, string? team, bool allTeams)
    {
        var result = new List<OpenExchange>();
        foreach (var e in _core.ListExchanges(dir))
        {
            if (e.Member("open") is not { IsTruthy: true })
            {
                continue;
            }

            var path = e.Member("request")?.Member("path")?.AsString() ?? string.Empty;
            if (!allTeams)
            {
                var tag = FrontMatter(path)?.Get("team") is { IsTruthy: true } t && t.IsText ? t.AsString() : null;
                if (tag != (string.IsNullOrEmpty(team) ? null : team))
                {
                    continue;
                }
            }

            var slug = e.Member("slug") is { IsText: true } s ? s.AsString() : null;
            result.Add(new OpenExchange(e.Member("id")!.Value.AsString(), slug, path));
        }

        return result;
    }

    public JsonObject? FrontMatter(string path) =>
        _core.ReadMsgFile(path) is { IsMap: true } parsed && parsed.AsObject().Get("fm") is { IsMap: true } fm ? fm.AsObject() : null;

    public double AgeSeconds(JsonValue? timestamp)
    {
        if (timestamp is not { IsText: true } t || !Timestamps.TryParseMs(t.AsString(), out var ms))
        {
            return double.PositiveInfinity;
        }

        return Math.Max(0, (clock.UtcNow.ToUnixTimeMilliseconds() - ms) / 1000);
    }
}
