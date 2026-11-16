using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Codex.Tests;

/// <summary>A file reader whose real path is the path itself (a path that does not exist is its own real path), and that holds no files.</summary>
internal sealed class IdentityFiles : IFileReader
{
    public string ReadText(string path) => throw new FileNotFoundException(path);

    public byte[] ReadBytes(string path) => throw new FileNotFoundException(path);

    public FileTail ReadTail(string path, int maxBytes) => throw new FileNotFoundException(path);

    public FileRange ReadRange(string path, long offset, int maxBytes) => throw new FileNotFoundException(path);

    public NoFollowRead ReadNoFollow(string path, int maxBytes) => throw new FileNotFoundException(path);

    public FileStat? Stat(string path, bool followSymlinks = true) => null;

    public bool Exists(string path) => false;

    public bool IsExecutable(string path) => false;

    public string RealPath(string path) => path;

    public IReadOnlyList<string> List(string directory) => [];
}

/// <summary>An environment of fixed variables; <see cref="GetTestOnly"/> answers only while <c>AH_TEST</c> is exactly <c>1</c>.</summary>
internal sealed class FakeEnvironment(IReadOnlyDictionary<string, string> variables) : IAhEnvironment
{
    public FakeEnvironment(JsonObject variables)
        : this(variables.Keys.ToDictionary(k => k, k => variables[k].AsString()))
    {
    }

    public string? GetVariable(string name) => variables.GetValueOrDefault(name);

    public bool TestMode => GetVariable("AH_TEST") == "1";

    public string? GetTestOnly(string name) => TestMode ? GetVariable(name) : null;

    public string HomeDirectory => "/home/test";

    public string AhHome => "/tmp/ah-test-home";

    public PluginInfo? Plugin => null;
}
