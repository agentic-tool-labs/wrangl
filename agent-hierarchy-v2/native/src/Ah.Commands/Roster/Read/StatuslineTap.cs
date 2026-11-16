using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Commands;

/// <summary>
/// The statusline tap: <c>doctor --fix --statusline-tap on|off</c>, the report row and the prune.
/// <para>
/// The user's <c>statusLine.command</c> is wrapped in an inline guard that runs a small sh script when it exists and the
/// original command directly when it does not, so removing the script can never break the statusline. The script copies
/// Claude Code's statusline input to a per-session file and runs the original command on the same input.
/// </para>
/// <para>
/// <c>settings.json</c> is edited by replacing the text of the <c>statusLine</c> value only, and the original value's text is
/// kept in the backup, so switching the tap off restores the file byte for byte.
/// </para>
/// </summary>
internal static class StatuslineTap
{
    /// <summary>The guard's presence is recognised by this path inside the command.</summary>
    internal const string Marker = Ah.Launch.SpawnShapes.TapMarker;

    internal const int GuardVersion = 1;

    internal static readonly TimeSpan PruneAge = TimeSpan.FromDays(7);

    /// <summary>
    /// Runs the original command, passed as <c>$1</c>, on the exact input it was given, after copying the input to
    /// <c>$HOME/.claude/ah2/statusline/&lt;session_id&gt;.json</c>. A failure of the copy is never seen by the original command.
    /// </summary>
    internal const string Script = """
        #!/bin/sh
        umask 077
        ORIGINAL=$1
        input=$(cat; printf x)
        input=${input%x}
        sid=$(printf '%s' "$input" | sed -n 's/.*"session_id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1)
        case $sid in
          ''|*[!A-Za-z0-9-]*) sid= ;;
        esac
        [ "${#sid}" -le 128 ] || sid=
        if [ -n "$sid" ]; then
          d="$HOME/.claude/ah2/statusline"
          tmp="$d/.$sid.$$.tmp"
          { mkdir -p "$d" && printf '%s' "$input" > "$tmp" && mv -f "$tmp" "$d/$sid.json"; } 2>/dev/null || rm -f "$tmp" 2>/dev/null || true
        fi
        printf '%s' "$input" 2>/dev/null | /bin/sh -c "$ORIGINAL"
        exit $?
        """ + "\n";

    private static string AhDir(IAhEnvironment env) => NodePath.Join(env.HomeDirectory, ".claude", "ah2");

    internal static string ScriptPath(IAhEnvironment env) => NodePath.Join(AhDir(env), "bin", "statusline-tap.sh");

    internal static string BackupPath(IAhEnvironment env) => NodePath.Join(AhDir(env), "statusline-tap.json");

    internal static string DataDir(IAhEnvironment env) => NodePath.Join(AhDir(env), "statusline");

    /// <summary>The wrapper command: the script when readable, else the original, which is passed as <c>$0</c>.</summary>
    internal static string Guard(string original) => Ah.Launch.SpawnShapes.TapGuard(original);

    internal static bool HasGuard(string? command) => command is not null && command.Contains(Marker, StringComparison.Ordinal);

    private sealed record Settings(string Path, string Text, JsonObject Root);

