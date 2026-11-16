using Ah.Abstractions;

namespace Ah.TestKit;

public sealed class FakeHostProbe(HostSnapshot snapshot) : IHostProbe
{
    public HostSnapshot Snapshot { get; set; } = snapshot;

    public int Reads { get; private set; }

    public HostSnapshot Read()
    {
        Reads++;
        return Snapshot;
    }
}
