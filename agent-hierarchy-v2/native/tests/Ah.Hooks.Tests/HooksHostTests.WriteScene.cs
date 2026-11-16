using System.Text.Json;
using Ah.Abstractions;
using Ah.Core.Text;
using Ah.Hierarchy;
using Ah.Mesh;
using Ah.TestKit;

namespace Ah.Hooks.Tests;

/// <summary>
/// The shared scene of the hook tests that run over a real sandbox: a repository with two linked work trees, one team, one grants
/// document and a session that is a member (by its marker), the Orchestrator (by its process) or nobody. Kept from the worktree write
/// gate's tests (deleted in ah-simplify S2) for the tests that still use it.
/// </summary>
public sealed partial class HooksHostTests
{
    private const string WriteGate = "pretooluse-worktree-write-gate";

    internal sealed class CountingTable(IReadOnlyDictionary<int, string?>? starts = null) : IProcessTable
    {
        public int AncestorCalls { get; private set; }

        public int StartCalls { get; private set; }

        public IReadOnlyList<int>? Ancestors(int pid)
        {
            AncestorCalls++;
            return [];
        }

        public bool? HasAncestor(int pid, int candidate)
        {
            AncestorCalls++;
            return false;
        }

        public string? StartTime(long pid)
        {
            StartCalls++;
            return starts is not null && starts.TryGetValue((int)pid, out var s) ? s : null;
        }
    }

    internal sealed class FakeSessions(IProcessTable table, params TeamOrchestrator[] teams) : IGateSessions
    {
        public IProcessTable Table { get; } = table;

        public int TeamReads { get; private set; }

        public IReadOnlyList<TeamOrchestrator> Teams(string hierarchyDir)
        {
            TeamReads++;
            return teams;
        }

        public IReadOnlyList<RegisteredSession> Registered() => [];
    }

    /// <summary>One repository, its work trees, the team's grants document and the rig the gate runs in.</summary>
    private sealed class WriteScene
    {
        public WriteScene(
            string mode = "enforce",
            string role = "implementor",
            bool marker = true,
            string? extraJson = null,
            string? tmpRoot = null,
            Dictionary<string, string>? env = null,
            bool document = true,
            bool nested = false,
            Dictionary<string, string>? variables = null,
            string? alias = null,
            MsgsRoute? msgsRoute = null)
        {
            var seed = new Rig();
            var root = Canon(seed.Sandbox.Root, seed);
            Repo = root + "/repo";
            Wt1 = nested ? Repo + "/.claude/worktrees/wt1" : root + "/wt1";
            Wt2 = nested ? Repo + "/.claude/worktrees/wt2" : root + "/wt2";
            Wt10 = Wt1 + "0";
            foreach (var d in new[] { Repo + "/.git/worktrees/wt1", Repo + "/.git/worktrees/wt2", Repo + "/.git/worktrees/wt10", Wt1, Wt2, Wt10 })
            {
                Directory.CreateDirectory(d);
            }

            File.WriteAllText(Repo + "/.git/worktrees/wt1/gitdir", Wt1 + "/.git\n");
            File.WriteAllText(Repo + "/.git/worktrees/wt2/gitdir", Wt2 + "/.git\n");
            File.WriteAllText(Repo + "/.git/worktrees/wt10/gitdir", Wt10 + "/.git\n");
            File.WriteAllText(Wt10 + "/.git", "gitdir: " + Repo + "/.git/worktrees/wt10\n");
            File.WriteAllText(Wt1 + "/.git", "gitdir: " + Repo + "/.git/worktrees/wt1\n");
            File.WriteAllText(Wt2 + "/.git", "gitdir: " + Repo + "/.git/worktrees/wt2\n");

            Rig = new Rig(env, msgsRoute: msgsRoute);
            Dir = Rig.Services.Roster.TeamHome(Rig.Services.Config.HierarchyDir(Wt1));
            var launchEnvironment = new Dictionary<string, string>
            {
                ["HOME"] = Rig.Sandbox.Home,
                ["AH_HOME"] = Rig.Sandbox.Root + "/ah",
                ["AH_TEST"] = "1",
                ["WRANGL_TEAM_FILE"] = Dir + "/teams/t.json",
            };
            if (marker)
            {
                launchEnvironment[MeshModule.MemberMarkerEnv] = "m1";
            }

            foreach (var (key, value) in variables ?? [])
            {
                launchEnvironment[key] = value;
            }

            Environment = new FakeEnvironment(launchEnvironment, new PluginInfo("/ahroot", "wrangl", "0.0.0", "1"));
            Directory.CreateDirectory(Dir + "/msgs");
            Directory.CreateDirectory(Dir + "/specs");
            Directory.CreateDirectory(Dir + "/teams");
            Role = role;
            WriteTeam(("m1", role));
            if (document)
            {
                WriteDocument(mode, role, extraJson, tmpRoot, alias: alias);
            }
        }

