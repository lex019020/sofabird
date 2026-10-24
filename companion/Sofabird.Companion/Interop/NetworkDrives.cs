using System.Runtime.InteropServices;

namespace Sofabird.Companion.Interop;

/// <summary>
/// Network drive letters (<c>Y:</c> → <c>\\server\share</c>). A letter mapped without "Reconnect at sign-in" disappears
/// after a restart, so media folders on such a drive are kept as network paths instead.
/// </summary>
internal static class NetworkDrives
{
    private const int NO_ERROR = 0;

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetGetConnectionW")]
    private static extern int WNetGetConnection(string localName, [Out] char[] remoteName, ref int length);

    /// <summary>
    /// <paramref name="path"/> with a mapped drive letter replaced by its network path (<c>Y:\Movies</c> →
    /// <c>\\server\share\Movies</c>); any other path, or a letter that is not mapped now, as it is.
    /// </summary>
    public static string ToNetworkPath(string path)
    {
        if (path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0])) return path;
        var buffer = new char[1024];
        var length = buffer.Length;
        if (WNetGetConnection(path[..2], buffer, ref length) != NO_ERROR) return path;
        var remote = new string(buffer, 0, Array.IndexOf(buffer, '\0') is var end and >= 0 ? end : buffer.Length);
        if (remote.Length == 0) return path;
        var rest = path.Length > 2 ? path[2..].TrimStart('\\') : "";
        return rest.Length == 0 ? remote.TrimEnd('\\') + "\\" : Path.Combine(remote, rest);
    }
}
