using Ah.Abstractions;

namespace Ah.State;

/// <summary>The only write path an op body gets: the engine's raw writer and appender behind <see cref="IStoreWriteContext"/>.</summary>
internal sealed class StoreWriteContext(IRawFileWriter writer, IRecordAppender appender) : IStoreWriteContext
{
    public void Write(string path, string tmpPath, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null) =>
        writer.WriteAtomic(path, tmpPath, bytes, mode);

    public bool CreateExclusive(string path, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null) =>
        writer.CreateExclusive(path, bytes, mode);

    public void Append(string path, ReadOnlySpan<byte> record) => appender.Append(path, record);

    public void Rename(string fromPath, string toPath) => writer.Rename(fromPath, toPath);

    public bool Remove(string path) => writer.Unlink(path);

    public void Mkdir(string path, UnixFileMode? mode = null) => writer.Mkdir(path, mode);
}
