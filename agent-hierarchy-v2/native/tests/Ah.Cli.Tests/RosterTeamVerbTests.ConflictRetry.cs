using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy.StateCalls;

namespace Ah.Cli.Tests;

/// <summary>close-approve against a team file that changes under it: the grant lands once, or the verb says so and writes nothing.</summary>
public sealed partial class RosterTeamVerbTests
{
    /// <summary>State calls whose team update reports a write conflict the first <see cref="Conflicts"/> times, as a result or as the thrown conflict.</summary>
    public class ConflictCalls : System.Reflection.DispatchProxy
    {
        internal IHierarchyStateCalls Inner { get; set; } = null!;

        internal int Conflicts { get; set; }

        internal bool AsException { get; set; }

        internal int Updates { get; private set; }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "UpdateTeam")
            {
                Updates++;
                if (Updates <= Conflicts)
                {
                    if (AsException)
                    {
                        var error = new InvalidOperationException("conflict");
                        error.Data["code"] = "conflict";
                        throw error;
                    }

                    return new StateUpdateResult(false, new StateResult(true, System.Text.Encoding.UTF8.GetBytes("{\"conflict\":true}")));
                }
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
    }

    private void ConflictThen(int conflicts, bool asException)
    {
        IHierarchyStateCalls? shared = null;
        _wrapStateCalls = inner =>
        {
            if (shared is null)
            {
                var proxy = System.Reflection.DispatchProxy.Create<IHierarchyStateCalls, ConflictCalls>();
                var calls = (ConflictCalls)(object)proxy;
                calls.Inner = inner;
                calls.Conflicts = conflicts;
                calls.AsException = asException;
                shared = proxy;
            }

            return shared;
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void N1aTwoConflictsThenSuccessWritesTheGrantOnce(bool asException)
    {
        ArrangeManaged();
        var token = PlanToken();
        ConflictThen(2, asException);

        var (code, stdout, stderr) = Roster("close-approve", "--team", ChildName, "--verb", "disband", "--plan-token", token);

        Assert.True(code == 0, stderr);
        Assert.True(JsonText.Parse(stdout).Member("approve")!.Value.AsBool());
        Assert.Single(JsonText.Parse(_sandbox.TeamText(ManagerPath)).Member("streams")!.Value.Member("ws1")!.Value.Member("close_grants")!.Value.AsArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void N1bThreeConflictsGiveTheSpecTextAndWriteNothing(bool asException)
    {
        ArrangeManaged();
        var token = PlanToken();
        ConflictThen(3, asException);

        var (code, stdout, stderr) = Roster("close-approve", "--team", ChildName, "--verb", "disband", "--plan-token", token);

        Assert.NotEqual(0, code);
        Assert.Contains("close-approve: team file changed under close-approve; retry; nothing was written.", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("\"approve\": true", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("close_grants", _sandbox.TeamText(ManagerPath), StringComparison.Ordinal);
    }
}
