using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Roster;

namespace Ah.Commands;

/// <summary>
/// <c>codex-trust [&lt;dir&gt;] [--yes]</c>: the one writer of Codex's trust for a directory. Without <c>--yes</c> it prints the plan and
/// writes nothing. With it, it backs the config up, edits only the table of the directory's trust root, writes the file atomically,
/// re-reads the result through the same reader a launch uses, and puts the backup back when that does not say trusted. A member
/// session is refused, so only the user or an Orchestrator can grant trust.
/// </summary>
internal static class CodexTrustVerb
{
    private const string BackupSuffix = ".ah2-bak";

    [Verb("roster:codex-trust")]
    internal static int CodexTrust(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, Run);

    private static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (key is not ("yes" or "json" or "cwd" or "team" or "preapprove" or "parent" or "via" or "revoke" or "revoke-all" or "list"))
            {
                throw new RosterFailure(
                    $"codex-trust: unrecognized flag --{key} (use [<dir>] [--yes] [--json] [--cwd], --preapprove <dir> [--parent] [--via install|bootstrap|cli], --revoke <dir>, --revoke-all, --list)");
            }
        }

        var modes = new[] { c.Opts.Has("preapprove"), c.Opts.Has("revoke"), c.Opts.IsTrue("revoke-all"), c.Opts.IsTrue("list") }.Count(m => m);
        if (modes > 1)
        {
            throw new RosterFailure("codex-trust: --preapprove, --revoke, --revoke-all and --list are mutually exclusive");
        }

        if ((c.Opts.IsTrue("parent") && !c.Opts.Has("preapprove")) || (c.Opts.Has("via") && !c.Opts.Has("preapprove")))
        {
            throw new RosterFailure("codex-trust: --parent and --via belong to --preapprove");
        }

        if (c.Opts.Positionals.Count > 1)
        {
            throw new RosterFailure("codex-trust takes at most one directory");
        }

        var proved = c.Proved();
        if (proved.Marked || proved.Nearest.Kind == NearestKind.Member)
        {
            const string Message = "codex-trust: a member session may not change Codex's trust; ask the Orchestrator or run it from your own terminal — nothing was changed";
            throw c.Refuse(new JsonObject { ["refused"] = "not-from-member", ["message"] = Message }, Message);
        }

        var dir = c.Opts.Positionals.Count == 1 ? Path.GetFullPath(c.Opts.Positionals[0], c.Cwd) : c.Cwd;
        var services = c.Services;
        var trust = services.CodexTrust ?? RosterModule.CreateCodexTrust(services.Files, services.Environment, cwd => services.Config.CheckoutRoot(cwd));
        if (c.Opts.Has("preapprove"))
        {
            return Preapprove(c, trust, c.Opts.Text("preapprove") is { Length: > 0 } given ? Path.GetFullPath(given, c.Cwd) : dir);
        }

        if (c.Opts.Has("revoke"))
        {
            return Revoke(c, trust, c.Opts.Text("revoke") is { Length: > 0 } victim ? Path.GetFullPath(victim, c.Cwd) : dir);
        }

        if (c.Opts.IsTrue("revoke-all"))
        {
            return RevokeAll(c);
        }

        if (c.Opts.IsTrue("list"))
        {
            return List(c, trust, dir);
        }

        var plan = trust.PlanTrust(dir);
        var state = MemberPlanning.Text(plan, "state") ?? string.Empty;
        var configPath = MemberPlanning.Text(plan, "configPath") ?? string.Empty;
        var newText = MemberPlanning.Text(plan, "newText");
        var backup = configPath + BackupSuffix;

        var output = new JsonObject
        {
            ["config"] = configPath,
            ["key"] = plan.Get("key") ?? JsonValue.Null,
            ["state"] = state,
            ["edit"] = plan.Get("edit") ?? JsonValue.Null,
            ["backup"] = state == "absent" ? JsonValue.Null : backup,
        };

        if (state == "unreadable")
        {
            var message = $"codex-trust: {MemberPlanning.Text(plan, "why")} — nothing was written";
            throw c.Refuse(new JsonObject { ["refused"] = "codex-config-unreadable", ["config"] = configPath, ["message"] = message }, message);
        }

        if (newText is null)
        {
            output["written"] = false;
            output["result"] = "unchanged";
            c.Out(output);
            return 0;
        }

        if (!c.Opts.IsTrue("yes"))
        {
            output["written"] = false;
            output["result"] = "plan";
            output["next"] = "re-run with --yes to make this edit";
            c.Out(output);
            return 0;
        }

        Apply(c, trust, dir, configPath, backup, state == "absent", newText, MemberPlanning.Text(plan, "sourceSha256"));
        output["written"] = true;
        output["result"] = "trusted";
        c.Out(output);
        return 0;
    }

    private static string Real(RosterContext c, string path)
    {
        try
        {
            return c.Services.Files.RealPath(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    private static int Preapprove(RosterContext c, ICodexTrust trust, string dir)
    {
        var via = c.Opts.Text("via") ?? "cli";
        if (!CodexPreapproval.Vias.Contains(via))
        {
            throw new RosterFailure($"codex-trust: --via must be one of {string.Join(", ", CodexPreapproval.Vias)}, got \"{via}\"");
        }

        var parent = c.Opts.IsTrue("parent");
        var path = parent ? Real(c, dir) : MemberPlanning.Text(trust.PlanTrust(dir), "key") ?? Real(c, dir);
        var kind = parent ? "parent" : "root";
        if (parent && path == "/")
        {
            throw new RosterFailure("codex-trust: --parent of / would pre-approve every repository on this machine; name a narrower folder");
        }

        var scopes = CodexPreapproval.Read(c);
        var store = CodexPreapproval.StorePath(c.Services.Environment);
        var output = new JsonObject { ["store"] = store, ["scope"] = new JsonObject { ["path"] = path, ["kind"] = kind, ["via"] = via } };
        if (scopes.Any(s => MemberPlanning.Text(s, "path") == path && MemberPlanning.Text(s, "kind") == kind))
        {
            output["written"] = false;
            output["result"] = "unchanged";
            c.Out(output);
            return 0;
        }

        if (parent && path == Real(c, c.Services.Environment.HomeDirectory))
        {
            output["warnings"] = new JsonArray { (JsonValue)"this pre-approves your whole home folder: every repository under it will have its Codex trust screen answered without asking" };
        }

        output["written"] = false;
        output["result"] = "plan";
        output["next"] = "re-run with --yes to record this pre-approval";
        if (c.Opts.IsTrue("yes"))
        {
            scopes.Add(new JsonObject
            {
                ["path"] = path,
                ["kind"] = kind,
                ["granted_at"] = c.Services.Clock.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
                ["via"] = via,
            });
            CodexPreapproval.Write(c, scopes);
            output["written"] = true;
            output["result"] = "preapproved";
            output.Remove("next");
        }

        c.Out(output);
        return 0;
    }

    private static int Revoke(RosterContext c, ICodexTrust trust, string dir)
    {
        var real = Real(c, dir);
        var key = MemberPlanning.Text(trust.PlanTrust(dir), "key");
        var scopes = CodexPreapproval.Read(c);
        var kept = scopes.Where(s => MemberPlanning.Text(s, "path") is not { } p || (p != dir && p != real && p != key)).ToList();
        var output = new JsonObject { ["store"] = CodexPreapproval.StorePath(c.Services.Environment), ["removed"] = scopes.Count - kept.Count };
        if (kept.Count != scopes.Count)
        {
            CodexPreapproval.Write(c, kept);
        }

        output["result"] = kept.Count == scopes.Count ? "unchanged" : "revoked";
        c.Out(output);
        return 0;
    }

    private static int RevokeAll(RosterContext c)
    {
        var store = CodexPreapproval.StorePath(c.Services.Environment);
        var count = CodexPreapproval.Read(c).Count;
        var output = new JsonObject { ["store"] = store, ["scopes"] = count, ["written"] = false, ["result"] = count == 0 ? "unchanged" : "plan" };
        if (count > 0 && !c.Opts.IsTrue("yes"))
        {
            output["next"] = "re-run with --yes to remove every pre-approval";
        }
        else if (count > 0)
        {
            CodexPreapproval.Write(c, null);
            output["written"] = true;
            output["result"] = "revoked";
        }

        c.Out(output);
        return 0;
    }

    private static int List(RosterContext c, ICodexTrust trust, string dir)
    {
        var env = c.Services.Environment;
        var scopes = new JsonArray();
        foreach (var scope in CodexPreapproval.Read(c))
        {
            scopes.Add(scope);
        }

        var tail = new JsonArray();
        var log = CodexPreapproval.AutoAnswerLogPath(env);
        if (File.Exists(log))
        {
            foreach (var line in File.ReadLines(log).TakeLast(5))
            {
                tail.Add(line);
            }
        }

        c.Out(new JsonObject
        {
            ["trust"] = trust.Check(dir),
            ["store"] = CodexPreapproval.StorePath(env),
            ["preapprovals"] = scopes,
            ["autoanswer"] = new JsonObject
            {
                ["log"] = log,
                ["last_7_days"] = CodexPreapproval.AutoAnswersSince(env, c.Services.Clock.UtcNow, 7),
                ["tail"] = tail,
            },
        });
        return 0;
    }

    private static void Apply(RosterContext c, ICodexTrust trust, string dir, string configPath, string backup, bool created, string newText, string? plannedFrom)
    {
        if (!created)
        {
            var current = File.ReadAllBytes(configPath);
            if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(current)) != plannedFrom)
            {
                const string Raced = "codex-trust: the config changed after it was planned (another process edited it), so nothing was written — run it again to plan from the new file";
                throw c.Refuse(new JsonObject { ["refused"] = "codex-config-changed", ["config"] = configPath, ["message"] = Raced }, Raced);
            }

            File.Copy(configPath, backup, overwrite: true);
        }

        try
        {
            if (created)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                if (!LevelFiles.WriteNew(configPath, newText))
                {
                    throw new IOException($"{configPath} appeared while the edit was being prepared");
                }

                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            else
            {
                LevelFiles.WriteAtomically(configPath, newText);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new RosterFailure($"codex-trust: {configPath} could not be written ({e.Message}) — it is as it was");
        }

        if (MemberPlanning.Text(trust.Check(dir), "verdict") == "trusted")
        {
            return;
        }

        if (created)
        {
            File.Delete(configPath);
        }
        else
        {
            LevelFiles.WriteAtomically(configPath, File.ReadAllBytes(backup));
        }

        const string Failed = "codex-trust: the edited config does not read as trusted, so it was put back as it was";
        throw c.Refuse(new JsonObject { ["refused"] = "codex-trust-verify-failed", ["config"] = configPath, ["message"] = Failed }, Failed);
    }
}
