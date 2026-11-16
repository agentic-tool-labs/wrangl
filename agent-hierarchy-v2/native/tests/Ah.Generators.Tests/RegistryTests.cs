using Ah.Fixture.Registry;

namespace Ah.Generators.Tests;

public sealed class RegistryTests
{
    private static readonly Abstractions.ModuleRegistry Registry = FixtureModule.Registry;

    [Fact]
    public void OnlyTheAttributedMethodIsAVerb()
    {
        var verb = Assert.Single(Registry.Verbs);

        Assert.Equal("fixture-verb", verb.Name);
    }

    [Fact]
    public void TheVerbRunsThroughTheRegisteredDelegate()
    {
        var stdout = new StringWriter();

        var code = Registry.Verbs[0].Run(["a", "b"], stdout, new StringWriter());

        Assert.Equal(7, code);
        Assert.Equal("a b", stdout.ToString());
    }

    [Fact]
    public void HooksRoutesAndJobsAreRecordedByKind()
    {
        Assert.Equal("fixture-hook", Assert.Single(Registry.Hooks).Name);
        Assert.Equal("POST /fixture", Assert.Single(Registry.Routes).Pattern);
        var job = Assert.Single(Registry.Jobs);
        Assert.Equal("fixture-job", job.Name);
        Assert.Equal(TimeSpan.FromSeconds(5), job.Period);
    }

    [Fact]
    public async Task HookRouteAndJobDelegatesRunTheDeclaredMethods()
    {
        var hook = Registry.Hooks[0].Run(new("fixture-hook", default, new Dictionary<string, string?>(), null!));
        var route = await Registry.Routes[0].Handle(
            new("POST", "/fixture", string.Empty, new Dictionary<string, string>(), default, new Dictionary<string, string>()),
            CancellationToken.None);

        Assert.Equal(0, hook.ExitCode);
        Assert.Equal(204, route.Status);
        await Registry.Jobs[0].Run(CancellationToken.None);
    }

    [Fact]
    public void TheStateDomainCarriesItsStores()
    {
        var domain = Assert.Single(Registry.StateDomains);

        Assert.Equal("fixture", domain.Name);
        Assert.Equal(["fixture/a.json", "fixture/b.json"], domain.Stores);
        Assert.Equal("Ah.Fixture.Registry.FixtureDomain", domain.Target);
    }
}
