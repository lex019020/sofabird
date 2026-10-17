using System.Globalization;
using Sofabird.Core.YouTube;
using Xunit;

namespace Sofabird.Core.Tests;

public class FirefoxDownloadTests
{
    [Theory]
    [InlineData("en-US", "en-US")]
    [InlineData("en-GB", "en-GB")]
    [InlineData("en-AU", "en-US")]
    [InlineData("ru-RU", "ru")]
    [InlineData("sr-Latn-RS", "sr")]
    [InlineData("de-AT", "de")]
    [InlineData("pt-PT", "pt-PT")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("es-ES", "es-ES")]
    [InlineData("es-MX", "es-MX")]
    [InlineData("es-CO", "es-MX")]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh-TW", "zh-TW")]
    [InlineData("sv-SE", "sv-SE")]
    [InlineData("nb-NO", "nb-NO")]
    [InlineData("sw-KE", "en-US")]
    [InlineData("", "en-US")]
    public void Windows_language_to_firefox_locale(string culture, string locale) =>
        Assert.Equal(locale, FirefoxDownload.Locale(CultureInfo.GetCultureInfo(culture)));

    [Fact]
    public void The_url_asks_mozilla_for_the_latest_64_bit_installer() =>
        Assert.Equal("https://download.mozilla.org/?product=firefox-latest-ssl&os=win64&lang=ru",
            FirefoxDownload.Url(CultureInfo.GetCultureInfo("ru-RU")).ToString());
}
