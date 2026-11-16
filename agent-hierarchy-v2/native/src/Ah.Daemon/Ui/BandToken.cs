using System.Security.Cryptography;
using System.Text;
using Ah.Abstractions;

namespace Ah.Daemon;

/// <summary>
/// The one-shot token a press of the band button presents to the daemon: 64 lower-case hex characters held in memory and in a file
/// only the user can read. It is new at every daemon start and new again after each use, before the use is answered. A token that
/// does not match changes nothing, so a wrong guess cannot spoil the user's next press.
/// </summary>
internal sealed class BandToken(IRawFileWriter writer, string path, int pid)
{
    internal const int Bytes = 32;

    private readonly object _gate = new();
    private string _current = Mint();

    /// <summary>Writes the current token to its file, 0600, by a temp file and a rename.</summary>
    internal void Write()
    {
        lock (_gate)
        {
            Store(_current);
        }
    }

    /// <summary>
    /// True when <paramref name="presented"/> is the current token. The token is then replaced, and the new one is in the file before
    /// this returns. When the new file cannot be written nothing changes and the answer is false.
    /// </summary>
    internal bool TryUse(string? presented)
    {
        if (presented is not { Length: Bytes * 2 })
        {
            return false;
        }

        lock (_gate)
        {
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(presented), Encoding.ASCII.GetBytes(_current)))
            {
                return false;
            }

            var next = Mint();
            try
            {
                Store(next);
            }
            catch (IOException)
            {
                return false;
            }

            _current = next;
            return true;
        }
    }

    /// <summary>The token now in force; for a test to read what the file should hold.</summary>
    internal string Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    private static string Mint() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(Bytes));

    private void Store(string token) =>
        writer.WriteAtomic(path, $"{path}.{pid}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}.tmp", Encoding.ASCII.GetBytes(token), UnixFileMode.UserRead | UnixFileMode.UserWrite);
}
