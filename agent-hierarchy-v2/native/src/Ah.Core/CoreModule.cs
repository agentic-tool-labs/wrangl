using Ah.Core.Shell;

namespace Ah.Core;

public static class CoreModule
{
    /// <summary>
    /// Adds a long-lived cache of <paramref name="owner"/> to the health census under <paramref name="name"/>: <paramref name="count"/> its
    /// entries, <paramref name="bytes"/> its approximate size when that is cheap. The owner is held weakly.
    /// </summary>
    public static void CensusAdd<T>(T owner, string name, Func<T, long> count, Func<T, long>? bytes = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(count);
        Diagnostics.Census.Add(owner, name, o => count((T)o), bytes is null ? null : o => bytes((T)o));
    }

    /// <summary>The health census: per cache name its entry count, its owners and, where known, its bytes.</summary>
    public static Json.JsonObject CensusRead() => Diagnostics.Census.Read();

    /// <summary>Where the command line is that a wrapper before the program at <paramref name="program"/> takes as an option value: the word, and how many leading characters of it are the option itself; null when there is none.</summary>
    public static (int Index, int Prefix)? WrapperScriptWord(IReadOnlyList<string> words, int program)
    {
        ArgumentNullException.ThrowIfNull(words);
        return WrapperTable.ScriptWord(words, program);
    }

    /// <summary>The command line that a wrapper before the program at <paramref name="program"/> takes as an option value (<c>env -S</c>, <c>npx -c</c>), or null.</summary>
    public static string? WrapperScript(IReadOnlyList<string> words, int program)
    {
        ArgumentNullException.ThrowIfNull(words);
        return WrapperTable.ScriptWord(words, program) is var (index, prefix) ? words[index][prefix..] : null;
    }

    /// <summary>What the wrapper word at <paramref name="index"/> of <paramref name="words"/> wraps, or null when it is no wrapper; see <see cref="WrapperStep"/>.</summary>
    public static WrapperStep? StepWrapper(IReadOnlyList<string> words, int index)
    {
        ArgumentNullException.ThrowIfNull(words);
        return WrapperTable.Step(words, index);
    }

    /// <summary>The program word of every wrapper that is one word; the test bucket also runs as the three words <c>ah test run</c>.</summary>
    public static IReadOnlyList<string> WrapperWords => WrapperTable.Words;

    /// <summary>Whether the word at <paramref name="index"/> is a wrapper (as it is used there) or a shell: a program that runs the command after it.</summary>
    public static bool IsLauncher(IReadOnlyList<string> words, int index)
    {
        ArgumentNullException.ThrowIfNull(words);
        return WrapperTable.IsLauncher(words, index);
    }

    /// <summary>The commands of a shell text, normalised; see <see cref="CommandSegment"/>.</summary>
    public static IReadOnlyList<CommandSegment> ShellCommands(string? text) => CommandSegments.Of(text);

    /// <summary>The same, and whether the text has more commands than the work allowed for (<paramref name="unreadable"/>): the list is then incomplete and a gate fails closed.</summary>
    public static IReadOnlyList<CommandSegment> ShellCommandsChecked(string? text, out bool unreadable) => CommandSegments.Of(text, out unreadable);

    /// <summary>The same for one command that is already split into words.</summary>
    public static IReadOnlyList<CommandSegment> ShellCommandsOfWords(IReadOnlyList<string> words) => CommandSegments.OfWords(words);

    /// <summary>One word quoted so that a shell reads it back as the same word.</summary>
    public static string QuoteShellWord(string word) => CommandSegments.Quote(word);
}
