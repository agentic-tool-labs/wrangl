using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;

namespace Ah.Commands;

/// <summary><c>roster testing</c>: show the resolved durable testing orders, or set one role's order in a level file.</summary>
internal static class TestingVerb
{
    [Verb("roster:testing")]
    internal static int Testing(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, Run);

    private static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (key is not ("cwd" or "level"))
            {
                throw new RosterFailure($"testing: unrecognized flag --{key} (use --level, --cwd)");
            }
        }

        var words = c.Opts.Positionals;
        if (words is ["show"])
        {
            var orders = ITestingOrders.Resolve(c.Services.Config.ResolveConfig(c.Cwd));
            var modes = new JsonObject();
            foreach (var key in orders.Keys)
            {
                modes[key] = ITestingOrders.Mode(orders.Get(key)!.Value.AsString()) is { } mode ? mode : JsonValue.Null;
            }

            c.Out(JsonValue.FromObject(new JsonObject { [ITestingOrders.Key] = orders, ["modes"] = modes }));
            return 0;
        }

        if (words.Count < 3 || words[0] != "set" || string.Join(' ', words.Skip(2)).Trim() is not { Length: > 0 } text)
        {
            throw new RosterFailure("testing: use `testing show` or `testing set <role> <text> [--level repo|global|repo-user]`");
        }

        if (words[1] != "e4" && c.Services.Config.ResolveConfig(c.Cwd).Get("roles")?.Member(words[1]) is null)
        {
            c.Warn($"testing set: {LevelFiles.Quote(words[1])} is neither a registry role nor e4, so no member reads this order");
        }

        var files = new LevelFiles(c);
        var level = files.RequireLevel(c.Opts.Text("level") ?? "repo");
        var path = files.PathOf(level);
        var data = files.Read(path);
        var block = data.Get(ITestingOrders.Key) is { IsMap: true } m ? m.AsObject() : new JsonObject();
        block[words[1]] = text;
        data[ITestingOrders.Key] = block;
        files.Write(path, data);
        c.Out(JsonValue.FromObject(new JsonObject
        {
            ["path"] = path,
            ["level"] = level,
            ["role"] = words[1],
            ["text"] = text,
            ["mode"] = ITestingOrders.Mode(text) is { } set ? set : JsonValue.Null,
        }));
        return 0;
    }
}
