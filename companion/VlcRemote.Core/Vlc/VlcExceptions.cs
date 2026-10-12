namespace VlcRemote.Core.Vlc;

/// <summary>Базовая ошибка общения с VLC (HTTP или RC).</summary>
public class VlcException : Exception
{
    public VlcException(string message) : base(message) { }
    public VlcException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>Нет TCP-соединения: интерфейс выключен, VLC не запущен или ещё стартует.</summary>
public sealed class VlcUnavailableException : VlcException
{
    public VlcUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>401/403 — неверный пароль или пароль не задан.</summary>
public sealed class VlcAuthException : VlcException
{
    public int StatusCode { get; }
    public VlcAuthException(int statusCode, string message) : base(message) => StatusCode = statusCode;
}

/// <summary>404 — страницы нет (например, <c>tracks.json</c> не установлен → уровень 0).</summary>
public sealed class VlcNotFoundException : VlcException
{
    public VlcNotFoundException(string message) : base(message) { }
}

/// <summary>Lua вернул HTML с <c>&lt;title&gt;Error loading</c> (200 OK, но ошибка).</summary>
public sealed class VlcLuaException : VlcException
{
    public VlcLuaException(string message) : base(message) { }
}

/// <summary>Пустой ответ — интерфейс VLC ещё поднимается; повторить позже.</summary>
public sealed class VlcStartingException : VlcException
{
    public VlcStartingException() : base("VLC HTTP interface returned an empty response (still starting?)") { }
}

/// <summary>Прочие неожиданные HTTP-коды.</summary>
public sealed class VlcHttpException : VlcException
{
    public int StatusCode { get; }
    public VlcHttpException(int statusCode, string message) : base(message) => StatusCode = statusCode;
}

/// <summary>Ошибка RC-интерфейса (неизвестная команда, таймаут ответа, отказ в паузе).</summary>
public class VlcRcException : VlcException
{
    public VlcRcException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>RC отказал, потому что VLC на паузе («Press pause to continue.»).</summary>
public sealed class VlcRcPausedException : VlcRcException
{
    public VlcRcPausedException() : base("RC refuses commands while VLC is paused") { }
}
