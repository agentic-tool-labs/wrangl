namespace Ah.Commands;

internal static partial class DoctorVerb
{
    /// <summary>The heal's row: whether <c>ahd heal</c> restarted a hung daemon lately, hit its cap or failed; it names the command and starts nothing.</summary>
    private static Check AhdHealRow(RosterContext c)
    {
        if (c.Services.Doctor is not { } doctor)
        {
            return new Check("ok", "not checked in this process");
        }

        var (level, detail) = doctor.AhdHeal();
        return new Check(level == "error" ? "red" : level, detail);
    }

    /// <summary>The state database's row: its schema and integrity and how fresh its backups are; a damaged database is red.</summary>
    private static Check StateDbRow(RosterContext c)
    {
        if (c.Services.Doctor is not { } doctor)
        {
            return new Check("ok", "not checked in this process");
        }

        var (level, detail) = doctor.StateDb();
        return new Check(level == "error" ? "red" : level, detail);
    }
}
