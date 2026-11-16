using System.Reflection;
using System.Runtime.InteropServices;

namespace Ah.StateDb;

/// <summary>The system SQLite library, bound with source-generated P/Invokes and found through a resolver that tries each platform candidate in order.</summary>
internal static partial class Sqlite
{
    internal const string Library = "sqlite3";

    internal const int Ok = 0;
    internal const int Busy = 5;
    internal const int Locked = 6;
    internal const int Corrupt = 11;
    internal const int NotADatabase = 26;
    internal const int Row = 100;
    internal const int Done = 101;

    internal const int OpenReadOnly = 0x1;
    internal const int OpenReadWrite = 0x2;
    internal const int OpenUri = 0x40;
    internal const int OpenCreate = 0x4;
    internal const int OpenFullMutex = 0x10000;

    /// <summary>SQLITE_TRANSIENT: the library copies a bound value before the bind call returns.</summary>
    internal static readonly nint Transient = -1;

    private static readonly Lazy<bool> ResolverInstalled = new(
        () =>
        {
            NativeLibrary.SetDllImportResolver(typeof(Sqlite).Assembly, Resolve);
            return true;
        },
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The library file names tried, in order, on a platform; empty when no candidate is known.</summary>
    /// <param name="platform">The platform name: <c>macos</c> or <c>linux</c>.</param>
    internal static string[] Candidates(string platform) => platform switch
    {
        "macos" => ["libsqlite3.dylib"],
        "linux" => ["libsqlite3.so.0", "libsqlite3.so"],
        _ => [],
    };

    internal static string CurrentPlatform() => OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "other";

    /// <summary>Installs the resolver for this assembly once.</summary>
    internal static void InstallResolver() => _ = ResolverInstalled.Value;

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library)
        {
            return 0;
        }

        foreach (var candidate in Candidates(CurrentPlatform()))
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
            {
                return handle;
            }
        }

        return 0;
    }

    [LibraryImport(Library, EntryPoint = "sqlite3_libversion_number")]
    internal static partial int LibVersionNumber();

    [LibraryImport(Library, EntryPoint = "sqlite3_open_v2", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int OpenV2(string file, out nint db, int flags, nint vfs);

    [LibraryImport(Library, EntryPoint = "sqlite3_close_v2")]
    internal static partial int CloseV2(nint db);

    [LibraryImport(Library, EntryPoint = "sqlite3_busy_timeout")]
    internal static partial int BusyTimeout(nint db, int milliseconds);

    [LibraryImport(Library, EntryPoint = "sqlite3_prepare_v2", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int PrepareV2(nint db, string sql, int byteCount, out nint statement, nint tail);

    [LibraryImport(Library, EntryPoint = "sqlite3_step")]
    internal static partial int Step(nint statement);

    [LibraryImport(Library, EntryPoint = "sqlite3_reset")]
    internal static partial int Reset(nint statement);

    [LibraryImport(Library, EntryPoint = "sqlite3_clear_bindings")]
    internal static partial int ClearBindings(nint statement);

    [LibraryImport(Library, EntryPoint = "sqlite3_finalize")]
    internal static partial int Finalize(nint statement);

    [LibraryImport(Library, EntryPoint = "sqlite3_bind_int64")]
    internal static partial int BindInt64(nint statement, int index, long value);

    [LibraryImport(Library, EntryPoint = "sqlite3_bind_null")]
    internal static partial int BindNull(nint statement, int index);

    [LibraryImport(Library, EntryPoint = "sqlite3_bind_text", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int BindText(nint statement, int index, string value, int byteCount, nint destructor);

    [LibraryImport(Library, EntryPoint = "sqlite3_bind_blob")]
    internal static partial int BindBlob(nint statement, int index, ReadOnlySpan<byte> value, int byteCount, nint destructor);

    [LibraryImport(Library, EntryPoint = "sqlite3_bind_zeroblob")]
    internal static partial int BindZeroBlob(nint statement, int index, int byteCount);

    [LibraryImport(Library, EntryPoint = "sqlite3_column_blob")]
    internal static partial nint ColumnBlob(nint statement, int column);

    [LibraryImport(Library, EntryPoint = "sqlite3_column_bytes")]
    internal static partial int ColumnBytes(nint statement, int column);

    [LibraryImport(Library, EntryPoint = "sqlite3_column_int64")]
    internal static partial long ColumnInt64(nint statement, int column);

    [LibraryImport(Library, EntryPoint = "sqlite3_column_text")]
    internal static partial nint ColumnText(nint statement, int column);

    [LibraryImport(Library, EntryPoint = "sqlite3_column_count")]
    internal static partial int ColumnCount(nint statement);

    [LibraryImport(Library, EntryPoint = "sqlite3_column_name")]
    internal static partial nint ColumnName(nint statement, int column);

    [LibraryImport(Library, EntryPoint = "sqlite3_column_type")]
    internal static partial int ColumnType(nint statement, int column);

    [LibraryImport(Library, EntryPoint = "sqlite3_column_double")]
    internal static partial double ColumnDouble(nint statement, int column);

    [LibraryImport(Library, EntryPoint = "sqlite3_set_authorizer")]
    internal static partial int SetAuthorizer(nint db, nint callback, nint userData);

    [LibraryImport(Library, EntryPoint = "sqlite3_changes")]
    internal static partial int Changes(nint db);

    [LibraryImport(Library, EntryPoint = "sqlite3_errmsg")]
    internal static partial nint ErrMsg(nint db);

    [LibraryImport(Library, EntryPoint = "sqlite3_extended_errcode")]
    internal static partial int ExtendedErrCode(nint db);
}
