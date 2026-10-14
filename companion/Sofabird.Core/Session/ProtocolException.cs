using System.Globalization;
using Sofabird.Core.Localization;
using Sofabird.Core.Protocol;

namespace Sofabird.Core.Session;

/// <summary>
/// An error sent to the phone as <c>err</c> with the given code. When created from a <see cref="LocalizableText"/>,
/// the message is rendered in the phone's language by the session; <see cref="Exception.Message"/> is English.
/// </summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string code, string message) : base(message) => Code = code;

    public ProtocolException(string code, LocalizableText text) : base(text.ToString())
    {
        Code = code;
        Text = text;
    }

    public string Code { get; }

    /// <summary>Localizable message, if any.</summary>
    public LocalizableText? Text { get; }

    public string MessageFor(CultureInfo? culture) => Text?.Render(culture) ?? Message;

    public static ProtocolException BadRequest(string message) => new(ErrorCodes.BadRequest, message);
    public static ProtocolException BadRequest(LocalizableText text) => new(ErrorCodes.BadRequest, text);
    public static ProtocolException Unsupported(string message) => new(ErrorCodes.Unsupported, message);
    public static ProtocolException VlcNotRunning(string message = "VLC is not running") => new(ErrorCodes.VlcNotRunning, message);
    public static ProtocolException VlcNotRunning(LocalizableText text) => new(ErrorCodes.VlcNotRunning, text);
    public static ProtocolException VlcError(string message) => new(ErrorCodes.VlcError, message);
    public static ProtocolException VlcError(LocalizableText text) => new(ErrorCodes.VlcError, text);
    public static ProtocolException Unauthorized(string message) => new(ErrorCodes.Unauthorized, message);
}
