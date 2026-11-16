using System.Text;
using Ah.Core.Json;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Tests;

/// <summary>
/// A session that is out stays bare: a plain payload through every hook unit, for the events each one can see, shows the model
/// nothing. A new unit is covered with no edit here. No payload here is an ah2-specific act, which is what the gates answer.
/// </summary>
public sealed class BareSessionTests
{
    private static JsonObject Event(string root, string hookEvent, string? tool, JsonObject? input)
    {
        var e = new JsonObject
        {
            ["hook_event_name"] = hookEvent,
            ["session_id"] = "s-bare",
            ["cwd"] = root + "/proj",
            ["transcript_path"] = root + "/none.jsonl",
        };
        if (tool is not null)
        {
            e["tool_name"] = tool;
            e["tool_input"] = input ?? new JsonObject();
            e["tool_use_id"] = "t1";
        }

        return e;
    }

    public static TheoryData<string, string?> Payloads() => new()
    {
        { "SessionStart", null },
        { "UserPromptSubmit", null },
        { "Stop", null },
        { "SubagentStart", null },
        { "SubagentStop", null },
        { "SessionEnd", null },
        { "PreToolUse", "Read" },
        { "PreToolUse", "Bash" },
        { "PreToolUse", "AskUserQuestion" },
        { "PreToolUse", "SendMessage" },
        { "PostToolUse", "Bash" },
        { "PostToolUse", "Skill" },
        { "PostToolUse", "SendMessage" },
        { "PostToolUse", "AskUserQuestion" },
        { "PostToolUseFailure", "Bash" },
    };

    [Theory]
    [MemberData(nameof(Payloads))]
    public void EveryUnitIsSilentForAPlainPayload(string hookEvent, string? tool)
    {
        // A gate holds any payload that is not a tool call, which is about the payload and not the session.
        var units = HooksModule.Units.Where(u => hookEvent == "PreToolUse" || !u.Name.StartsWith("pretooluse", StringComparison.Ordinal));
        foreach (var unit in units.Select(u => u.Name))
        {
            var f = new OrchestrateCommandTests.Fixture();
            var rig = f.Rig;
            var input = tool switch
            {
                "Bash" => new JsonObject { ["command"] = "herdr agent start 'bad name' && echo hi" },
                "Skill" => new JsonObject { ["skill"] = "foo:agent-team" },
                "SendMessage" => new JsonObject { ["to"] = "someone", ["message"] = "hello" },
                "AskUserQuestion" => new JsonObject { ["questions"] = new JsonArray(), ["metadata"] = new JsonObject { ["source"] = "consent=member-launch-rules" } },
                _ => null,
            };
            var payload = Event(rig.Sandbox.Root, hookEvent, tool, input);
            if (hookEvent == "UserPromptSubmit")
            {
                payload["prompt"] = "stand up a team, please";
            }

            if (hookEvent == "SessionStart")
            {
                payload["source"] = "startup";
            }

            var gate = HooksModule.Units.Single(u => u.Name == unit).Class != GateClass.NotAGate;
            var output = Encoding.UTF8.GetString(rig.Run(unit, JsonText.Stringify(payload, 0), gate: gate, services: () => f.Services()).Stdout.Span);

            Assert.True(output.Length == 0, $"{unit} on {hookEvent}/{tool}: {output}");
        }
    }
}
