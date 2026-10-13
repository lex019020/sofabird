namespace VlcRemote.Core.History;

/// <summary>File system access of the watch history (replaced by an in-memory fake in tests).</summary>
public interface IHistoryFileSystem
{
    /// <summary>Text of the file, or <c>null</c> when it does not exist.</summary>
    string? ReadAllText(string path);
    /// <summary>Write via a temporary file and an atomic replace, so a crash never leaves a half-written file.</summary>
    void WriteAllTextAtomic(string path, string content);
    /// <summary>Rename a file (overwriting the target); used to keep a corrupt history aside.</summary>
    void Move(string from, string to);
    /// <summary>Size of a media file in bytes; <c>null</c> when unknown or not reachable.</summary>
    long? TryGetFileSize(string path);
    /// <summary>File names (not paths) of a folder; <c>null</c> when it is not reachable.</summary>
    IReadOnlyList<string>? TryListFiles(string directory);
}

/// <summary><see cref="IHistoryFileSystem"/> over System.IO.</summary>
public sealed class PhysicalHistoryFileSystem : IHistoryFileSystem
{
    public static PhysicalHistoryFileSystem Instance { get; } = new();

    public string? ReadAllText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    public void WriteAllTextAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
        {
            w.Write(content);
            w.Flush();
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public void Move(string from, string to) => File.Move(from, to, overwrite: true);

    public long? TryGetFileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public IReadOnlyList<string>? TryListFiles(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return null;
            return Directory.EnumerateFiles(directory).Select(Path.GetFileName).OfType<string>().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
