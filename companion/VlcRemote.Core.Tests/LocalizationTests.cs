using System.Globalization;
using VlcRemote.Core.Localization;
using VlcRemote.Core.Protocol;
using VlcRemote.Core.Session;
using Xunit;

namespace VlcRemote.Core.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void Renders_in_phone_language_with_english_fallback()
    {
        var text = ProtocolText.VlcExitedImmediately(3);
        Assert.Equal("VLC exited right after starting (code 3). One-instance mode may be on.", text.Render(null));
        Assert.Equal("VLC завершился сразу после запуска (код 3). Возможно, включён режим одного экземпляра.", text.Render(CultureInfo.GetCultureInfo("ru-RU")));
        Assert.Equal(text.Render(null), text.Render(CultureInfo.GetCultureInfo("de-DE")));
        Assert.Equal(text.Render(null), text.ToString());
    }

    [Theory]
    [InlineData("ru-RU", "ru-RU")]
    [InlineData("ru_RU", "ru-RU")]
    [InlineData("en", "en")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("not a tag!!", null)]
    public void Parses_phone_locale_tags(string? tag, string? expected)
    {
        Assert.Equal(expected, LocalizableText.CultureFromTag(tag)?.Name);
    }

    [Fact]
    public void Error_message_uses_session_culture()
    {
        var ex = ProtocolException.VlcError(ProtocolText.VlcWindowNotFound);
        Assert.Equal("VLC window not found", ex.Message);
        Assert.Equal("Окно VLC не найдено", Session.Session.MapError(ex, "7", CultureInfo.GetCultureInfo("ru")).Msg);
        Assert.Equal("VLC window not found", Session.Session.MapError(ex, "7").Msg);
        Assert.Equal(ErrorCodes.VlcError, Session.Session.MapError(ex, "7").Code);
    }

    [Fact]
    public void Plain_string_errors_are_passed_through()
    {
        var ex = ProtocolException.BadRequest("seek requires 'seconds' or 'delta'");
        Assert.Equal("seek requires 'seconds' or 'delta'", Session.Session.MapError(ex, null, CultureInfo.GetCultureInfo("ru")).Msg);
    }

    [Fact]
    public void Hello_device_locale_round_trips()
    {
        var json = """{"t":"hello","v":1,"device":{"name":"S24","id":"x","locale":"ru-RU"},"token":null,"pairCode":null}""";
        var hello = Assert.IsType<HelloMessage>(ProtocolJson.Deserialize(json));
        Assert.Equal("ru-RU", hello.Device?.Locale);
    }
}
