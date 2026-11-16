using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.State;

namespace Ah.Commands;

/// <summary>
/// <c>ahd state &lt;domain&gt; &lt;op&gt; [--hook] [--budget MS]</c>: one write of the state hub, run in this process.
/// Stdin is one JSON object, <c>{"args", "context", "op_id"}</c>; stdout is exactly the body the daemon's route would
/// return. Exit codes: 0 success (a digest conflict is a success), 1 the op failed, 2 a bad request, an unknown domain or
/// op, or a body over the cap, 75 the domain lock did not clear, 70 any other failure (stdout carries the route's
/// <c>internal</c> body).
/// </summary>
internal static class StateVerb
{
    internal const int NotComposed = 70;
    internal const int DefaultBodyCap = 65_536;
    internal const int MessageBodyCap = 262_144;

    private static readonly string[] AllowedKeys = ["args", "context", "op_id"];

    [Verb("state")]
    internal static int Run(string[] rest, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(rest);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        if (StateModule.Service is not { } service)
        {
            stderr.WriteLine("ahd state: the state service has not been composed");
            return NotComposed;
        }

        if (!TryParseArguments(rest, out var domain, out var op, out var budget, out var problem))
        {
            return Bad(stdout, problem);
        }

        var cap = domain == "msgs" && op is "create" or "settle" or "fill" ? MessageBodyCap : DefaultBodyCap;
        var read = ReadBody(CommandsModule.StandardInput(), cap + 1);
        if (read.Length > cap)
        {
            return Bad(stdout, $"the request body is over {cap.ToString(CultureInfo.InvariantCulture)} bytes", "too_large");
        }

        JsonValue request;
        try
        {
            request = read.Length == 0 ? JsonValue.FromObject(new JsonObject()) : JsonText.Parse(read);
        }
        catch (JsonParseException)
        {
            return Bad(stdout, "the request body is not JSON");
        }

        if (!request.IsMap)
        {
            return Bad(stdout, "the request body must be a JSON object");
        }

        foreach (var key in request.AsObject().Keys)
        {
            if (!AllowedKeys.Contains(key, StringComparer.Ordinal))
            {
                return Bad(stdout, $"unknown key {key} in the request body");
            }
        }

        var reply = service.InvokeAsync(
            domain,
            op,
            viaPost: true,
            request.Member("args"),
            request.Member("context"),
            request.Member("op_id") is { IsText: true } id ? id.AsString() : null,
            static (_, work) => Task.FromResult(work()),
            budget).GetAwaiter().GetResult();
        stdout.Write(System.Text.Encoding.UTF8.GetString(reply.Body.Span));
        stdout.Write('\n');
        return ExitCode(reply);
    }

    /// <summary>The exit code for an answer: 0 for a 200, 1 for the op's own failure (422), 2 for a bad request or an unknown op, 75 for a lock that never cleared, else 70.</summary>
    internal static int ExitCode(StateReply reply) => reply switch
    {
        { Status: 200 } => 0,
        { Status: 422 } => 1,
        { Code: "lock_timeout" } => 75,
        { Status: 400 or 404 or 413 } => 2,
        _ => 70,
    };

    private static bool TryParseArguments(string[] rest, out string domain, out string op, out TimeSpan? budget, out string problem)
    {
        domain = string.Empty;
        op = string.Empty;
        budget = null;
        problem = string.Empty;
        var positional = new List<string>();
        var hook = false;
        for (var i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--hook":
                    hook = true;
                    break;
                case "--cwd":
                    // The request body carries its own context; the flag is accepted for the callers that pass it to every verb.
                    if (i + 1 >= rest.Length || rest[i + 1].StartsWith('-'))
                    {
                        problem = "--cwd needs a value";
                        return false;
                    }

                    i++;
                    break;
                case "--budget":
                    if (i + 1 >= rest.Length
                        || !int.TryParse(rest[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
                        || ms <= 0)
                    {
                        problem = "--budget needs a positive number of milliseconds";
                        return false;
                    }

                    budget = TimeSpan.FromMilliseconds(ms);
                    i++;
                    break;
                default:
                    if (rest[i].StartsWith('-'))
                    {
                        problem = $"unknown option {rest[i]}";
                        return false;
                    }

                    positional.Add(rest[i]);
                    break;
            }
        }

        if (positional.Count != 2)
        {
            problem = "usage: ahd state <domain> <op> [--hook] [--budget MS]";
            return false;
        }

        if (budget is not null && !hook)
        {
            problem = "--budget is for --hook";
            return false;
        }

        domain = positional[0];
        op = positional[1];
        return true;
    }

    private static byte[] ReadBody(Stream input, int limit)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int n;
        while (buffer.Length < limit && (n = input.Read(chunk, 0, (int)Math.Min(chunk.Length, limit - buffer.Length))) > 0)
        {
            buffer.Write(chunk, 0, n);
        }

        return buffer.ToArray();
    }

    private static int Bad(TextWriter stdout, string message, string code = "bad_request")
    {
        var error = new JsonObject();
        error["code"] = code;
        error["message"] = message;
        var body = new JsonObject();
        body["error"] = error;
        stdout.Write(JsonText.Stringify(JsonValue.FromObject(body), 0));
        stdout.Write('\n');
        return 2;
    }
}
