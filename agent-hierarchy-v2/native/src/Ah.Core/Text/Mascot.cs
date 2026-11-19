namespace Ah.Core.Text;

/// <summary>
/// The Wrangl mascot, embedded from <c>assets/mascot.ansi</c> (24-bit colour) and <c>assets/mascot.txt</c> (plain), both made by
/// <c>scripts/gen-mascot.py</c>. The one reader of the art: <c>ah --version</c> and the terminal view both draw it from here.
/// </summary>
public static class Mascot
{
    /// <summary>The art in 24-bit colour, exactly as the file holds it.</summary>
    public static string Ansi() => Read("mascot.ansi");

    /// <summary>The art without colour, exactly as the file holds it.</summary>
    public static string Plain() => Read("mascot.txt");

    private static string Read(string name)
    {
        using var stream = typeof(Mascot).Assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
