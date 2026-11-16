using Ah.Abstractions;
using Ah.Core.Text;

namespace Ah.Daemon.Tests;

/// <summary>Every request of a daemon shares its kept spellings, so a route that resolves a path does not list each of its parents every time.</summary>
public sealed class PathCaseRouteTests
{
    [Fact]
    public async Task ARouteKeepsTheSpellingAnEarlierRequestFound()
    {
        var root = Directory.CreateTempSubdirectory("ahcaseroute").FullName;
        var lower = Path.Join(root, "kept");
        var upper = Path.Join(root, "KEPT");
        _ = Directory.CreateDirectory(lower);
        if (!Directory.Exists(upper))
        {
            Directory.Delete(root, recursive: true);
            return; // this volume tells case apart, so nothing is listed
        }

        try
        {
            // as Main does around a program's whole run
            using var keep = PathGuard.KeepCases();
            await using var h = await Harness.StartAsync(Sandbox.Create(), o => o.Routes.Add(new RouteRegistration("GET /t/real", (_, _) =>
                Task.FromResult(TestRoutes.Json(200, PathGuard.RealPath(upper, strict: false)!)))));

            Assert.EndsWith("/kept", (await h.GetAsync("/t/real")).Body, StringComparison.Ordinal);

            Directory.Move(lower, lower + ".tmp");
            Directory.Move(lower + ".tmp", upper);

            Assert.EndsWith("/kept", (await h.GetAsync("/t/real")).Body, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
