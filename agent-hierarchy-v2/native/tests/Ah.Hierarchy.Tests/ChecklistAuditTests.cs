using Ah.Core.Json;

namespace Ah.Hierarchy.Tests;

public sealed class ChecklistAuditTests
{
    private const string Req = "20261106-100000-aaaa";
    private const string ReqB = "20261106-100000-bbbb";
    private const string ReqW = "20261106-100000-wwww";
    private const string ReqC = "20261106-100000-cccc";
    private const string ReqD = "20261106-100000-dddd";
    private const string T0 = "2026-11-06T10:00:00Z";
    private const string T1 = "2026-11-06T11:00:00Z";

    [Fact]
    public void ARowADoneReplyLeftInReviewIsExpectedReportedAndAManualOnlyRowMatchesWithNothingWritten()
    {
        var rig = HierarchyReadOpsTests.Make();
        var p = rig.Services.Progress;
        var verb = new PlanCause(HierarchyModule.ChecklistCauseVerb, "add", T0);
        var dispatch = new PlanCause(HierarchyModule.ChecklistCauseDispatch, Req, T0);
        Assert.Null(p.RecordChecklist(rig.Pool, "tm", "_run", "orchestrator@tm", [
            new PlanInput("a", "add", "Build A", At: T0) { Cause = verb },
            new PlanInput("a", "assign", Holder: "tm-implementor", At: T0) { Cause = dispatch },
            new PlanInput("a", "start", At: T0) { Cause = dispatch },
            new PlanInput("a", "ref", Ref: Req, At: T0) { Cause = dispatch },

            // the pre-B mover put a DONE report in review with no reviewer request open
            new PlanInput("a", "review", At: T1) { Cause = new PlanCause(HierarchyModule.ChecklistEventReply, Req, T1) },
            new PlanInput("m", "add", "Mind M", At: T0) { Cause = verb },
            new PlanInput("m", "block", Note: "on the user", At: T1) { Cause = HierarchyModule.ChecklistManualCause(DateTimeOffset.Parse(T1, System.Globalization.CultureInfo.InvariantCulture)) },
        ]).Refused);
        string? Log() => p.PlanLogText(rig.Pool, "tm", "_run");
        string Ledger() => string.Join('\n', p.ReadLedger(rig.Pool, "tm").Select(static r => JsonText.Stringify(r, 0)));
        var before = (Log(), Ledger());
        ChecklistAuditRequest? RequestOf(string id) => id == Req
            ? new ChecklistAuditRequest(Req, T0, "implementor", "tm-implementor", "orchestrator", "build-a", null, "_run/a", null, [new ChecklistAuditResponse(T1, "DONE build-a\n- built", false, null, false)])
            : null;

        var (count, disagree) = HierarchyModule.ChecklistAudit(p, rig.Pool, "tm", ["_run"], stream => p.PlanLogText(rig.Pool, "tm", stream), RequestOf);

        Assert.Equal(2, count);
        var finding = Assert.Single(disagree);
        Assert.Equal(("a", "in_review", "reported", false), (finding.Id, finding.Stored, finding.Expected, finding.PreCause));
        Assert.Equal($"reply {Req} at {T1}", finding.Why);
        Assert.Equal(before, (Log(), Ledger()));
    }

    [Fact]
    public void AReplyOrAckWithNoResponseAtOrBeforeItStandsAsRecordedAndDoesNotDisagree()
    {
        var rig = HierarchyReadOpsTests.Make();
        var p = rig.Services.Progress;
        var dispatch = new PlanCause(HierarchyModule.ChecklistCauseDispatch, Req, T0);
        Assert.Null(p.RecordChecklist(rig.Pool, "tm", "_run", "orchestrator@tm", [
            new PlanInput("a", "add", "Build A", At: T0) { Cause = new PlanCause(HierarchyModule.ChecklistCauseVerb, "add", T0) },
            new PlanInput("a", "assign", Holder: "tm-implementor", At: T0) { Cause = dispatch },
            new PlanInput("a", "start", At: T0) { Cause = dispatch },
            new PlanInput("a", "ref", Ref: Req, At: T0) { Cause = dispatch },
            new PlanInput("a", "review", At: T1) { Cause = new PlanCause(HierarchyModule.ChecklistEventReply, Req, T1) },
            new PlanInput("b", "add", "Build B", At: T0) { Cause = new PlanCause(HierarchyModule.ChecklistCauseVerb, "add", T0) },
            new PlanInput("b", "block", Note: "acked", At: T1) { Cause = new PlanCause(HierarchyModule.ChecklistEventAck, ReqB, T1) },

            // c has two briefs; withdrawing ReqC kept it because ReqD was still open (b287)
            new PlanInput("c", "add", "Build C", At: T0) { Cause = new PlanCause(HierarchyModule.ChecklistCauseVerb, "add", T0) },
            new PlanInput("c", "start", At: T0) { Cause = new PlanCause(HierarchyModule.ChecklistCauseDispatch, ReqC, T0) },
            new PlanInput("c", "ref", Ref: ReqC, At: T0) { Cause = new PlanCause(HierarchyModule.ChecklistCauseDispatch, ReqC, T0) },
            new PlanInput("c", "ref", Ref: ReqD, At: T0) { Cause = new PlanCause(HierarchyModule.ChecklistCauseDispatch, ReqD, T0) },
            new PlanInput("c", "note", Note: $"brief {ReqC} withdrawn; another brief is open", At: T1) { Cause = new PlanCause(HierarchyModule.ChecklistEventWithdraw, ReqC, T1) },
        ]).Refused);

        // each only response is filed after its reply/ack fact: replaying that later DONE body would re-decide the row and disagree
        var late = new ChecklistAuditResponse("2026-11-06T12:00:00Z", "DONE build\n- built", false, null, false);
        ChecklistAuditRequest? RequestOf(string id) => id switch
        {
            Req => new ChecklistAuditRequest(Req, T0, "implementor", "tm-implementor", "orchestrator", "build-a", null, "_run/a", null, [late]),
            ReqB => new ChecklistAuditRequest(ReqB, T0, "implementor", "tm-implementor", "orchestrator", "build-b", null, "_run/b", null, [late]),
            ReqC => new ChecklistAuditRequest(ReqC, T0, "implementor", "tm-implementor", "orchestrator", "build-c", null, "_run/c", null, [new ChecklistAuditResponse(T1, null, false, null, true)]),
            ReqD => new ChecklistAuditRequest(ReqD, T0, "implementor", "tm-implementor", "orchestrator", "build-c", null, "_run/c", null, []),
            _ => null,
        };

        var (count, disagree) = HierarchyModule.ChecklistAudit(p, rig.Pool, "tm", ["_run"], stream => p.PlanLogText(rig.Pool, "tm", stream), RequestOf);

        // c replays as kept: dropping it would disagree with its stored in_progress
        Assert.Equal(3, count);
        Assert.Empty(disagree);
    }