    /// <summary>The user's settings file with symbolic links followed, or null when there is none; throws <see cref="RosterFailure"/> when it cannot be read as JSON.</summary>
    private static Settings? ReadSettings(RosterContext c)
    {
        var path = LifecyclePermissions.SettingsPath(c.Services.Environment);
        var files = c.Services.Files;
        if (files.Stat(path, followSymlinks: false) is { Kind: FileKind.Symlink })
        {
            try
            {
                path = files.RealPath(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new RosterFailure($"{path} is a link to something that is not a readable file; nothing was changed");
            }
        }

        if (!files.Exists(path))
        {
            return null;
        }

        try
        {
            var text = files.ReadText(path);
            if (!JsonText.TryParse(Encoding.UTF8.GetBytes(text), out var doc) || !doc.IsMap)
            {
                throw new RosterFailure("settings.json could not be read as JSON; nothing was changed");
            }

            return new Settings(path, text, doc.AsObject());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new RosterFailure("settings.json could not be read as JSON; nothing was changed");
        }
    }

    private static string? CommandOf(JsonValue? statusLine) =>
        statusLine is { IsMap: true } s && s.Member("command") is { IsText: true } cmd ? cmd.AsString() : null;

    /// <summary>Applies <c>--statusline-tap on|off</c>; returns the sentence to report.</summary>
    internal static string Apply(RosterContext c, string mode) => mode switch
    {
        "on" => On(c),
        "off" => Off(c),
        _ => throw new RosterFailure("--statusline-tap takes on or off"),
    };

    private static string On(RosterContext c)
    {
        var env = c.Services.Environment;
        var settings = ReadSettings(c);
        var statusLine = settings?.Root.Get("statusLine");
        if (settings is null || statusLine is null)
        {
            return OnWithoutStatusLine(c, settings);
        }

        if (!statusLine.Value.IsMap || statusLine.Value.Member("type") is not { IsText: true } type || type.AsString() != "command"
            || CommandOf(statusLine) is not { Length: > 0 } original)
        {
            throw new RosterFailure("the statusLine is not a command; the tap chains a command statusline");
        }

        if (HasGuard(original))
        {
            return "statusline tap: already on";
        }

        if (JsonSpan.Find(settings.Text, "statusLine") is not { } span)
        {
            throw new RosterFailure("settings.json could not be read as JSON; nothing was changed");
        }

        var wrapped = new JsonObject();
        foreach (var key in statusLine.Value.AsObject().Keys)
        {
            wrapped[key] = key == "command" ? JsonValue.FromString(Guard(original)) : statusLine.Value.AsObject()[key];
        }

        var backup = BackupPath(env);
        EnsureScript(c);
        LevelFiles.WriteAtomically(
            backup,
            JsonText.Stringify(
                new JsonObject
                {
                    ["settings_path"] = settings.Path,
                    ["original"] = statusLine.Value,
                    ["original_text"] = settings.Text[span.Start..span.End],
                    ["installed_at"] = c.Services.Clock.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                    ["guard_version"] = JsonValue.FromNumber(GuardVersion),
                },
                2) + "\n");
        try
        {
            LevelFiles.WriteAtomically(settings.Path, settings.Text[..span.Start] + JsonText.Stringify(wrapped, 0) + settings.Text[span.End..]);
        }
        catch
        {
            File.Delete(backup);
            throw;
        }

        return "statusline tap: on";
    }

    /// <summary>Writes the tap script when it is missing or differs from this build's; the script is ah's own file, not a setting.</summary>
    internal static void EnsureScript(RosterContext c)
    {
        var script = ScriptPath(c.Services.Environment);
        if (c.Services.Files.Exists(script) && c.Services.Files.ReadText(script) == Script)
        {
            return;
        }

        LevelFiles.WriteAtomically(script, Script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>
    /// The tap on a machine with no statusLine: installs one whose command is the guard around an empty command, so the pane stays as it was
    /// and the rate limits are recorded. The inserted text is kept in the backup, so <c>off</c> removes exactly that and the file is
    /// otherwise byte for byte what it was (a file the tap created is deleted again when nothing else was put in it).
    /// </summary>
    private static string OnWithoutStatusLine(RosterContext c, Settings? settings)
    {
        var env = c.Services.Environment;
        var backup = BackupPath(env);
        var line = new JsonObject { ["type"] = "command", ["command"] = JsonValue.FromString(Guard(string.Empty)) };
        var member = "\"statusLine\":" + JsonText.Stringify(line, 0);
        EnsureScript(c);
        string path;
        string text;
        string inserted;
        bool created;
        if (settings is null)
        {
            path = LifecyclePermissions.SettingsPath(env);
            inserted = "{" + member + "}\n";
            text = inserted;
            created = true;
        }
        else
        {
            if (JsonSpan.ObjectStart(settings.Text) is not { } open)
            {
                throw new RosterFailure("settings.json could not be read as JSON; nothing was changed");
            }

            path = settings.Path;
            inserted = member + (settings.Root.Count > 0 ? "," : string.Empty);
            text = settings.Text[..(open + 1)] + inserted + settings.Text[(open + 1)..];
            created = false;
        }

        LevelFiles.WriteAtomically(
            backup,
            JsonText.Stringify(
                new JsonObject
                {
                    ["settings_path"] = path,
                    ["no_statusline"] = true,
                    ["inserted_text"] = inserted,
                    ["settings_created"] = created,
                    ["installed_at"] = c.Services.Clock.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                    ["guard_version"] = JsonValue.FromNumber(GuardVersion),
                },
                2) + "\n");
        try
        {
            LevelFiles.WriteAtomically(path, text);
        }
        catch
        {
            File.Delete(backup);
            throw;
        }

        return "statusline tap: on";
    }

    /// <summary>Puts a settings file back the way it was before <see cref="OnWithoutStatusLine"/>.</summary>
    private static string OffWithoutStatusLine(RosterContext c, Settings settings, JsonValue record)
    {
        var backup = BackupPath(c.Services.Environment);
        var inserted = record.Member("inserted_text") is { IsText: true } t ? t.AsString() : null;
        var at = inserted is null ? -1 : settings.Text.IndexOf(inserted, StringComparison.Ordinal);
        if (inserted is null || at < 0)
        {
            throw new RosterFailure($"the backup {backup} cannot be restored from; nothing was changed");
        }

        var restored = settings.Text.Remove(at, inserted.Length);
        if (record.Member("settings_created") is { IsBool: true } made && made.AsBool() && restored.Trim() is "" or "{}")
        {
            File.Delete(settings.Path);
        }
        else
        {
            LevelFiles.WriteAtomically(settings.Path, restored);
        }

        File.Delete(backup);
        return "statusline tap: off";
    }

    private static string Off(RosterContext c)
    {
        var env = c.Services.Environment;
        var backup = BackupPath(env);
        var settings = ReadSettings(c);
        var current = CommandOf(settings?.Root.Get("statusLine"));
        if (!c.Services.Files.Exists(backup))
        {
            return HasGuard(current)
                ? throw new RosterFailure($"the tap is on but its backup {backup} is missing; nothing was restored")
                : "statusline tap: already off";
        }

        if (settings is null || !HasGuard(current))
        {
            throw new RosterFailure($"statusLine was changed since the tap was installed; restored nothing; the backup is kept at {backup}");
        }

        var record = JsonText.Parse(Encoding.UTF8.GetBytes(c.Services.Files.ReadText(backup)));
        if (record.Member("no_statusline") is { IsBool: true } none && none.AsBool())
        {
            return OffWithoutStatusLine(c, settings, record);
        }

        var restored = record.Member("original_text") is { IsText: true } raw
            ? raw.AsString()
            : record.Member("original") is { IsMap: true } original ? JsonText.Stringify(original, 0) : null;
        if (restored is null || JsonSpan.Find(settings.Text, "statusLine") is not { } span)
        {
            throw new RosterFailure($"the backup {backup} cannot be restored from; nothing was changed");
        }

        LevelFiles.WriteAtomically(settings.Path, settings.Text[..span.Start] + restored + settings.Text[span.End..]);
        File.Delete(backup);
        return "statusline tap: off";
    }

    /// <summary>Deletes the files in the tap directory that were last written more than <see cref="PruneAge"/> ago; returns how many.</summary>
    internal static int Prune(RosterContext c)
    {
        var dir = DataDir(c.Services.Environment);
        if (!Directory.Exists(dir))
        {
            return 0;
        }

        var cutoff = c.Services.Clock.UtcNow - PruneAge;
        var removed = 0;
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles())
        {
            if (file.LinkTarget is null && file.LastWriteTimeUtc < cutoff.UtcDateTime)
            {
                try
                {
                    file.Delete();
                    removed++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // a file that cannot be removed is left for the next run
                }
            }
        }

        return removed;
    }

    /// <summary>The one report line: <c>statusline tap: on|off|broken (why)</c>, and whether it is a problem.</summary>
    internal static DoctorVerb.Check Row(RosterContext c)
    {
        var env = c.Services.Environment;
        Settings? settings;
        try
        {
            settings = ReadSettings(c);
        }
        catch (RosterFailure)
        {
            settings = null;
        }

        var guard = HasGuard(CommandOf(settings?.Root.Get("statusLine")));
        var backup = c.Services.Files.Exists(BackupPath(env));
        if (!guard)
        {
            return backup
                ? Broken("tap not installed: statusLine changed; run --statusline-tap on again")
                : new DoctorVerb.Check("ok", "statusline tap: off");
        }

        if (!c.Services.Files.Exists(ScriptPath(env)))
        {
            return Broken($"the guard is present but {ScriptPath(env)} is missing; the statusline still works through the guard");
        }

        foreach (var name in new[] { "settings.local.json", "settings.json" })
        {
            var file = NodePath.Join(c.Cwd, ".claude", name);
            if (OverridesStatusLine(c, file))
            {
                return Broken($"tap bypassed in this project by {file}");
            }
        }

        return new DoctorVerb.Check("ok", "statusline tap: on");

        static DoctorVerb.Check Broken(string why) => new("warn", $"statusline tap: broken ({why})");
    }

    private static bool OverridesStatusLine(RosterContext c, string file)
    {
        try
        {
            return c.Services.Files.Exists(file)
                && JsonText.TryParse(Encoding.UTF8.GetBytes(c.Services.Files.ReadText(file)), out var doc)
                && doc.IsMap && doc.AsObject().ContainsKey("statusLine");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Finds the text of one top-level member's value in a JSON document without parsing it into anything that loses its formatting.</summary>
internal static class JsonSpan
{
    internal readonly record struct Range(int Start, int End);

    /// <summary>The span of the value of top-level member <paramref name="key"/>; null when it is missing, given twice, or the text is not an object.</summary>
    internal static Range? Find(string text, string key)
    {
        var i = SkipSpace(text, 0);
        if (i >= text.Length || text[i] != '{')
        {
            return null;
        }

        Range? found = null;
        i = SkipSpace(text, i + 1);
        while (i < text.Length && text[i] != '}')
        {
            var keyEnd = SkipString(text, i);
            if (keyEnd < 0)
            {
                return null;
            }

            var name = text[(i + 1)..(keyEnd - 1)];
            i = SkipSpace(text, keyEnd);
            if (i >= text.Length || text[i] != ':')
            {
                return null;
            }

            var start = SkipSpace(text, i + 1);
            var end = SkipValue(text, start);
            if (end < 0)
            {
                return null;
            }

            if (name == key)
            {
                if (found is not null)
                {
                    return null;
                }

                found = new Range(start, end);
            }

            i = SkipSpace(text, end);
            if (i < text.Length && text[i] == ',')
            {
                i = SkipSpace(text, i + 1);
            }
        }

        return found;
    }

    /// <summary>The index of the <c>{</c> that opens the top-level object, or null when the text is not an object.</summary>
    internal static int? ObjectStart(string text)
    {
        var i = SkipSpace(text, 0);
        return i < text.Length && text[i] == '{' ? i : null;
    }

    private static int SkipSpace(string text, int i)
    {
        while (i < text.Length && text[i] is ' ' or '\t' or '\r' or '\n')
        {
            i++;
        }

        return i;
    }

    /// <summary>The index after the string that starts at <paramref name="i"/>, or -1.</summary>
    private static int SkipString(string text, int i)
    {
        if (i >= text.Length || text[i] != '"')
        {
            return -1;
        }

        for (i++; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == '"')
            {
                return i + 1;
            }
        }

        return -1;
    }

    /// <summary>The index after the value that starts at <paramref name="i"/>, or -1.</summary>
    private static int SkipValue(string text, int i)
    {
        if (i >= text.Length)
        {
            return -1;
        }

        if (text[i] == '"')
        {
            return SkipString(text, i);
        }

        if (text[i] is '{' or '[')
        {
            var depth = 0;
            while (i < text.Length)
            {
                if (text[i] == '"')
                {
                    i = SkipString(text, i);
                    if (i < 0)
                    {
                        return -1;
                    }

                    continue;
                }

                if (text[i] is '{' or '[')
                {
                    depth++;
                }
                else if (text[i] is '}' or ']' && --depth == 0)
                {
                    return i + 1;
                }

                i++;
            }

            return -1;
        }

        var start = i;
        while (i < text.Length && text[i] is not (',' or '}' or ']' or ' ' or '\t' or '\r' or '\n'))
        {
            i++;
        }

        return i > start ? i : -1;
    }
}
