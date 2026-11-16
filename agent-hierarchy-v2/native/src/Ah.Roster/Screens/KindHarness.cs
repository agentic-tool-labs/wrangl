using System.Text;
using System.Text.RegularExpressions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Roster;

/// <summary>One label a prompt's option may read: an exact text, or a pattern.</summary>
internal sealed record LabelMatcher(string? Exact, Regex? Pattern)
{
    public static LabelMatcher Of(string exact) => new(exact, null);

    public static LabelMatcher Of(Regex pattern) => new(null, pattern);

    public bool Matches(string label) => Exact is not null ? Exact == label : Pattern!.IsMatch(label);
}

/// <summary>A prompt a harness draws: its heading, footer and the layout of its option block.</summary>
internal sealed record PromptSpec(
    string Heading,
    string Marker,
    Regex Footer,
    string Herdr,
    bool Onboarding,
    bool JoinLabels,
    bool OptionsSpaced,
    IReadOnlyList<LabelMatcher> Labels,
    bool OptionsAdjacent = false);

/// <summary>An answer that may be relayed to a prompt.</summary>
internal sealed record AnswerRow(
    string Id,
    string Label,
    bool Grants,
    IReadOnlyList<string> Keys,
    LabelMatcher OnScreen,
    string? Description,
    Func<JsonValue?, string>? DescriptionOf)
{
    public string DescriptionFor(JsonValue? context) => DescriptionOf is not null ? DescriptionOf(context) : Description!;
}

/// <summary>The idle composer's shape.</summary>
internal sealed record ComposerSpec(IReadOnlyList<string> Glyphs, IReadOnlyList<string> Placeholders, Regex Status, Regex Shortcuts, IReadOnlyList<string> Hints);

/// <summary>What the hierarchy knows about launching and driving one non-Claude harness.</summary>
internal sealed partial class KindHarness
{
    private const string Lt = "\\n\\r\\u2028\\u2029";
    private const string Sp = JsText.SpaceClass;

    public required string Label { get; init; }

    public required string ModelsCommand { get; init; }

    public required IReadOnlyList<string> ApprovalsArgs { get; init; }

    public required string ApprovalsKey { get; init; }

    public required ComposerSpec Composer { get; init; }

    public required IReadOnlyList<KeyValuePair<string, PromptSpec>> Prompts { get; init; }

    /// <summary>
    /// Screens that must never read as idle although no prompt of <see cref="Prompts"/> matches: each entry is a name and the
    /// consecutive screen lines (each matched after trimming) that show it.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, IReadOnlyList<Regex>>> BlockingAnchors { get; init; } = [];

    public required IReadOnlyList<string> OtherHeadings { get; init; }

    public required IReadOnlyList<KeyValuePair<string, IReadOnlyList<AnswerRow>>> Options { get; init; }

    /// <summary>How a member's model reaches the command line.</summary>
    public required Func<string, IReadOnlyList<string>> ModelArgs { get; init; }

    /// <summary>How the standing-instructions file reaches the command line.</summary>
    public required Func<string, IReadOnlyList<string>> InstructionsArgs { get; init; }

    /// <summary>How an extra writable directory reaches the command line.</summary>
    public required Func<string, IReadOnlyList<string>> WritableDirArgs { get; init; }

    /// <summary>How an environment variable the harness's commands run with reaches the command line, given its name and value.</summary>
    public required Func<string, string, IReadOnlyList<string>> EnvArgs { get; init; }

    /// <summary>Whether a member's <c>args</c> already set the model through one of the harness's spellings.</summary>
    public required Func<IReadOnlyList<JsonValue>, bool> ArgsSetModel { get; init; }

    /// <summary>How a member spawns, instructs and waits on a native legwork child, given the child's model or null.</summary>
    public required Func<string?, string> NativeLegwork { get; init; }

    private static IReadOnlyList<string> CodexModelArgs(string model) => ["--model", model];

    private static IReadOnlyList<string> CodexInstructionsArgs(string path) => ["-c", $"model_instructions_file={TomlString(path)}"];

    private static IReadOnlyList<string> CodexWritableDirArgs(string dir) => ["--add-dir", dir];

    private static IReadOnlyList<string> CodexEnvArgs(string name, string value) => ["-c", $"shell_environment_policy.set.{name}={TomlString(value)}"];

    private static bool CodexArgsSetModel(IReadOnlyList<JsonValue> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (!args[i].IsText)
            {
                continue;
            }

            var a = args[i].AsString();
            if (a == "--model" || a.StartsWith("--model=", StringComparison.Ordinal) || a == "-m" || (a.Length >= 3 && a.StartsWith("-m", StringComparison.Ordinal) && a[2] != '-'))
            {
                return true;
            }

            var next = i + 1 < args.Count ? args[i + 1] : (JsonValue?)null;
            if ((a == "-c" || a == "--config") && next is { IsText: true } n && ConfigModel(n.AsString()))
            {
                return true;
            }

            if ((a.StartsWith("-c", StringComparison.Ordinal) && a.Length > 2 && ConfigModel(a[2..]))
                || (a.StartsWith("--config=", StringComparison.Ordinal) && ConfigModel(a["--config=".Length..])))
            {
                return true;
            }
        }

