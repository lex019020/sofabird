using System.Globalization;
using Sofabird.Core.Localization;

namespace Sofabird.Core.Displays;

/// <summary>
/// Pure mapping from raw Win32 data (GDI screens + display-config targets) to <see cref="MonitorInfo"/>:
/// stable ids, readable names, internal/external kind. One entry per GDI source (clones share one).
/// </summary>
public static class MonitorTopology
{
    // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY values that matter here.
    public const uint OutputOther = 0xFFFFFFFF;
    public const uint OutputHdmi = 5;
    public const uint OutputLvds = 6;
    public const uint OutputDisplayPortExternal = 10;
    public const uint OutputDisplayPortEmbedded = 11;
    public const uint OutputUdiEmbedded = 13;
    public const uint OutputInternal = 0x80000000;

    public static MonitorKind KindOf(uint outputTechnology) => outputTechnology switch
    {
        OutputInternal or OutputDisplayPortEmbedded or OutputUdiEmbedded or OutputLvds => MonitorKind.Internal,
        OutputOther => MonitorKind.Unknown,
        _ => MonitorKind.External,
    };

    public static IReadOnlyList<MonitorInfo> Build(IEnumerable<GdiScreen> screens, IEnumerable<DisplayTarget> targets)
    {
        var bySource = targets
            .Where(t => !string.IsNullOrEmpty(t.SourceGdiName))
            .GroupBy(t => t.SourceGdiName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var ordered = screens
            .GroupBy(s => s.DeviceName, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(s => s.DeviceName, StringComparer.Ordinal)
            .ToList();

        var result = new List<MonitorInfo>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            bySource.TryGetValue(s.DeviceName, out var sourceTargets);
            // Clone mode: the external monitor (the TV) represents the source; ties by device path for determinism.
            var sorted = (sourceTargets ?? [])
                .OrderBy(t => KindOf(t.OutputTechnology) switch { MonitorKind.External => 0, MonitorKind.Unknown => 1, _ => 2 })
                .ThenBy(t => t.MonitorDevicePath ?? "", StringComparer.Ordinal)
                .ToList();
            var main = sorted.FirstOrDefault();
            var id = string.IsNullOrWhiteSpace(main?.MonitorDevicePath) ? s.DeviceName : main.MonitorDevicePath!;
            var kind = main is null ? MonitorKind.Unknown : KindOf(main.OutputTechnology);
            var names = sorted
                .Select(t => t.FriendlyName?.Trim())
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var number = DisplayNumber(s.DeviceName) ?? i + 1;

            LocalizableText? nameText = null;
            string name;
            if (names.Count > 0) name = string.Join(" + ", names);
            else
            {
                nameText = kind == MonitorKind.Internal ? ProtocolText.DisplayBuiltIn : ProtocolText.DisplayFallbackName(number);
                name = nameText.ToString();
            }

            result.Add(new MonitorInfo
            {
                Index = i,
                Id = id,
                Name = name,
                NameText = nameText,
                Kind = kind,
                Primary = s.Primary,
                Bounds = s.Bounds,
                WorkArea = s.WorkArea,
                GdiDeviceName = s.DeviceName,
            });
        }

        // Two identical monitors (same EDID name): add the display number so they can be told apart.
        var duplicates = result
            .Where(m => m.NameText is null)
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToHashSet();
        if (duplicates.Count == 0) return result;
        return result.Select(m => duplicates.Contains(m)
            ? new MonitorInfo
            {
                Index = m.Index, Id = m.Id, Kind = m.Kind, Primary = m.Primary, Bounds = m.Bounds, WorkArea = m.WorkArea,
                GdiDeviceName = m.GdiDeviceName,
                Name = string.Create(CultureInfo.InvariantCulture, $"{m.Name} ({DisplayNumber(m.GdiDeviceName) ?? m.Index + 1})"),
            }
            : m).ToList();
    }

    /// <summary><c>\\.\DISPLAY12</c> → 12.</summary>
    public static int? DisplayNumber(string gdiDeviceName)
    {
        var end = gdiDeviceName.Length;
        var start = end;
        while (start > 0 && char.IsAsciiDigit(gdiDeviceName[start - 1])) start--;
        return start < end && int.TryParse(gdiDeviceName.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n : null;
    }

    public static MonitorInfo? FindById(IReadOnlyList<MonitorInfo> monitors, string? id) =>
        string.IsNullOrEmpty(id) ? null : monitors.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    public static MonitorInfo? FindByGdi(IReadOnlyList<MonitorInfo> monitors, string? gdiDeviceName) =>
        string.IsNullOrEmpty(gdiDeviceName) ? null
            : monitors.FirstOrDefault(m => string.Equals(m.GdiDeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>One-line signature of the topology (to log it only when it changes).</summary>
    public static string Signature(IReadOnlyList<MonitorInfo> monitors) => string.Join("|", monitors.Select(m => m.ToString()));
}
