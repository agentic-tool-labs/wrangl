using Ah.Abstractions;

namespace Ah.Platform.Tests;

/// <summary>
/// A virtual file system for the trust and test-mode rows that need an owner other than the test's: a flat map of absolute paths to
/// directories, files and symbolic links. <see cref="RealPath"/> is strict (every component must exist), like the operating system's.
/// </summary>
internal sealed class VirtualFiles : IFileReader
{
    internal sealed record Node(FileKind Kind, uint Uid, uint Mode, string Text = "", string? Target = null);

    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal) { ["/"] = new Node(FileKind.Directory, 0, 0b111_101_101) };

    internal VirtualFiles Dir(string path, uint uid, uint mode = 0b111_000_000)
    {
        Ensure(path);
        _nodes[path] = new Node(FileKind.Directory, uid, mode);
        return this;
    }

    internal VirtualFiles File(string path, uint uid, uint mode, string text)
    {
        Ensure(Dirname(path));
        _nodes[path] = new Node(FileKind.File, uid, mode, text);
        return this;
    }

    internal VirtualFiles Link(string path, string target)
    {
        Ensure(Dirname(path));
        _nodes[path] = new Node(FileKind.Symlink, 0, 0b111_111_111, Target: target);
        return this;
    }

    public string ReadText(string path) =>
        Lookup(path, follow: true) is { Kind: FileKind.File } node ? node.Text : throw new FileNotFoundException(path);

    public byte[] ReadBytes(string path) => System.Text.Encoding.UTF8.GetBytes(ReadText(path));

    public FileTail ReadTail(string path, int maxBytes) => throw new NotSupportedException();

    public FileRange ReadRange(string path, long offset, int maxBytes) => throw new NotSupportedException();

    public NoFollowRead ReadNoFollow(string path, int maxBytes) => throw new NotSupportedException();

    public FileStat? Stat(string path, bool followSymlinks = true)
    {
        var node = Lookup(path, followSymlinks);
        return node is null ? null : new FileStat(node.Kind, node.Text.Length, 0, 0, node.Mode, node.Uid);
    }

    public bool Exists(string path) => Lookup(path, follow: true) is not null;

    public string RealPath(string path)
    {
        var resolved = new List<string>();
        Walk(path, resolved, 0);
        return "/" + string.Join('/', resolved);
    }

    public IReadOnlyList<string> List(string directory) => throw new NotSupportedException();

    public bool IsExecutable(string path) => false;

    private void Walk(string path, List<string> resolved, int depth)
    {
        if (depth > 16)
        {
            throw new IOException("too many links: " + path);
        }

        if (path.Length == 0 || path[0] != '/')
        {
            throw new IOException("not absolute: " + path);
        }

        resolved.Clear();
        var pending = new Stack<string>(path.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse());
        while (pending.Count > 0)
        {
            var part = pending.Pop();
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (resolved.Count > 0)
                {
                    resolved.RemoveAt(resolved.Count - 1);
                }

                continue;
            }

            resolved.Add(part);
            var here = "/" + string.Join('/', resolved);
            if (!_nodes.TryGetValue(here, out var node))
            {
                throw new FileNotFoundException(here);
            }

            if (node.Kind == FileKind.Symlink)
            {
                resolved.RemoveAt(resolved.Count - 1);
                var target = node.Target!;
                foreach (var p in target.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
                {
                    pending.Push(p);
                }

                if (target.StartsWith('/'))
                {
                    resolved.Clear();
                }

                if (++depth > 16)
                {
                    throw new IOException("too many links: " + path);
                }
            }
        }
    }

    private Node? Lookup(string path, bool follow)
    {
        if (!follow)
        {
            if (path == "/")
            {
                return _nodes["/"];
            }

            string realParent;
            try
            {
                realParent = RealPath(Dirname(path));
            }
            catch (IOException)
            {
                return null;
            }

            return _nodes.GetValueOrDefault((realParent == "/" ? string.Empty : realParent) + "/" + path[(path.LastIndexOf('/') + 1)..]);
        }

        try
        {
            return _nodes.GetValueOrDefault(RealPath(path));
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void Ensure(string dir)
    {
        for (var d = dir; d != "/" && !_nodes.ContainsKey(d); d = Dirname(d))
        {
            _nodes[d] = new Node(FileKind.Directory, 0, 0b111_101_101);
        }
    }

    private static string Dirname(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }
}
