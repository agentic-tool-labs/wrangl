using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Ah.Codex;

/// <summary>One complete WebSocket message: its opcode and its data, the fragments of a fragmented message joined.</summary>
internal readonly record struct WsMessage(int Opcode, byte[] Data);

/// <summary>A frame the reader refused: a declared length (or the fragments of one message together) over the cap.</summary>
internal sealed class WsFrameTooLargeException(int maxBytes) : Exception($"websocket message larger than {maxBytes} bytes");

/// <summary>RFC 6455 framing as the Node client does it: text frames, always masked from the client, with the length cap enforced when the header is read.</summary>
internal static class WsFraming
{
    public const int OpText = 1;
    public const int OpClose = 8;
    public const int OpPing = 9;
    public const int OpPong = 10;

    /// <summary>The largest WebSocket message (a frame, or the fragments of one message) the client buffers.</summary>
    public const int MaxFrameBytes = 8 * 1024 * 1024;

    /// <summary>The largest HTTP response head the client buffers while waiting for the upgrade to finish.</summary>
    public const int MaxHeaderBytes = 16 * 1024;

    private const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>The accept key a server must answer an upgrade with: SHA-1 of the key and the RFC 6455 GUID, base64.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "RFC 6455 fixes the accept key as SHA-1; it is a handshake check, not a security boundary.")]
    public static string AcceptKey(string key) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key + Guid)));

    /// <summary>One frame carrying <paramref name="payload"/>; a client masks (RFC 6455 5.3), a server does not.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> payload, int opcode = OpText, bool mask = false)
    {
        var n = payload.Length;
        var maskBit = mask ? 0x80 : 0;
        var headLength = n < 126 ? 2 : n < 65536 ? 4 : 10;
        var frame = new byte[headLength + (mask ? 4 : 0) + n];
        frame[0] = (byte)(0x80 | opcode);
        if (n < 126)
        {
            frame[1] = (byte)(maskBit | n);
        }
        else if (n < 65536)
        {
            frame[1] = (byte)(maskBit | 126);
            frame[2] = (byte)(n >> 8);
            frame[3] = (byte)(n & 0xff);
        }
        else
        {
            frame[1] = (byte)(maskBit | 127);
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2, 8), (ulong)n);
        }

        var at = headLength;
        if (!mask)
        {
            payload.CopyTo(frame.AsSpan(at));
            return frame;
        }

        var key = RandomNumberGenerator.GetBytes(4);
        key.CopyTo(frame, at);
        at += 4;
        for (var i = 0; i < n; i++)
        {
            frame[at + i] = (byte)(payload[i] ^ key[i % 4]);
        }

        return frame;
    }

    public static byte[] Encode(string text, int opcode = OpText, bool mask = false) => Encode(Encoding.UTF8.GetBytes(text), opcode, mask);
}

/// <summary>
/// Incremental frame reader: <see cref="Push"/> returns the complete messages it now holds. It throws when a frame's declared
/// length, or the fragments of one message together, would exceed the cap, and it does so as soon as the header is read: no
/// buffer is ever sized from a declared length.
/// </summary>
internal sealed class WsFrameReader(int maxBytes = WsFraming.MaxFrameBytes)
{
    private byte[] _buf = new byte[4096];
    private int _start;
    private int _end;
    private List<byte[]>? _parts;
    private int _fragOpcode;
    private long _fragBytes;

    public IReadOnlyList<WsMessage> Push(ReadOnlySpan<byte> chunk)
    {
        Append(chunk);
        var output = new List<WsMessage>();
        while (true)
        {
            var available = _end - _start;
            if (available < 2)
            {
                break;
            }

            var span = _buf.AsSpan(_start, available);
            var fin = (span[0] & 0x80) != 0;
            var opcode = span[0] & 0x0f;
            var masked = (span[1] & 0x80) != 0;
            ulong len = (ulong)(span[1] & 0x7f);
            var offset = 2;
            if (len == 126)
            {
                if (available < 4)
                {
                    break;
                }

                len = BinaryPrimitives.ReadUInt16BigEndian(span[2..4]);
                offset = 4;
            }
            else if (len == 127)
            {
                if (available < 10)
                {
                    break;
                }

                len = BinaryPrimitives.ReadUInt64BigEndian(span[2..10]);
                offset = 10;
            }

            if (len > (ulong)maxBytes || (ulong)_fragBytes + len > (ulong)maxBytes)
            {
                throw new WsFrameTooLargeException(maxBytes);
            }

            var payload = (int)len;
            var need = offset + (masked ? 4 : 0) + payload;
            if (available < need)
            {
                break;
            }

            var data = span.Slice(offset + (masked ? 4 : 0), payload).ToArray();
            if (masked)
            {
                var key = span.Slice(offset, 4);
                for (var i = 0; i < data.Length; i++)
                {
                    data[i] ^= key[i % 4];
                }
            }

            _start += need;
            if (opcode == 0 || (opcode == WsFraming.OpText && !fin))
            {
                if (_parts is null)
                {
                    _parts = [];
                    _fragOpcode = opcode;
                }

                _parts.Add(data);
                _fragBytes += data.Length;
                if (!fin)
                {
                    continue;
                }

                output.Add(new WsMessage(_fragOpcode, Join(_parts)));
                _parts = null;
                _fragBytes = 0;
            }
            else
            {
                output.Add(new WsMessage(opcode, data));
            }
        }

        return output;
    }

    private static byte[] Join(List<byte[]> parts)
    {
        var all = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var p in parts)
        {
            p.CopyTo(all, at);
            at += p.Length;
        }

        return all;
    }

    private void Append(ReadOnlySpan<byte> chunk)
    {
        if (_start == _end)
        {
            _start = 0;
            _end = 0;
        }

        if (_end + chunk.Length > _buf.Length)
        {
            var live = _end - _start;
            var next = live + chunk.Length > _buf.Length ? new byte[Math.Max(_buf.Length * 2, live + chunk.Length)] : _buf;
            Buffer.BlockCopy(_buf, _start, next, 0, live);
            _buf = next;
            _start = 0;
            _end = live;
        }

        chunk.CopyTo(_buf.AsSpan(_end));
        _end += chunk.Length;
    }
}
