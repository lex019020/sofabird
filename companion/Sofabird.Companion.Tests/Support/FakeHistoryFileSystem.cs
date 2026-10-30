using Sofabird.Core.History;

namespace Sofabird.Companion.Tests.Support;

/// <summary>In-memory <see cref="IHistoryFileSystem"/>: the history file and the listings of series folders.</summary>
internal sealed class FakeHistoryFileSystem : IHistoryFileSystem
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _folders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Puts <paramref name="names"/> into <paramref name="directory"/> (<c>D:\Video\Show</c>).</summary>
    public void AddFolder(string directory, params string[] names)
    {
        lock (_gate) _folders[directory.TrimEnd('\\')] = [.. names];
    }

    public string? ReadAllText(string path)
    {
        lock (_gate) return _files.TryGetValue(path, out var t) ? t : null;
    }

    public void WriteAllTextAtomic(string path, string content)
    {
        lock (_gate) _files[path] = content;
    }

    public void Move(string source, string destination)
    {
        lock (_gate)
        {
            _files[destination] = _files[source];
            _files.Remove(source);
        }
    }

    public long? TryGetFileSize(string path) => null;

    public IReadOnlyList<string>? TryListFiles(string directory)
    {
        lock (_gate) return _folders.TryGetValue(directory.TrimEnd('\\'), out var f) ? f.ToList() : null;
    }
}