        return false;
    }

    private static string CodexNativeLegwork(string? model)
    {
        var m = !string.IsNullOrEmpty(model) ? $", `model: {JsonText.Stringify(model, 0)}`" : string.Empty;
        var modelRule = !string.IsNullOrEmpty(model)
            ? "If the spawn is refused for that model, spawn once more without `model` and say so in your response."
            : "Do not set `model`: the child runs on your model.";
        return $"The tested form: `spawn_agent` with `task_name`, `message` and `fork_turns: \"none\"`{m}; then `wait_agent`; `followup_task` with `target` sends that child a further order. If your spawn tool's names or fields differ, use their equivalents. Never fork your conversation history into a child: it starts from this file and your order alone. {modelRule}";
    }

    internal static string TomlString(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            if (c == '\\')
            {
                sb.Append("\\\\");
            }
            else if (c == '"')
            {
                sb.Append("\\\"");
            }
            else if (c <= 0x1f || c == 0x7f)
            {
                sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.Append('"').ToString();
    }

    /// <summary><c>/^\s*model\s*=/</c>.</summary>
    private static bool ConfigModel(string v)
    {
        var i = 0;
        while (i < v.Length && JsText.IsSpace(v[i]))
        {
            i++;
        }

        if (string.CompareOrdinal(v, i, "model", 0, 5) != 0)
        {
            return false;
        }

        i += 5;
        while (i < v.Length && JsText.IsSpace(v[i]))
        {
            i++;
        }

        return i < v.Length && v[i] == '=';
    }

    // ---------------------------------------------------------------- patterns

    [GeneratedRegex("^ {2}(?! )(?:(?! · )[^" + Lt + "])+(?: · (?:(?! · )[^" + Lt + "])+)* · [~/]", RegexOptions.CultureInvariant)]
    private static partial Regex ComposerStatus();

    [GeneratedRegex("^ {2}\\? for shortcuts", RegexOptions.CultureInvariant)]
    private static partial Regex ComposerShortcuts();

    [GeneratedRegex("^[" + Sp + "]*Press [^" + Lt + "]+ to confirm or [^" + Lt + "]+ to cancel(?: or [^" + Lt + "]+ to open thread)?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ApprovalFooter();

    [GeneratedRegex("^• Queued follow-up inputs\\z", RegexOptions.CultureInvariant)]
    private static partial Regex QueuedInputsHeader();

    [GeneratedRegex("^\\? [0-9]+ questions?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex QueuedQuestionCount();

    [GeneratedRegex("^⇧← to answer\\z", RegexOptions.CultureInvariant)]
    private static partial Regex QueuedQuestionOpener();

    [GeneratedRegex("^[" + Sp + "]*Press enter to continue\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ContinueFooter();

    [GeneratedRegex("^[" + Sp + "]*enter continue · esc quit\\z", RegexOptions.CultureInvariant)]
    private static partial Regex EnterQuitFooter();

    [GeneratedRegex("^Yes, proceed \\([^()]+\\)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProceedLabel();

    [GeneratedRegex("^Yes, and don't ask again for commands that start with `[^" + Lt + "]+` \\([^()]+\\)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex DontAskLabel();

    [GeneratedRegex("^No, and tell Codex what to do differently \\([^()]+\\)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex DenyLabel();

    private const string TrustRootText =
        "Codex saves `trust_level = \"trusted\"` for `<trust root>` in `<config path>`. That is the whole repository, not only `<cwd>`. From then on, every Codex session anywhere under `<trust root>`, yours included, skips this question and loads that repository's own Codex config, hooks and exec policies.";

    private const string TrustCwdText =
        "Codex saves `trust_level = \"trusted\"` for `<cwd>` in `<config path>`. From then on, every Codex session anywhere under `<cwd>`, yours included, skips this question and loads that directory's own Codex config, hooks and exec policies.";

    /// <summary>The harnesses by kind, in the order Node's table lists them.</summary>
    internal static IReadOnlyList<KeyValuePair<string, KindHarness>> All { get; } =
    [
        new("codex", new KindHarness
        {
            Label = "Codex",
            ModelArgs = CodexModelArgs,
            InstructionsArgs = CodexInstructionsArgs,
            WritableDirArgs = CodexWritableDirArgs,
            EnvArgs = CodexEnvArgs,
            ArgsSetModel = CodexArgsSetModel,
            NativeLegwork = CodexNativeLegwork,
            ModelsCommand = "codex debug models",
            ApprovalsArgs = ["-c", "approvals_reviewer=\"user\""],
            ApprovalsKey = "approvals_reviewer",
            Composer = new ComposerSpec(["›", "»"], ["Ask Codex to do anything", "Ask a follow-up question"], ComposerStatus(), ComposerShortcuts(), ["  esc again to edit previous message"]),
            Prompts =
            [
                new("approval", new PromptSpec(
                    "Would you like to run the following command?",
                    "›",
                    ApprovalFooter(),
                    "blocked",
                    Onboarding: false,
                    JoinLabels: true,
                    OptionsSpaced: false,
                    [LabelMatcher.Of(ProceedLabel()), LabelMatcher.Of(DontAskLabel()), LabelMatcher.Of(DenyLabel())])),
                new("trust-dialog", new PromptSpec(
                    "Do you trust the contents of this directory",
                    "›",
                    ContinueFooter(),
                    "idle",
                    Onboarding: true,
                    JoinLabels: true,
                    OptionsSpaced: false,
                    [LabelMatcher.Of("Yes, continue"), LabelMatcher.Of("No, quit")])),
                new("trust-folder", new PromptSpec(
                    "Trust this folder?",
                    "›",
                    EnterQuitFooter(),
                    "idle",
                    Onboarding: false,
                    JoinLabels: true,
                    OptionsSpaced: false,
                    [LabelMatcher.Of("Trust and continue"), LabelMatcher.Of("Quit")],
                    OptionsAdjacent: true)),
                new("login", new PromptSpec(
                    "Sign in with ChatGPT",
                    ">",
                    ContinueFooter(),
                    "idle",
                    Onboarding: true,
                    JoinLabels: false,
                    OptionsSpaced: true,
                    [
                        LabelMatcher.Of("Sign in with ChatGPT"),
                        LabelMatcher.Of("Sign in with Device Code"),
                        LabelMatcher.Of("Provide your own API key"),
                        LabelMatcher.Of("Use Amazon Bedrock"),
                    ])),
            ],
            BlockingAnchors = [new("queued-question", [QueuedInputsHeader(), QueuedQuestionCount(), QueuedQuestionOpener()])],
            OtherHeadings =
            [
                "Would you like to make the following edits?",
                "Would you like to grant these permissions?",
                "Do you want to approve network access to",
                "Would you like to send input to",
                "needs your approval.",
            ],
            Options =
            [
                new("approval", new AnswerRow[]
                {
                    new(
                        "approve",
                        "Yes, once",
                        true,
                        ["1"],
                        LabelMatcher.Of(ProceedLabel()),
                        "Codex runs the command shown above outside the member's sandbox, this one time. Its next command that needs approval asks again.",
                        null),
                    new(
                        "deny",
                        "No",
                        false,
                        ["esc"],
                        LabelMatcher.Of("No, and tell Codex what to do differently (esc)"),
                        "Codex does not run the command, and the member's turn ends. It then waits for its next brief.",
                        null),
                }),
                new("trust-dialog", new AnswerRow[]
                {
                    new(
                        "trust",
                        "Yes, trust",
                        true,
                        ["1", "Enter"],
                        LabelMatcher.Of("Yes, continue"),
                        null,
                        ctx => !JsStrictEquals(ctx?.Member("trustRoot"), ctx?.Member("cwd")) ? TrustRootText : TrustCwdText),
                    new(
                        "distrust",
                        "No, quit",
                        false,
                        ["2"],
                        LabelMatcher.Of("No, quit"),
                        "Codex quits and saves nothing. The member doesn't start, and its pane is left at a shell. Nothing is written to `<config path>`, so the next Codex launch in `<cwd>` asks again.",
                        null),
                }),
                new("trust-folder", new AnswerRow[]
                {
                    new(
                        "trust",
                        "Yes, trust",
                        true,
                        ["Enter"],
                        LabelMatcher.Of("Trust and continue"),
                        null,
                        ctx => !JsStrictEquals(ctx?.Member("trustRoot"), ctx?.Member("cwd")) ? TrustRootText : TrustCwdText),
                    new(
                        "distrust",
                        "No, quit",
                        false,
                        ["esc"],
                        LabelMatcher.Of("Quit"),
                        "Codex quits and saves nothing. The member doesn't start, and its pane is left at a shell. Nothing is written to `<config path>`, so the next Codex launch in `<cwd>` asks again.",
                        null),
                }),
                new("login", new AnswerRow[]
                {
                    new(
                        "sign-in",
                        "Start ChatGPT sign-in",
                        false,
                        ["1"],
                        LabelMatcher.Of("Sign in with ChatGPT"),
                        "Codex opens a ChatGPT sign-in in your browser. You finish it there, and the member waits until you do. The sign-in is saved for every Codex session that uses `<CODEX_HOME>`.",
                        null),
                }),
            ],
        }),
    ];

    internal static KindHarness? Get(string kind)
    {
        foreach (var (name, harness) in All)
        {
            if (name == kind)
            {
                return harness;
            }
        }

        return null;
    }

    /// <summary>JavaScript <c>===</c> over two values read from JSON, where a missing member is <c>undefined</c>.</summary>
    internal static bool JsStrictEquals(JsonValue? a, JsonValue? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        var x = a.Value;
        var y = b.Value;
        if (x.Kind != y.Kind)
        {
            return false;
        }

        return x.Kind switch
        {
            JsonKind.Null => true,
            JsonKind.Bool => x.AsBool() == y.AsBool(),
            JsonKind.Number => x.AsNumber() == y.AsNumber(),
            JsonKind.Text => x.AsString() == y.AsString(),
            _ => false,
        };
    }
}
