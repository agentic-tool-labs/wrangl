using Ah.Abstractions;
using Ah.Mesh;
using Ah.Watch;

namespace Ah.Cli.Composition;

/// <summary>The view's path layout: the mailbox's own box-directory computation, which reads no store.</summary>
internal sealed class MailboxLayout(IMailbox mailbox, IAhEnvironment environment) : IViewLayout
{
    public string AhHome => environment.AhHome;

    public string BoxDirFor(string teamFile, string member) => mailbox.BoxDirFor(teamFile, member);
}
