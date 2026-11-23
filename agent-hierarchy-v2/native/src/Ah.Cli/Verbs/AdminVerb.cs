using System.Globalization;
using Ah.Abstractions;
using Ah.Mesh;
using Ah.Platform;

namespace Ah.Cli.Verbs;

/// <summary>
/// <c>ah admin grant &lt;pid&gt; [--session &lt;id&gt;] | revoke | show</c> (ah2-32): the user designates one Claude session, by its
/// process id, as an Orchestrator with admin rights; ah then treats a call from that session as the user's own and refuses it
/// nothing. <c>grant</c> and <c>revoke</c> are the user's alone: run them in a plain terminal, not inside a session.
/// </summary>
internal static class AdminVerb
{
    internal const string Usage = "usage: ah admin grant <pid> [--session <id>] | revoke | show   (grant and revoke: the user only, in a plain terminal)";

    internal static int Run(CliContext context, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] is not ("grant" or "revoke" or "show"))
        {
            stderr.WriteLine(Usage);
            return 2;
        }

        var home = context.Env.AhHome;
        var docs = Ah.StateDb.StateDbModule.CreateHomeDocs(home);
        var table = PlatformModule.CreateProcessTable(context.Runner);
        // ah2-46: the grant is a row of the state database; one left in admin.json by an older build is taken in here
        var leftover = MeshModule.ImportAdminGrant(docs, home);
        if (args[0] == "show")
        {
            if (!MeshModule.TryReadAdminGrant(docs, home, out var grant))
            {
                stdout.WriteLine("the state database cannot be read, so no session counts as the admin session now");
            }
            else
            {
                stdout.WriteLine(grant?.TrimEnd() ?? "no admin session");
                if (grant is not null)
                {
                    stdout.WriteLine($"via: {MeshModule.AdminGrantVia(grant)}");
                    stdout.WriteLine(MeshModule.HoldsAdminGrant(table, Environment.ProcessId, docs, home) ? "this call is from the admin session" : "this call is not from the admin session");
                }
            }

            if (leftover is not null)
            {
                stdout.WriteLine(leftover);
            }

            return 0;
        }

        if (context.Services is not { } services || services.Value.Mailbox.PrincipalOf(Environment.ProcessId).Kind != PrincipalKind.User)
        {
            stderr.WriteLine($"ah admin {args[0]}: only the user may do this. Run it yourself in a plain terminal, outside any Claude Code session.");
            return 1;
        }

        if (args[0] == "revoke")
        {
            if (MeshModule.RevokeAdminGrant(docs, home) is { } failed)
            {
                stderr.WriteLine($"ah admin revoke: {failed}");
                return 1;
            }

            stdout.WriteLine("admin session revoked");
            return 0;
        }

        if (args.Length < 2 || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
        {
            stderr.WriteLine(Usage);
            return 2;
        }

        var session = args.Length >= 4 && args[2] == "--session" ? args[3] : null;
        if (MeshModule.WriteAdminGrant(table, docs, home, pid, session, DateTimeOffset.UtcNow) is { } error)
        {
            stderr.WriteLine($"ah admin grant: {error}");
            return 1;
        }

        stdout.WriteLine($"admin session: pid {pid.ToString(CultureInfo.InvariantCulture)}{(session is null ? string.Empty : $" (session {session})")}. ah treats its calls as yours until that process ends or you run: ah admin revoke");
        return 0;
    }
}
