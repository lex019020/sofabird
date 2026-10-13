using System.Globalization;
using VlcRemote.Core.Session;

namespace VlcRemote.Core.Vlc;

/// <summary>A request to VLC's HTTP interface: <c>status.json?command=…&amp;name=value</c>.</summary>
public sealed record VlcHttpCommand(string Command, params (string Name, string Value)[] Args);

/// <summary>
/// Protocol <c>cmd</c>s that translate into exactly one VLC HTTP command, without looking at VLC's state
/// (protocol/README.md "cmd", protocol/vlc-http.md). Commands that depend on the current status (play, mute, the
/// toggles, chapter by delta), on the playlist or on the process are handled by the companion's VLC manager.
/// </summary>
public static class VlcCommandMap
{
    /// <summary>The VLC command for <paramref name="name"/>; <c>null</c> — not a stateless command.</summary>
    /// <exception cref="ProtocolException">Missing or invalid arguments (<c>bad_request</c>).</exception>
    public static VlcHttpCommand? Map(string name, CommandArgs args) => name switch
    {
        "pause" => new("pl_forcepause"),
        "togglePause" => new("pl_pause"),
        "stop" => new("pl_stop"),
        "next" => new("pl_next"),
        "prev" => new("pl_previous"),
        "playlistClear" => new("pl_empty"),
        "seek" => Seek(args),
        "rate" => new("rate", ("val", Fraction(args.RequireDouble("value")))),
        "track" => new(TrackCommand(args.RequireString("kind")), ("val", Number(args.RequireInt("id")))),
        "title" => new("title", ("val", Number(args.RequireInt("index")))),
        "delay" => new(DelayCommand(args.RequireString("kind")), ("val", Fraction(args.RequireDouble("seconds")))),
        "aspect" => new("aspectratio", ("val", args.RequireString("value"))),
        "chapter" when args.GetInt("index") is int index => new("chapter", ("val", Number(index))),
        _ => null,
    };

    private static VlcHttpCommand Seek(CommandArgs args)
    {
        if (args.GetInt("seconds") is int seconds) return new("seek", ("val", Number(Math.Max(0, seconds))));
        if (args.GetInt("delta") is int delta) return new("seek", ("val", Relative(delta)));
        throw ProtocolException.BadRequest("seek requires 'seconds' or 'delta'");
    }

    private static string TrackCommand(string kind) => kind switch
    {
        "audio" => "audio_track",
        "subtitle" => "subtitle_track",
        "video" => "video_track",
        _ => throw ProtocolException.BadRequest("track.kind must be audio|subtitle|video"),
    };

    private static string DelayCommand(string kind) => kind switch
    {
        "audio" => "audiodelay",
        "subtitle" => "subdelay",
        _ => throw ProtocolException.BadRequest("delay.kind must be audio|subtitle"),
    };

    public static string Number(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>A relative value for VLC: <c>+30</c> / <c>-10</c>.</summary>
    public static string Relative(int v) => v >= 0 ? "+" + Number(v) : Number(v);

    public static string Fraction(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
