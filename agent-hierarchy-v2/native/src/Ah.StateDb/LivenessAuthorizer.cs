using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ah.StateDb;

/// <summary>
/// The statements a liveness connection may run: reads, transactions, and updates of <c>sessions.last_seen_at</c>, the one column it
/// writes. The liveness connection commits with a weaker sync level than the record writers, so it must never touch a table of record
/// (the <c>activity</c> table included, which the activity domain writes through the record writers).
/// </summary>
internal static unsafe class LivenessAuthorizer
{
    private const int Allow = 0;
    private const int Deny = 1;

    private const int Read = 20;
    private const int Select = 21;
    private const int Transaction = 22;
    private const int Update = 23;
    private const int Function = 31;
    private const int Savepoint = 32;
    private const int Recursive = 33;

    internal static nint Callback => (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, nint, nint, nint, int>)&Authorize;

    /// <summary>The decision for one action; the native callback below only decodes its arguments.</summary>
    internal static bool Permits(int action, string? table, string? column) => action switch
    {
        Read or Select or Transaction or Function or Savepoint or Recursive => true,
        Update => table == "sessions" && column == "last_seen_at",
        _ => false,
    };

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Authorize(nint user, int action, nint first, nint second, nint database, nint trigger) =>
        Permits(action, Marshal.PtrToStringUTF8(first), Marshal.PtrToStringUTF8(second)) ? Allow : Deny;
}
