using VlcRemote.Core.Protocol;

namespace VlcRemote.Core.Session;

/// <summary>Ошибка, которая уходит телефону как <c>err</c> с заданным кодом.</summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string code, string message) : base(message) => Code = code;

    public string Code { get; }

    public static ProtocolException BadRequest(string message) => new(ErrorCodes.BadRequest, message);
    public static ProtocolException Unsupported(string message) => new(ErrorCodes.Unsupported, message);
    public static ProtocolException VlcNotRunning(string message = "VLC is not running") => new(ErrorCodes.VlcNotRunning, message);
    public static ProtocolException VlcError(string message) => new(ErrorCodes.VlcError, message);
    public static ProtocolException Unauthorized(string message) => new(ErrorCodes.Unauthorized, message);
}
