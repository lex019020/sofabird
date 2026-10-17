using System.Globalization;

namespace Sofabird.Core.YouTube;

/// <summary>Where the companion downloads Firefox for the TV from: Mozilla's own download service, in Windows' language.</summary>
public static class FirefoxDownload
{
    /// <summary>The publisher every Firefox installer is signed by (Authenticode).</summary>
    public const string Publisher = "Mozilla Corporation";

    /// <summary>The latest release, 64-bit full installer, in <paramref name="culture"/>'s language.</summary>
    public static Uri Url(CultureInfo culture) =>
        new($"https://download.mozilla.org/?product=firefox-latest-ssl&os=win64&lang={Locale(culture)}");

    /// <summary>
    /// Firefox's locale code for a Windows culture: mostly the language alone ("de", "ru", "ja"), a region where Firefox
    /// ships several ("pt-BR"/"pt-PT", "zh-CN"/"zh-TW", "es-ES"/"es-MX"…), English by default.
    /// </summary>
    public static string Locale(CultureInfo culture)
    {
        var name = culture.Name;
        var language = culture.TwoLetterISOLanguageName;
        return language switch
        {
            "en" => name is "en-GB" or "en-CA" ? name : "en-US",
            "pt" => name == "pt-PT" ? "pt-PT" : "pt-BR",
            "es" => name switch
            {
                "es-AR" or "es-CL" or "es-MX" => name,
                _ when culture.Parent.Name == "es" && name != "es-ES" && name.Length > 2 => "es-MX",
                _ => "es-ES",
            },
            "zh" => name.Contains("Hant", StringComparison.Ordinal) || name is "zh-TW" or "zh-HK" or "zh-MO" ? "zh-TW" : "zh-CN",
            "sv" => "sv-SE",
            "nb" or "no" => "nb-NO",
            "nn" => "nn-NO",
            "ga" => "ga-IE",
            "hy" => "hy-AM",
            "pa" => "pa-IN",
            "gu" => "gu-IN",
            "hi" => "hi-IN",
            "ne" => "ne-NP",
            "bn" or "de" or "fr" or "it" or "ru" or "uk" or "pl" or "cs" or "sk" or "nl" or "da" or "fi" or "hu" or "ro" or "bg"
                or "el" or "tr" or "ja" or "ko" or "he" or "ar" or "fa" or "th" or "vi" or "id" or "ms" or "lt" or "lv" or "et"
                or "sl" or "sr" or "hr" or "bs" or "mk" or "sq" or "be" or "kk" or "ka" or "az" or "uz" or "ca" or "eu" or "gl"
                or "is" or "cy" or "af" or "ta" or "te" or "kn" or "ml" or "mr" or "ur" or "km" or "lo" or "my" or "si" => language,
            _ => "en-US",
        };
    }
}
