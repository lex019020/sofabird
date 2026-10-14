using System.Globalization;
using System.Resources;

namespace Sofabird.Core.Localization;

/// <summary>
/// A message key + arguments rendered later in a chosen culture: the PC builds errors without knowing who reads
/// them, and the session renders them in the phone's language. English (neutral resources) is the fallback.
/// </summary>
public sealed class LocalizableText
{
    private static readonly ResourceManager Rm = new("Sofabird.Core.Localization.ProtocolText", typeof(LocalizableText).Assembly);

    public LocalizableText(string key, params object[] args)
    {
        Key = key;
        Args = args;
    }

    public string Key { get; }
    public object[] Args { get; }

    public string Render(CultureInfo? culture)
    {
        var c = culture ?? CultureInfo.InvariantCulture;
        var template = Rm.GetString(Key, c) ?? Key;
        return Args.Length == 0 ? template : string.Format(c, template, Args);
    }

    /// <summary>English text (neutral resources).</summary>
    public override string ToString() => Render(CultureInfo.InvariantCulture);

    /// <summary>Phone locale tag (e.g. <c>ru-RU</c>) → culture; null/invalid → null (English).</summary>
    public static CultureInfo? CultureFromTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        try { return CultureInfo.GetCultureInfo(tag.Trim().Replace('_', '-')); }
        catch (CultureNotFoundException) { return null; }
    }
}
