using System.Text.RegularExpressions;

namespace Ah.Watch.Model;

/// <summary>
/// The one place that says who a message is for, served as the <c>class</c> field of an exchange slice and of a transcript entry: <see cref="Work"/>
/// (briefs, responses, reports, the user's own conversation), <see cref="Internal"/> (traffic between agents and the machinery) or
/// <see cref="NeedsYou"/> (shown whatever the viewer's filter says). A client hides only <see cref="Internal"/>, so anything not recognised here shows.
/// </summary>
internal static partial class MessageClass
{
    public const string Work = "work";

    public const string Internal = "internal";

    public const string NeedsYou = "needs_you";

    /// <summary>The slugs of exchanges no person asked for: the run anchor, a release check and a restart checkpoint. Matched whole: <c>release-notes</c> is work.</summary>
    private static readonly HashSet<string> InternalSlugs = new(StringComparer.Ordinal) { "pipeline-run-anchor", "release", "checkpoint" };

    /// <summary>The leading text of a transcript user entry that the machinery wrote, not a person or a dispatching agent.</summary>
    private static readonly string[] InternalPrefixes =
    [
        "[Cross-session idle notice]",
        "[Cross-session delivery notice]",
        "<task-notification>",
        "<system-reminder>",
        "<local-command-caveat>",
        "<local-command-stdout>",
        "Base directory for this skill:",
        "This session is being continued from a previous conversation",
        "Stop hook feedback:",
        "[ah-nudge]",
    ];

    /// <summary>A notification's result with no result in it: the wording seen in real transcripts.</summary>
    private static readonly string[] PointerResults = ["<result>This agent's report was delivered to you as a message", "<result>This agent has not reported yet"];

    /// <summary>The mail events known to be machinery, by whole name. Any other event shows, so a new one can't vanish silently.</summary>
    private static readonly HashSet<string> NoiseEvents = new(StringComparer.Ordinal)
    {
        "resume", "relay-needed", "job-overdue", "machine-overloaded", "machine-load-ok", "check-in-due", "member-bare", "member-bare-asked", "member-bare-busy",
        "member-resume", "outage", "team-readopt", "peer-silent", "unread-at-idle", "report-owed-advisory", "idle-capacity",
    };

    /// <summary>The mail events a person may have to act on: the ones the doorbell ranks 1 and 2 (<c>MailDoorbell.RankOfEvent</c>).</summary>
    private static readonly HashSet<string> NeedsYouEvents = new(StringComparer.Ordinal)
    {
        "user-ask", "member-blocked", "member-stuck", "member-stuck-escalated", "step-needs-user", "classifier-denied", "mail-unacked", "delivery-failed", "api-failed", "member-down",
    };

    private static readonly string[] MailWrappers = ["Another Claude session sent a message:", "The wrangl plugin sent a message:", "The ah2 plugin sent a message:"];

    /// <summary>The class of an exchange from its slug. A link note's slug is <c>ln-</c> and the id of its mail entry.</summary>
    public static string OfExchange(string? slug) =>
        slug is not null && (InternalSlugs.Contains(slug) || LinkNoteSlug().IsMatch(slug)) ? Internal : Work;

    /// <summary>The class of a transcript user entry from its first text.</summary>
    public static string OfUserText(string text)
    {
        // A background agent's notification that carries its result is work, not traffic; one that only points at its report is traffic.
        if (InternalPrefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal)))
        {
            return text.StartsWith("<task-notification>", StringComparison.Ordinal) && text.Contains("<result>", StringComparison.Ordinal)
                && !PointerResults.Any(p => text.Contains(p, StringComparison.Ordinal)) ? Work : Internal;
        }

        var wrapped = MailWrappers.Any(w => text.StartsWith(w, StringComparison.Ordinal));
        if (!wrapped && !text.StartsWith("ah mail ", StringComparison.Ordinal))
        {
            return Work;
        }

        if (MailLine().Match(text) is { Success: true } line)
        {
            return OfMail(line.Groups[1].Value, text[line.Groups[2].Index..]);
        }

        return MailDigest().Match(text) is { Success: true } digest ? OfDigest(text[(digest.Index + digest.Length)..]) : Work;
    }

    private static string OfMail(string kind, string body) => kind switch
    {
        // A link note has its envelope line first, so the question is looked for anywhere: a wrong show costs a line, a wrong hide loses a question.
        "note" => body.Contains("QUESTION:", StringComparison.Ordinal) ? NeedsYou : Internal,
        "event" => OfEvent(body),
        _ => Work,
    };

    /// <summary>The class of a mail event from its name, the first word of what it says. A relayed event says its text, not its name: that name is unknown, so it shows.</summary>
    private static string OfEvent(string body)
    {
        var name = body.Split([' ', '\n'], 2)[0].TrimEnd(':');
        return NoiseEvents.Contains(name) ? Internal : NeedsYouEvents.Contains(name) ? NeedsYou : Work;
    }

    /// <summary>A digest of mail, one <c>- kind …</c> line each: it takes the class of its most important line, and a line not recognised shows.</summary>
    private static string OfDigest(string lines)
    {
        var classes = lines.Split('\n').Where(static l => l.StartsWith("- ", StringComparison.Ordinal)).Select(OfDigestLine).ToList();
        return classes.Contains(NeedsYou) ? NeedsYou : classes.Count > 0 && classes.All(static c => c == Internal) ? Internal : Work;
    }

    private static string OfDigestLine(string line)
    {
        const string Event = "- event ";
        if (line.StartsWith(Event, StringComparison.Ordinal))
        {
            return OfEvent(line[Event.Length..]);
        }

        // A compact note has no text to read, so it could be a question: it shows.
        var note = NoteLine().Match(line);
        return note.Success ? OfMail("note", note.Groups[1].Value) : Work;
    }

    [GeneratedRegex(@"\A- note from \S+: (.*)\z", RegexOptions.CultureInvariant)]
    private static partial Regex NoteLine();

    [GeneratedRegex(@"\Aln-[0-9]{8}-", RegexOptions.CultureInvariant)]
    private static partial Regex LinkNoteSlug();

    [GeneratedRegex(@"^ah mail from \S+ to \S+ \(([a-z]+)\): (.*)$", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex MailLine();

    [GeneratedRegex(@"^ah mail for \S+ \([0-9]+\):$", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex MailDigest();
}
