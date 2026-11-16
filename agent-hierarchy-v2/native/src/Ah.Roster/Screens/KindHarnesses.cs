namespace Ah.Roster;

/// <summary>
/// What the hierarchy knows about launching and driving a non-Claude harness, read by kind: the command-line arguments its model, its
/// standing-instructions file, an extra writable directory and its approvals reviewer map to, the label it goes by, and which of its
/// prompts the screen recognizer knows. A kind with no harness entry answers false or null.
/// </summary>
public interface IKindHarnesses
{
    /// <summary>Whether the table has a harness for <paramref name="kind"/>.</summary>
    bool Has(string kind);

    /// <summary>The label a harness goes by in text shown to the user, or null for a kind with no harness.</summary>
    string? Label(string kind);

    /// <summary>The command that lists the models a harness accepts, or null for a kind with no harness.</summary>
    string? ModelsCommand(string kind);

    /// <summary>The arguments that give a member its model.</summary>
    IReadOnlyList<string> ModelArgs(string kind, string model);

    /// <summary>The arguments that point the harness at its standing-instructions file.</summary>
    IReadOnlyList<string> InstructionsArgs(string kind, string path);

    /// <summary>The arguments that add a writable directory to the harness's sandbox.</summary>
    IReadOnlyList<string> WritableDirArgs(string kind, string dir);

    /// <summary>The arguments that make <paramref name="name"/> an environment variable of the commands <paramref name="kind"/> runs; none for a kind with no such argument.</summary>
    IReadOnlyList<string> EnvArgs(string kind, string name, string value) => [];

    /// <summary>The arguments that send the harness's approvals to a human reviewer.</summary>
    IReadOnlyList<string> ApprovalsArgs(string kind);

    /// <summary>Whether the harness has a native legwork child a member can delegate to.</summary>
    bool HasNativeLegwork(string kind);

    /// <summary>How a member spawns, instructs and waits on a native legwork child, given the child's model or null.</summary>
    string NativeLegwork(string kind, string? model);

    /// <summary>Whether the harness draws a composer or prompts the screen recognizer reads (a startup screen can stop it).</summary>
    bool HasComposerOrPrompts(string kind);

    /// <summary>Whether a screen of <paramref name="kind"/> has a composer the recognizer reads: a harness with one, or Claude.</summary>
    bool HasComposerScreen(string kind);

    /// <summary>The heading line of the named prompt, or null when the harness has no such prompt.</summary>
    string? PromptHeading(string kind, string prompt);

    /// <summary>The glyphs a harness's composer row starts with; empty for a kind with no harness.</summary>
    IReadOnlyList<string> ComposerGlyphs(string kind);
}

/// <summary>The harness table of <c>lib-roster</c>.</summary>
public sealed record KindHarnesses(string KindDefault) : IKindHarnesses
{
    public bool Has(string kind) => KindHarness.Get(kind) is not null;

    public string? Label(string kind) => KindHarness.Get(kind)?.Label;

    public string? ModelsCommand(string kind) => KindHarness.Get(kind)?.ModelsCommand;

    public IReadOnlyList<string> ModelArgs(string kind, string model) => KindHarness.Get(kind)?.ModelArgs(model) ?? [];

    public IReadOnlyList<string> InstructionsArgs(string kind, string path) => KindHarness.Get(kind)?.InstructionsArgs(path) ?? [];

    public IReadOnlyList<string> WritableDirArgs(string kind, string dir) => KindHarness.Get(kind)?.WritableDirArgs(dir) ?? [];

    public IReadOnlyList<string> EnvArgs(string kind, string name, string value) => KindHarness.Get(kind)?.EnvArgs(name, value) ?? [];

    public IReadOnlyList<string> ApprovalsArgs(string kind) => KindHarness.Get(kind)?.ApprovalsArgs ?? [];

    public bool HasNativeLegwork(string kind) => KindHarness.Get(kind) is not null;

    public string NativeLegwork(string kind, string? model) => KindHarness.Get(kind)?.NativeLegwork(model) ?? string.Empty;

    public bool HasComposerOrPrompts(string kind) => KindHarness.Get(kind) is not null;

    public bool HasComposerScreen(string kind) => KindHarness.Get(kind) is not null || kind == KindDefault;

    public IReadOnlyList<string> ComposerGlyphs(string kind) => KindHarness.Get(kind)?.Composer.Glyphs ?? [];

    public string? PromptHeading(string kind, string prompt)
    {
        if (KindHarness.Get(kind) is not { } harness)
        {
            return null;
        }

        foreach (var (name, spec) in harness.Prompts)
        {
            if (name == prompt)
            {
                return spec.Heading;
            }
        }

        return null;
    }
}
