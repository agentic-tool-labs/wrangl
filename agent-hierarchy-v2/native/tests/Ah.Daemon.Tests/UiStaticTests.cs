using System.Text;
using Ah.Platform;

namespace Ah.Daemon.Tests;

/// <summary>What <see cref="UiStatic"/> serves: the UI's own script with whole-line comments blanked, everything else byte for byte (adr-29).</summary>
public sealed class UiStaticTests
{
    [Fact]
    public void TheUisOwnScriptIsServedWithWholeLineCommentsBlankedAndEverythingElseAsWritten()
    {
        var root = Path.Combine(Sandbox.Create().Root, "ui");
        Directory.CreateDirectory(Path.Combine(root, "vendor"));
        const string script = "  // note\n\t//tab\nx = 1; // note\n/* block */\n * jsdoc\nconst u = \"http://x\";\n// crlf\r\ny();\r\n// last";
        const string served = "\n\nx = 1; // note\n/* block */\n * jsdoc\nconst u = \"http://x\";\n\r\ny();\r\n";
        const string other = "// kept\n  // kept\r\nz();\n";
        File.WriteAllText(Path.Combine(root, "app.js"), script);
        File.WriteAllText(Path.Combine(root, "vendor", "d3.min.js"), other);
        File.WriteAllText(Path.Combine(root, "index.html"), other);
        File.WriteAllText(Path.Combine(root, "ui.css"), other);
        var ui = new UiStatic(PlatformModule.CreateIo(null).Files, root);

        Assert.Equal(served, Text(ui.Read("/ui/app.js")));
        Assert.Equal(other, Text(ui.Read("/ui/vendor/d3.min.js"))); // negative: vendor is byte for byte
        Assert.Equal(other, Text(ui.Read("/ui/index.html"))); // negative: not a script
        Assert.Equal(other, Text(ui.Read("/ui/ui.css"))); // negative: not a script
        Assert.Null(ui.Read("/ui/missing.js"));
        Assert.Null(ui.Read("/ui/../app.js"));
    }

    private static string Text((byte[] Bytes, string ContentType)? reply) => Encoding.UTF8.GetString(Assert.NotNull(reply).Bytes);
}
