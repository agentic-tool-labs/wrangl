using Ah.Core.Json;

namespace Ah.Hierarchy;

internal sealed partial class HierLib
{
    public IPeerReach PeerReach(IReadOnlyList<JsonValue> records, Func<string, string?> realPath) => new PeerReachIndex(this, records, realPath);

    /// <summary>A real-path lookup that resolves each path once; null when the path cannot be resolved because of an I/O or permission error.</summary>
    private Func<string, string?> MemoizedRealPath()
    {
        var held = new Dictionary<string, string?>(StringComparer.Ordinal);
        return path =>
        {
            if (!held.TryGetValue(path, out var resolved))
            {
                try
                {
                    resolved = _files.RealPath(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    resolved = null;
                }

                held[path] = resolved;
            }

            return resolved;
        };
    }

    /// <summary>
    /// One pass over the peer records, kept as two lookups: the acknowledgements by request id, and the <c>sent</c> rows of responses
    /// that no <c>reopened</c> row cut. What differs between callers (which sessions count, which addresses are the Orchestrator's)
    /// is applied to the lookups afterwards, so many callers share the pass.
    /// </summary>
    private sealed class PeerReachIndex : IPeerReach
    {
        private readonly Func<string, string?> _realPath;
        private readonly Dictionary<string, List<(string? Session, string? Team)>> _acks = new(StringComparer.Ordinal);
        private readonly List<(string Response, bool Always, string? To)> _sent = [];

        public PeerReachIndex(HierLib owner, IReadOnlyList<JsonValue> records, Func<string, string?> realPath)
        {
            _realPath = realPath;
            var cut = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in records)
            {
                if (!r.IsTruthy)
                {
                    continue;
                }

                if (Member(r, "type") is { IsText: true } kind && kind.AsString() == "reopened" && r.Member("msg_id") is { IsText: true } cutId)
                {
                    _ = cut.Add(cutId.AsString());
                }
            }

            foreach (var r in records)
            {
                if (!r.IsTruthy || Member(r, "type") is not { IsText: true } type)
                {
                    continue;
                }

                var name = type.AsString();
                if (name == "report-acked" && r.Member("request_id") is { IsText: true } acked)
                {
                    var session = r.Member("session_id") is { IsText: true } rs ? rs.AsString() : null;
                    var team = r.Member("role") is { IsText: true } role && role.AsString() == "orchestrator" && r.Member("team") is { IsText: true } t ? t.AsString() : null;
                    if (!_acks.TryGetValue(acked.AsString(), out var list))
                    {
                        list = [];
                        _acks[acked.AsString()] = list;
                    }

                    list.Add((session, team));
                }
                else if (name == "sent" && r.Member("response") is { IsText: true } response
                    && !(r.Member("msg_id") is { IsText: true } sentId && cut.Contains(sentId.AsString())))
                {
                    var always = ResolvedTheRequest(owner, r, response.AsString());
                    _sent.Add((response.AsString(), always, r.Member("to") is { IsText: true } to ? owner.StripRef(to.AsString()) : null));
                }
            }
        }

        public Func<JsonValue?, bool> AckedBy(Func<string, bool> counts, IReadOnlyCollection<string>? teams = null)
        {
            var answers = new Dictionary<string, bool>(StringComparer.Ordinal);
            return id =>
            {
                if (id is not { IsText: true } text || !_acks.TryGetValue(text.AsString(), out var entries))
                {
                    return false;
                }

                var key = text.AsString();
                if (!answers.TryGetValue(key, out var acked))
                {
                    acked = entries.Any(e => (e.Session is not null && counts(e.Session)) || (teams is not null && e.Team is not null && teams.Contains(e.Team)));
                    answers[key] = acked;
                }

                return acked;
            };
        }

        public Func<string, bool> SentFor(Func<string, bool> toIsOrchestrator)
        {
            var sent = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in _sent)
            {
                if (entry.Always || (entry.To is not null && toIsOrchestrator(entry.To)))
                {
                    _ = sent.Add(entry.Response);
                }
            }

            if (sent.Count == 0)
            {
                return static _ => false;
            }

            var real = new Lazy<HashSet<string>>(() => [.. sent.Select(s => _realPath(s) ?? s)]);
            return path => sent.Contains(path) || real.Value.Contains(_realPath(path) ?? path);
        }

        private static bool ResolvedTheRequest(HierLib owner, JsonValue sent, string response)
        {
            var requestId = owner._core.ParseMsgFilename(Path.GetFileName(response))?.Id;
            return requestId is not null && sent.Member("resolved") is { IsArray: true } resolved
                && resolved.AsArray().Any(o => o.IsMap
                    && ((o.Member("task") is { IsText: true } task && task.AsString() == requestId)
                        || (o.Member("msg") is { IsText: true } msg && msg.AsString().Contains(requestId, StringComparison.Ordinal))));
        }
    }
}
