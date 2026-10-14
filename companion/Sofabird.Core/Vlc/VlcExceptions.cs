namespace Sofabird.Core.Vlc;

/// <summary>Base error of communicating with VLC (HTTP or RC).</summary>
public class VlcException : Exception
{
    public VlcException(string message) : base(message) { }
    public VlcException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>No TCP connection: the interface is off, VLC is not running or is still starting.</summary>
public sealed class VlcUnavailableException : VlcException
{
    public VlcUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>401/403 — wrong password or no password set.</summary>
public sealed class VlcAuthException : VlcException
{
    public int StatusCode { get; }
    public VlcAuthException(int statusCode, string message) : base(message) => StatusCode = statusCode;
}

/// <summary>404 — no such page (for example, <c>tracks.json</c> is not installed → level 0).</summary>
public sealed class VlcNotFoundException : VlcException
{
    public VlcNotFoundException(string message) : base(message) { }
}

/// <summary>Lua returned HTML with <c>&lt;title&gt;Error loading</c> (200 OK, but an error).</summary>
public sealed class VlcLuaException : VlcException
{
    public VlcLuaException(string message) : base(message) { }
}

/// <summary>Empty response — the VLC interface is still coming up; retry later.</summary>
public sealed class VlcStartingException : VlcException
{
    public VlcStartingException() : base("VLC HTTP interface returned an empty response (still starting?)") { }
}

/// <summary>Other unexpected HTTP codes.</summary>
public sealed class VlcHttpException : VlcException
{
    public int StatusCode { get; }
    public VlcHttpException(int statusCode, string message) : base(message) => StatusCode = statusCode;
}

/// <summary>RC interface error (unknown command, response timeout, refusal while paused).</summary>
public class VlcRcException : VlcException
{
    public VlcRcException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>RC refused because VLC is paused ("Press pause to continue.").</summary>
public sealed class VlcRcPausedException : VlcRcException
{
    public VlcRcPausedException() : base("RC refuses commands while VLC is paused") { }
}
