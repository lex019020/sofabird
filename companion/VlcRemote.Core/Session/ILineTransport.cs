namespace VlcRemote.Core.Session;

/// <summary>
/// Транспорт «строка туда / строка сюда» (NDJSON). Реализации: WebSocket (M2), RFCOMM (M3),
/// in-memory для тестов. Одна строка = одно JSON-сообщение без переводов строк внутри.
/// </summary>
public interface ILineTransport : IAsyncDisposable
{
    /// <summary>Описание для логов, например <c>ws 192.168.1.5:51234</c>.</summary>
    string Description { get; }

    /// <summary>Прочитать следующую строку; <c>null</c> — соединение закрыто.</summary>
    Task<string?> ReadLineAsync(CancellationToken ct);

    /// <summary>Отправить строку (без завершающего <c>\n</c>; транспорт добавляет сам, если нужно).</summary>
    Task WriteLineAsync(string line, CancellationToken ct);

    /// <summary>Корректно закрыть соединение.</summary>
    Task CloseAsync(string? reason, CancellationToken ct);
}
