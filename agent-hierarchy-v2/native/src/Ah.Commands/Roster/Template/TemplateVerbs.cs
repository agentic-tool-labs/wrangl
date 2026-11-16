using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>The roster template verbs that need no live team: <c>init</c>, <c>remove</c> and the two that only refuse.</summary>
internal static class TemplateVerbs
{
    [Verb("roster:layout")]
    internal static int Layout(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, static _ => throw new RosterFailure(
            "layout was removed: a team's layout is chosen when it is created — `ah roster create --mode <auto|columns|grid>`. An explicit --mode also becomes the default for future teams."));

    [Verb("roster:alias")]
    internal static int Alias(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, static _ => throw new RosterFailure(
            "alias was removed: a team's name is chosen when it is created — `ah roster create --team <name>`. The name belongs to that team and is not stored anywhere else."));

    [Verb("roster:init")]
    internal static int Init(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, InitRun);

    [Verb("roster:remove")]
    internal static int Remove(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, RemoveRun);

    private static int InitRun(RosterContext c)
    {
        var files = new LevelFiles(c);
        if (c.Opts.Text("roster") == c.Services.Config.DefaultRoster)
        {
            throw new RosterFailure(
                "init --roster default: `default` means the unnamed `roster` block, so it can't be a roster name — run init without --roster for the default block, or pick another name");
        }

        var given = files.LevelArg() ?? throw new RosterFailure("init needs --level global|repo|repo-user (or the level as the first word)");
        var level = files.RequireLevel(given);
        var route = c.Opts.Text("route");
        var valid = route is not null && Ah.Roster.RosterModule.RouteValues.Contains(route);
        if (!valid)
        {
            var shown = c.Opts.Has("route") ? (route is null ? "true" : LevelFiles.Quote(route)) : "undefined";
            throw new RosterFailure($"--route must be \"peer\" or \"subagent\", got {shown}");
        }

        if (route == "subagent")
        {
            throw new RosterFailure(
                "init: --route subagent is not allowed — only legwork roles run as subagents. Init with --route peer, then route individual legwork members with `add --role <legwork role> --route subagent`.");
        }

        var path = files.PathOf(level);
        var data = files.Read(path);
        if (c.Opts.Has("layout"))
        {
            throw new RosterFailure(
                "init: --layout was removed — a team's layout is chosen when it is created: `ah roster create --mode <auto|columns|grid>` (an explicit --mode also becomes the default for future teams)");
        }

        var fresh = LevelFiles.FreshBlock(route);
        files.Install(data, c.RosterArg, fresh);
        files.Write(path, data);
        c.Out(JsonValue.FromObject(new JsonObject
        {
            ["level"] = level,
            ["path"] = path,
            ["container"] = LevelFiles.ContainerLabel(c.RosterArg),
            ["roster"] = fresh,
        }));
        return 0;
    }

    private static int RemoveRun(RosterContext c)
    {
        var files = new LevelFiles(c);
        var member = c.Opts.Text("member") ?? throw new RosterFailure("remove needs --member <derived-name>");
        var result = files.RemoveConfigMember(member, c.RosterArg);
        if (!result.Removed)
        {
            throw new RosterFailure(
                result.Reason == "no such member" ? $"no member named {LevelFiles.Quote(member)} at level \"{result.Level}\"" : result.Reason ?? "not removed");
        }

        if (result.WasDefaulted)
        {
            c.Stderr.Write($"ah roster: no --level given — removed from the currently-resolving level \"{result.Level}\" ({result.Path})\n");
        }

        c.Out(JsonValue.FromObject(new JsonObject
        {
            ["level"] = result.Level!,
            ["path"] = result.Path!,
            ["wasDefaulted"] = result.WasDefaulted,
            ["removed"] = member,
            ["container"] = result.Container!,
            ["store"] = $"{result.Container} config at \"{result.Level}\" ({result.Path})",
        }));
        return 0;
    }
}
