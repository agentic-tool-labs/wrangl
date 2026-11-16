namespace Ah.Cli.Tests;

/// <summary>What creating and migrating a home's state database leaves behind, made once per test process and copied into each sandbox: the migrations cost 17% of a roster test (sampled).</summary>
internal static class DatabaseTemplate
{
    private static readonly Lazy<string> Made = new(static () =>
    {
        var home = Path.Combine(Ah.TestProcess.ProcessSandbox.Root, "database-template");
        Assert.Null(Ah.StateDb.StateDbModule.EnsureDatabase(home));
        return home;
    });

    /// <summary>Gives <paramref name="ahHome"/> a state database of its own.</summary>
    internal static void CopyTo(string ahHome)
    {
        var from = Made.Value;
        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories).Prepend(from))
        {
            Directory.CreateDirectory(Path.Join(ahHome, Path.GetRelativePath(from, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Join(ahHome, Path.GetRelativePath(from, file)));
        }
    }
}
