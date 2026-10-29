using System.Text.Json;

namespace Sofabird.Core.Power;

/// <summary>
/// A network adapter that can wake the PC with a magic packet: whether its "Wake on Magic Packet" is on, whether
/// Windows lets it wake the computer (<c>powercfg /devicequery wake_armed</c>) and whether it can at all — its driver
/// and the firmware let it wake the PC from sleep (<c>wake_from_any</c>; a laptop's Realtek card with Windows' own
/// driver could only "wake" a running PC, and the setup window said "ready").
/// </summary>
/// <param name="Name">The adapter's name in Windows ("Ethernet").</param>
/// <param name="Description">Its device name ("Realtek Gaming 2.5GbE Family Controller"), as powercfg knows it.</param>
/// <param name="Mac"><c>AA:BB:CC:DD:EE:FF</c>.</param>
/// <param name="Connected">It is up now: only an adapter on the network hears the packet.</param>
public sealed record WakeAdapter(string Name, string Description, string Mac, bool Wired, bool MagicPacket, bool Armed,
    bool Connected = true, bool CanWake = true)
{
    public bool Ready => CanWake && MagicPacket && Armed;
}

/// <summary>The setup window's "Wake this PC from the phone" item.</summary>
public enum WakeState
{
    /// <summary>No connected network adapter of this PC can wake it with a magic packet.</summary>
    Unsupported,
    /// <summary>An adapter can, but its "Wake on Magic Packet" is off or it may not wake the PC.</summary>
    NeedsSetup,
    Ready,
}

/// <summary>Wake-on-LAN: what the adapters allow and what to turn on (pure; the reading is done by the companion).</summary>
public static class WakeOnLan
{
    /// <summary>
    /// PowerShell that lists the physical adapters with a "Wake on Magic Packet" setting as JSON (no administrator
    /// rights needed): <c>name</c>, <c>description</c>, <c>mac</c>, <c>wired</c>, <c>magicPacket</c>.
    /// </summary>
    public const string InspectScript =
        "$ErrorActionPreference='SilentlyContinue';$ProgressPreference='SilentlyContinue';" +
        "$a=Get-NetAdapter -Physical|ForEach-Object{" +
        "$p=Get-NetAdapterAdvancedProperty -Name $_.Name -RegistryKeyword '*WakeOnMagicPacket';" +
        "if($p){[pscustomobject]@{name=$_.Name;description=$_.InterfaceDescription;mac=$_.MacAddress;" +
        "wired=($_.PhysicalMediaType -eq '802.3');connected=($_.Status -eq 'Up');" +
        "magicPacket=([string]($p.RegistryValue|Select-Object -First 1) -eq '1')}}};" +
        "ConvertTo-Json -Compress -InputObject @($a)";

    /// <summary>
    /// Ready when a connected adapter that can wake the PC is set to; unsupported when no connected adapter can (none
    /// at all, or its driver or firmware does not let it: <see cref="Blocked"/>).
    /// </summary>
    public static WakeState State(IReadOnlyList<WakeAdapter> adapters)
    {
        var usable = adapters.Where(a => a.Connected && a.CanWake).ToList();
        return usable.Count == 0 ? WakeState.Unsupported
            : usable.Any(a => a.Ready) ? WakeState.Ready
            : WakeState.NeedsSetup;
    }

    /// <summary>Connected adapters whose driver or firmware does not let them wake the PC from sleep (what to tell the user).</summary>
    public static IReadOnlyList<WakeAdapter> Blocked(IReadOnlyList<WakeAdapter> adapters) =>
        adapters.Where(a => a.Connected && !a.CanWake).ToList();

    /// <summary>The adapters to turn on (administrator rights): "Wake on Magic Packet" and arming in powercfg.</summary>
    public static IReadOnlyList<WakeAdapter> ToFix(IReadOnlyList<WakeAdapter> adapters) => adapters.Where(a => a.CanWake && !a.Ready).ToList();

    /// <summary>What phones get as <c>macs</c>: the connected adapters that will wake the PC, wired first.</summary>
    public static IReadOnlyList<string> WakeMacs(IReadOnlyList<WakeAdapter> adapters) =>
        adapters.Where(a => a.Connected && a.Ready).Select(a => a.Mac).Distinct().ToList();

    /// <summary>
    /// The output of <see cref="InspectScript"/>, of <c>powercfg /devicequery wake_armed</c> and of <c>wake_from_any</c>
    /// (one device per line; <c>null</c> — not known: every adapter is taken as able to wake).
    /// </summary>
    public static IReadOnlyList<WakeAdapter> Parse(string? adaptersJson, string? armedDevices, string? wakeCapableDevices = null)
    {
        var armed = Devices(armedDevices);
        var capable = wakeCapableDevices is null ? null : Devices(wakeCapableDevices);
        var result = new List<WakeAdapter>();
        // PowerShell may print something before the JSON (a module loading notice).
        var start = adaptersJson?.IndexOfAny(['[', '{']) ?? -1;
        if (start < 0) return result;
        try
        {
            using var doc = JsonDocument.Parse(adaptersJson![start..]);
            // ConvertTo-Json of an empty array is "[]", of a single object an array too (-InputObject @(...)).
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
            foreach (var e in items)
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var name = Str(e, "name");
                var mac = NormalizeMac(Str(e, "mac"));
                if (name is null || mac is null) continue;
                var description = Str(e, "description") ?? name;
                var connected = !e.TryGetProperty("connected", out var up) || up.ValueKind != JsonValueKind.False;
                result.Add(new WakeAdapter(name, description, mac, Bool(e, "wired"), Bool(e, "magicPacket"), armed.Contains(description),
                    connected, capable?.Contains(description) ?? true));
            }
        }
        catch (JsonException)
        {
            // nothing readable: no adapter known
        }
        // Wired adapters first: those wake reliably; Wi-Fi only on some PCs.
        return result.OrderByDescending(a => a.Wired).ToList();
    }

    /// <summary>
    /// PowerShell (run elevated) that turns waking on for <paramref name="adapters"/>: "Wake on Magic Packet" (the
    /// adapter restarts, the network drops for a few seconds) and "Allow this device to wake the computer".
    /// </summary>
    public static string FixScript(IEnumerable<string> adapters)
    {
        var names = string.Join(",", adapters.Select(n => "'" + n.Replace("'", "''", StringComparison.Ordinal) + "'"));
        return "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';" +
            $"foreach($n in @({names})){{" +
            "$a=Get-NetAdapter -Name $n;" +
            "Set-NetAdapterAdvancedProperty -Name $n -RegistryKeyword '*WakeOnMagicPacket' -RegistryValue 1;" +
            "powercfg /deviceenablewake \"$($a.InterfaceDescription)\";" +
            "if($LASTEXITCODE -ne 0){throw \"powercfg: $LASTEXITCODE\"}}";
    }

    /// <summary><c>AA:BB:CC:DD:EE:FF</c> from "AA-BB-CC-DD-EE-FF", "aabbccddeeff" and the like; <c>null</c> — not a MAC.</summary>
    public static string? NormalizeMac(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var hex = new string(text.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length != 12 || text.Count(char.IsLetterOrDigit) != 12) return null;
        if (hex.All(c => c == '0') || hex.All(c => c == 'F')) return null;
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    private static HashSet<string> Devices(string? lines) => new(
        (lines ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        StringComparer.OrdinalIgnoreCase);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
