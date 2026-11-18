using Ah.Platform;

namespace Ah.Hierarchy.Tests;

/// <summary>A <c>qa</c> map that also holds <c>cadence</c> (ah2-154 Amendment 2): the QA scheduler still reads its own keys from it, with no warning.</summary>
public sealed class QaSettingsCadenceTests
{
    [Fact]
    public void TheSchedulerReadsItsKeysBesideCadence()
    {
        var dir = Directory.CreateTempSubdirectory("ah-qa-cadence-").FullName;
        try
        {
            var path = Path.Combine(dir, "agent-hierarchy.json");
            File.WriteAllText(path, "{\"version\":1,\"qa\":{\"parallel\":3,\"admit_load_ratio\":0.5,\"admit_free_mem_gib\":8,\"cadence\":\"batch\"}}");

            var (settings, warnings) = QaSettings.Read(PlatformModule.CreateIo(null).Files, [path]);

            Assert.Equal(new QaSettings(3, 0.5, 8), settings);
            Assert.Empty(warnings);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
