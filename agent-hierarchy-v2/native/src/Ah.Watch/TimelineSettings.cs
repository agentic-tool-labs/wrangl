using Ah.Abstractions;

namespace Ah.Watch;

/// <summary>Where the timeline is kept and how long it stays live: the opener of the SQLite binding, the path of the state database, its busy timeout, and the retention in days, read when the pruner runs.</summary>
/// <param name="Opener">The opener of SQLite files.</param>
/// <param name="DbPath">The path of the state database (<c>ah.db</c>), whose <c>timeline_events</c> table holds the timeline.</param>
/// <param name="BusyTimeoutMs">The busy timeout of the timeline's connections, the state database's own.</param>
/// <param name="RetentionDays">The days of timeline kept; older rows are pruned.</param>
public sealed record TimelineSettings(ISqliteOpener Opener, string DbPath, int BusyTimeoutMs, Func<int> RetentionDays);
