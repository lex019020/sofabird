using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sofabird.Core.YouTube.Cast;

/// <summary>Who the companion is to the lounge service: a "screen" (what a TV's YouTube app is).</summary>
/// <param name="DeviceId">Stable id of this screen device (a GUID kept in the settings).</param>
/// <param name="Name">The name phones show in the cast list (the PC's name).</param>
public sealed record LoungeScreen(string DeviceId, string Name)
{
    /// <summary>The screen app; any name works, YouTube's own TV app would say <c>lb-v4</c>.</summary>
    public const string App = "sofabird";
    /// <summary>The YouTube app's theme (YouTube Music is <c>m</c>).</summary>
    public const string Theme = "cl";
}

/// <summary>An outgoing lounge message: a name and its fields (sent as strings).</summary>
public sealed record LoungeCommand(string Name, IReadOnlyList<KeyValuePair<string, string>> Fields)
{
    public LoungeCommand(string name) : this(name, []) { }
}

/// <summary>
/// The state of one lounge channel (Google's "browser channel" under <c>/api/lounge/bc/bind</c>): the session ids from
/// the first response (<c>c</c> → SID, <c>S</c> → gsessionid), the request counter (RID), the last array id received
/// (AID, acknowledged with every request) and the count of messages sent (<c>ofs</c>). Builds the query strings and the
/// form of each request; the HTTP is the caller's.
/// </summary>
public sealed class LoungeChannel
{
    public const string BaseUrl = "https://www.youtube.com/api/lounge";
    public const string BindUrl = BaseUrl + "/bc/bind";

    private static readonly string DeviceInfo = JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["brand"] = "Sofabird",
        ["model"] = "Windows PC",
        ["year"] = 0,
        ["os"] = "Windows",
        ["osVersion"] = "10.0",
        ["chipset"] = "",
        ["clientName"] = "TVHTML5",
        ["dialAdditionalDataSupportLevel"] = "unsupported",
        ["mdxDialServerType"] = "MDX_DIAL_SERVER_TYPE_UNKNOWN",
    });

    private readonly LoungeScreen _screen;
    private readonly Random _random;

    public LoungeChannel(LoungeScreen screen, Random? random = null)
    {
        _screen = screen;
        _random = random ?? Random.Shared;
        Reset();
    }

    public string? Sid { get; private set; }
    public string? Gsessionid { get; private set; }
    public long LastAid { get; private set; }
    public int Rid { get; private set; }
    public int Ofs { get; private set; }

    /// <summary>The session is established: messages can be sent and received.</summary>
    public bool Bound => Sid is not null && Gsessionid is not null;

    /// <summary>Forgets the session (a new one starts with a fresh request counter).</summary>
    public void Reset()
    {
        Sid = null;
        Gsessionid = null;
        LastAid = -1;
        Ofs = 0;
        Rid = _random.Next(41000, 50000);
    }

    /// <summary>Takes the session ids and the array id of a received message.</summary>
    public void Observe(LoungeMessage message)
    {
        if (message.Aid > LastAid) LastAid = message.Aid;
        if (message.Name == "c" && message.Arg(0) is { Length: > 0 } sid) Sid = sid;
        else if (message.Name == "S" && message.Arg(0) is { Length: > 0 } gsid) Gsessionid = gsid;
    }

    /// <summary>The first request of a session (a <c>POST</c> with <c>count=0</c>).</summary>
    public string InitUrl(string loungeToken)
    {
        var q = Common(loungeToken);
        q.Add(new("deviceInfo", DeviceInfo));
        q.Add(new("RID", Next()));
        q.Add(new("CVER", "1"));
        return BindUrl + "?" + Encode(q);
    }

    /// <summary>A request that sends messages (a <c>POST</c> of <see cref="Form"/>).</summary>
    public string SendUrl(string loungeToken)
    {
        var q = Common(loungeToken);
        q.Add(new("deviceInfo", DeviceInfo));
        q.Add(new("SID", Sid ?? ""));
        q.Add(new("RID", Next()));
        q.Add(new("AID", LastAid.ToString(CultureInfo.InvariantCulture)));
        q.Add(new("gsessionid", Gsessionid ?? ""));
        return BindUrl + "?" + Encode(q);
    }

    /// <summary>The long poll that streams the messages for the screen (a <c>GET</c>).</summary>
    public string PollUrl(string loungeToken)
    {
        var q = Common(loungeToken);
        q.Add(new("RID", "rpc"));
        q.Add(new("SID", Sid ?? ""));
        q.Add(new("CI", "0"));
        q.Add(new("AID", LastAid.ToString(CultureInfo.InvariantCulture)));
        q.Add(new("gsessionid", Gsessionid ?? ""));
        q.Add(new("TYPE", "xmlhttp"));
        return BindUrl + "?" + Encode(q);
    }

    /// <summary>The form that sends <paramref name="commands"/> (<c>count</c>, <c>ofs</c>, <c>req0__sc</c>, <c>req0_key</c>…).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Form(IReadOnlyList<LoungeCommand> commands)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("count", commands.Count.ToString(CultureInfo.InvariantCulture)),
            new("ofs", Ofs.ToString(CultureInfo.InvariantCulture)),
        };
        for (var i = 0; i < commands.Count; i++)
        {
            form.Add(new($"req{i}__sc", commands[i].Name));
            foreach (var (key, value) in commands[i].Fields) form.Add(new($"req{i}_{key}", value));
        }
        Ofs += commands.Count;
        return form;
    }

    private List<KeyValuePair<string, string>> Common(string loungeToken) =>
    [
        new("device", "LOUNGE_SCREEN"),
        new("id", _screen.DeviceId),
        new("obfuscatedGaiaId", ""),
        new("name", _screen.Name),
        new("app", LoungeScreen.App),
        new("theme", LoungeScreen.Theme),
        new("capabilities", "dsp,mic,dpa,ntb"),
        new("cst", "m"),
        new("mdxVersion", "2"),
        new("loungeIdToken", loungeToken),
        new("VER", "8"),
        new("v", "2"),
        new("zx", Zx()),
        new("t", "1"),
    ];

    private string Next() => (Rid++).ToString(CultureInfo.InvariantCulture);

    /// <summary>A random cache buster, as YouTube's own clients send.</summary>
    private string Zx()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var sb = new StringBuilder(12);
        for (var i = 0; i < 12; i++) sb.Append(chars[_random.Next(chars.Length)]);
        return sb.ToString();
    }

    internal static string Encode(IEnumerable<KeyValuePair<string, string>> pairs) =>
        string.Join("&", pairs.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
}
