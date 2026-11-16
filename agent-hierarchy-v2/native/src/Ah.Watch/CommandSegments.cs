using Ah.Core;
using Ah.Core.Shell;

namespace Ah.Watch;

internal static class CommandSegments
{
    public static IReadOnlyList<CommandSegment> Of(string? text) => CoreModule.ShellCommands(text);

    public static IReadOnlyList<CommandSegment> OfWords(IReadOnlyList<string> words) => CoreModule.ShellCommandsOfWords(words);

    public static string BaseOf(string word) => word[(word.LastIndexOf('/') + 1)..];
}
