namespace Sofabird.Core.Library;

/// <summary>Why the library is being scanned.</summary>
public enum LibraryScanReason
{
    /// <summary>The companion started, or the media folders changed.</summary>
    FoldersChanged,
    /// <summary>The 30-minute timer.</summary>
    Periodic,
    /// <summary>A phone asked for the library (someone is about to watch).</summary>
    PhoneAsked,
}

/// <summary>
/// Which media folders a scan reads. A network folder lives on another computer that may be asleep, and reading it
/// wakes that computer (Windows wakes on directed network traffic), so it is never read by the timer: only at the
/// start, when the folders change, and when a phone asks for the library and the last scan is older than
/// <see cref="NetworkStale"/>. Local folders are read every time.
/// </summary>
public static class LibraryRescanPolicy
{
    public static readonly TimeSpan NetworkStale = TimeSpan.FromMinutes(30);

    public static bool ShouldScan(bool network, LibraryScanReason reason, DateTimeOffset? lastScan, DateTimeOffset now) =>
        lastScan is null || !network || reason switch
        {
            LibraryScanReason.FoldersChanged => true,
            LibraryScanReason.PhoneAsked => now - lastScan.Value >= NetworkStale,
            _ => false,
        };

    /// <summary>A network path (<c>\\server\share</c>) or a folder on a network drive letter.</summary>
    public static bool IsNetwork(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        if (path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0])) return false;
        try
        {
            return new DriveInfo(path[..1]).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
