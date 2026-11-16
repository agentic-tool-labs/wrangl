using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Hooks.Gates;

/// <summary>Reads of a team's parked-question store and an asked question's labels, shared by the ask, push and relay recorders.</summary>
internal static class AskStore
{
    /// <summary>
    /// The store's text, read once (per-dir-files-to-db §6c G5); a store that cannot be read is read as empty, so its callers resolve,
    /// write and nudge nothing and the relay notice keeps its plain header.
    /// </summary>
    internal static string ReadStore(GateServices s, string dir) =>
        s.Files.ReadAskLog(ParkedQuestions.PathIn(dir)).TryLines(out var lines) ? string.Join('\n', lines) : string.Empty;

    /// <summary>The option labels of one question of an AskUserQuestion call, whether the options are strings or objects with a label.</summary>
    internal static List<string> AskedLabels(JsonValue question) =>
        question.Member("options") is { IsArray: true } options
            ? options.AsArray().Select(o => o.IsText ? o.AsString() : o.Member("label") is { IsText: true } l ? l.AsString() : null).OfType<string>().ToList()
            : [];
}