        public Rig Rig { get; }

        public FakeEnvironment Environment { get; }

        public string Repo { get; }

        public string Wt1 { get; }

        public string Wt2 { get; }

        public string Wt10 { get; }

        public string Dir { get; }

        public string Role { get; }

        public string DocumentFile => Dir + "/grants/t.json";

        public string AuditFile => Rig.Sandbox.Home + "/.claude/ah2/agent-hierarchy.worktree-audit.jsonl";

        private static string Canon(string path, Rig rig) => CanonicalPath.Resolve(rig.Services.Files, path) ?? path;

        public void WriteTeam(params (string Name, string Role)[] members) =>
            File.WriteAllText(
                Dir + "/teams/t.json",
                "{\"team_id\":\"t\",\"members\":[" + string.Join(',', members.Select(m => $"{{\"name\":{Q(m.Name)},\"role\":{Q(m.Role)}}}")) + "]}");

        public void WriteDocument(string mode, string role, string? extraJson = null, string? tmpRoot = null, string? workRoot = null, string? alias = null)
        {
            Directory.CreateDirectory(Dir + "/grants");
            var tmp = tmpRoot is null ? "null" : $"{{\"root\":{Q(tmpRoot)},\"alias\":{Q(alias ?? tmpRoot + "-alias")}}}";
            File.WriteAllText(
                DocumentFile,
                $"{{\"version\":1,\"team\":\"t\",\"mode\":\"{mode}\",\"audit_hash\":null,\"members\":{{\"m1\":{{\"work_root\":{Q(workRoot ?? Wt1)},\"stream\":null,\"role\":\"{role}\",\"extra\":[{extraJson ?? string.Empty}],\"tmp\":{tmp}}}}}}}");
        }

        public string Event(string tool, string path, string? agentType = null, string key = "file_path", string? cwd = null, Dictionary<string, object?>? input_extra = null)
        {
            var input = new Dictionary<string, object?> { [key] = path };
            foreach (var pair in input_extra ?? [])
            {
                input[pair.Key] = pair.Value;
            }

            var evt = new Dictionary<string, object?>
            {
                ["hook_event_name"] = "PreToolUse",
                ["tool_name"] = tool,
                ["tool_input"] = input,
                ["cwd"] = cwd ?? Wt1,
                ["session_id"] = "s-1",
            };
            if (agentType is not null)
            {
                evt["agent_type"] = agentType;
                evt["agent_id"] = "a-1";
            }

            return JsonSerializer.Serialize(evt);
        }

        public HookOutcome Write(string path, string tool = "Write", string? agentType = null, Func<GateServices>? services = null) =>
            Rig.Run(WriteGate, Event(tool, path, agentType), services: services ?? (() => With(new FakeSessions(new CountingTable()))));

        public GateServices With(IGateSessions sessions) => Rig.Services with { Sessions = sessions, Environment = Environment };

        public List<string> AuditCalls() =>
            Rig.Writer.Calls.Select((c, i) => (c, i)).Where(x => x.c == "worktree-grants.audit").Select(x => Rig.Writer.Arguments[x.i]).ToList();

        public void SeedAudit(params string[] lines)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AuditFile)!);
            File.WriteAllText(AuditFile, string.Join('\n', lines) + "\n");
        }

        public static string DenyRow(string reason, string at, string member = "m1") =>
            $"{{\"at\":\"{at}\",\"team\":\"t\",\"member\":\"{member}\",\"action\":\"write\",\"path\":null,\"note\":\"Write\",\"result\":\"deny\",\"reason\":\"{reason}\",\"actor\":\"marker\",\"doc\":false}}";

        private static string Q(string s) => JsonSerializer.Serialize(s);
    }
}