    [Fact]
    public void AFiledResponseNoReplyRowCoversIsAMissedReplyAndACoveredOneIsNotReplayedTwice()
    {
        var rig = HierarchyReadOpsTests.Make();
        var p = rig.Services.Progress;
        PlanInput[] Dispatched(string item, string req) => [
            new PlanInput(item, "add", "Build", At: T0) { Cause = new PlanCause(HierarchyModule.ChecklistCauseVerb, "add", T0) },
            new PlanInput(item, "assign", Holder: "tm-implementor", At: T0) { Cause = new PlanCause(HierarchyModule.ChecklistCauseDispatch, req, T0) },
            new PlanInput(item, "start", At: T0) { Cause = new PlanCause(HierarchyModule.ChecklistCauseDispatch, req, T0) },
            new PlanInput(item, "ref", Ref: req, At: T0) { Cause = new PlanCause(HierarchyModule.ChecklistCauseDispatch, req, T0) },
        ];

        // a: the reply hook gave up, so the DONE response moved nothing; b: the reply hook moved it
        Assert.Null(p.RecordChecklist(rig.Pool, "tm", "_run", "orchestrator@tm", [
            .. Dispatched("a", Req),
            .. Dispatched("b", ReqB),
            new PlanInput("b", "report", At: T1) { Cause = new PlanCause(HierarchyModule.ChecklistEventReply, ReqB, T1) },

            .. Dispatched("w", ReqW),
        ]).Refused);

        // w: closed superseded by the Orchestrator with no report, the close row written by the mover; the close response is not a reply
        Assert.Null(HierarchyModule.ChecklistOnClose(p, rig.Pool, "tm", ReqW, "_run/w", "orchestrator@tm", null, "superseded", T1));
        var done = new ChecklistAuditResponse(T1, "DONE build\n- built", false, null, false);
        ChecklistAuditRequest? RequestOf(string id) => id switch
        {
            Req or ReqB => new ChecklistAuditRequest(id, T0, "implementor", "tm-implementor", "orchestrator", "build", null, id == Req ? "_run/a" : "_run/b", null, [done]),
            ReqW => new ChecklistAuditRequest(id, T0, "implementor", "tm-implementor", "orchestrator", "build", null, "_run/w", null, [new ChecklistAuditResponse(T1, "DONE build\n- built", true, "superseded", false)]),
            _ => null,
        };

        var (count, disagree) = HierarchyModule.ChecklistAudit(p, rig.Pool, "tm", ["_run"], stream => p.PlanLogText(rig.Pool, "tm", stream), RequestOf);

        Assert.Equal(3, count);
        var finding = Assert.Single(disagree);
        Assert.Equal(("a", "in_progress", "reported", $"missed reply {Req}"), (finding.Id, finding.Stored, finding.Expected, finding.Why));
    }

    [Fact]
    public void ALedgerShipCarryingAReportedShaWithNoShipRowIsAMissedShipAndOneThatDoesNotCarryItIsNot()
    {
        const string S0 = "2026-05-01T10:00:00Z";
        const string S1 = "2026-05-01T11:00:00Z";
        var rig = HierarchyReadOpsTests.Make();
        var p = rig.Services.Progress;
        PlanInput[] Reported(string item, string sha) => [
            new PlanInput(item, "add", "Build", At: S0) { Cause = new PlanCause(HierarchyModule.ChecklistCauseVerb, "add", S0) },
            new PlanInput(item, "report", Commits: [sha], At: S1) { Cause = HierarchyModule.ChecklistManualCause(DateTimeOffset.Parse(S1, System.Globalization.CultureInfo.InvariantCulture)) },
        ];
        Assert.Null(p.RecordChecklist(rig.Pool, "tm", "_run", "orchestrator@tm", [.. Reported("a", "aaa1111"), .. Reported("z", "zzz9999")]).Refused);

        // the ledger ship (at the rig clock, after S1) carries a's sha, not z's; the ship that should have closed a wrote no plan row
        Assert.True(p.AppendLedger(rig.Pool, "tm", new LedgerEvent("ship", Sha: "shipsha")).Appended);
        var (count, disagree) = HierarchyModule.ChecklistAudit(p, rig.Pool, "tm", ["_run"], stream => p.PlanLogText(rig.Pool, "tm", stream), static _ => null,
            static (rev, sha) => rev == "aaa1111" && sha == "shipsha");

        Assert.Equal(2, count);
        var finding = Assert.Single(disagree);
        Assert.Equal(("a", "reported", "done", "missed ship shipsha"), (finding.Id, finding.Stored, finding.Expected, finding.Why));
    }
}
