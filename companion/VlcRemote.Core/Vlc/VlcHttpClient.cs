using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VlcRemote.Core.Vlc;

/// <summary>
/// Client of the VLC 3.0.x HTTP interface (<c>/requests/*.json</c>, <c>/art</c>).
/// Basic auth with an empty user name; <see cref="HttpClient"/> on HTTP/1.1 does not send the
/// <c>Connection</c> header, so VLC's keep-alive works. The VLC server is single-threaded — all requests
/// are serialized through a semaphore.
/// </summary>
public sealed class VlcHttpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _log;

    public VlcHttpClient(string host, int port, string password, HttpMessageHandler? handler = null, ILogger? logger = null)
    {
        Host = host;
        Port = port;
        _log = logger ?? NullLogger.Instance;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.BaseAddress = new Uri($"http://{host}:{port}/");
        _http.Timeout = TimeSpan.FromSeconds(8);
        _http.DefaultRequestVersion = HttpVersion.Version11;
        _http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        SetPassword(password);
    }

    public string Host { get; }
    public int Port { get; }

    public void SetPassword(string password)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + password));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    public Task<VlcStatus> GetStatusAsync(CancellationToken ct = default) =>
        StatusRequestAsync("requests/status.json", ct);

    /// <summary>
    /// Execute a <c>status.json?command=…</c> command. The response is the fresh status.
    /// Parameters are URL-encoded (<c>+</c> → <c>%2B</c>).
    /// </summary>
    public Task<VlcStatus> CommandAsync(string command, CancellationToken ct = default, params (string Name, string Value)[] args)
    {
        var sb = new StringBuilder("requests/status.json?command=").Append(Uri.EscapeDataString(command));
        foreach (var (name, value) in args)
            sb.Append('&').Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
        return StatusRequestAsync(sb.ToString(), ct);
    }

    private async Task<VlcStatus> StatusRequestAsync(string path, CancellationToken ct)
    {
        var json = await GetTextAsync(path, ct).ConfigureAwait(false);
        return VlcJsonParser.ParseStatus(json);
    }

    public async Task<IReadOnlyList<VlcPlaylistItem>> GetPlaylistAsync(CancellationToken ct = default)
    {
        var json = await GetTextAsync("requests/playlist.json", ct).ConfigureAwait(false);
        return VlcJsonParser.ParsePlaylist(json);
    }

    public async Task<IReadOnlyList<VlcBrowseEntry>> BrowseAsync(string uri, CancellationToken ct = default)
    {
        var json = await GetTextAsync("requests/browse.json?uri=" + Uri.EscapeDataString(uri), ct).ConfigureAwait(false);
        return VlcJsonParser.ParseBrowse(json);
    }

    /// <summary>Our Lua page; <c>null</c> if it is not installed (404 → level 0).</summary>
    public async Task<VlcTracksPage?> GetTracksAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await GetTextAsync("requests/tracks.json", ct).ConfigureAwait(false);
            return VlcJsonParser.ParseTracksPage(json);
        }
        catch (VlcNotFoundException)
        {
            return null;
        }
    }

    public async Task<VlcSetVarResult> SetVarAsync(string obj, string name, string value, CancellationToken ct = default)
    {
        var path = $"requests/setvar.json?obj={Uri.EscapeDataString(obj)}&name={Uri.EscapeDataString(name)}&val={Uri.EscapeDataString(value)}";
        var json = await GetTextAsync(path, ct).ConfigureAwait(false);
        return VlcJsonParser.ParseSetVar(json);
    }

    /// <summary>Artwork of the current item (or of item <paramref name="plId"/>); <c>null</c> if there is none.</summary>
    public async Task<VlcArt?> GetArtAsync(int? plId = null, CancellationToken ct = default)
    {
        var path = plId is int id ? $"art?item={id}" : "art";
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var resp = await _http.GetAsync(path, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            ThrowIfAuthError(resp);
            if (!resp.IsSuccessStatusCode)
                throw new VlcHttpException((int)resp.StatusCode, $"GET /{path} → {(int)resp.StatusCode}");
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length == 0) return null;
            return new VlcArt { Data = bytes, ContentType = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg" };
        }
        catch (HttpRequestException ex)
        {
            throw new VlcUnavailableException($"VLC HTTP {Host}:{Port} unavailable: {ex.Message}", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> GetTextAsync(string path, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var resp = await _http.GetAsync(path, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            ThrowIfAuthError(resp);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                throw new VlcNotFoundException($"GET /{path} → 404");
            if (!resp.IsSuccessStatusCode)
                throw new VlcHttpException((int)resp.StatusCode, $"GET /{path} → {(int)resp.StatusCode}");

            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
                throw new VlcStartingException();

            var trimmed = text.AsSpan().TrimStart();
            if (trimmed.Length > 0 && trimmed[0] == '<')
            {
                // Lua errors arrive as 200 with an HTML body.
                var message = text.Contains("<title>Error loading", StringComparison.OrdinalIgnoreCase)
                    ? ExtractLuaError(text)
                    : "VLC returned HTML instead of JSON";
                throw new VlcLuaException(message);
            }
            return text;
        }
        catch (HttpRequestException ex)
        {
            throw new VlcUnavailableException($"VLC HTTP {Host}:{Port} unavailable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new VlcUnavailableException($"VLC HTTP {Host}:{Port} timed out", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ThrowIfAuthError(HttpResponseMessage resp)
    {
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new VlcAuthException(401, "VLC HTTP: wrong password (401)");
        if (resp.StatusCode == HttpStatusCode.Forbidden)
            throw new VlcAuthException(403, "VLC HTTP: password is not set in VLC (403)");
    }

    private static string ExtractLuaError(string html)
    {
        // The body looks like <pre>...</pre> or is plain text; take the first meaningful line after <body>.
        var bodyIdx = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        var slice = bodyIdx >= 0 ? html[bodyIdx..] : html;
        var sb = new StringBuilder();
        var inTag = false;
        foreach (var ch in slice)
        {
            if (ch == '<') { inTag = true; continue; }
            if (ch == '>') { inTag = false; sb.Append(' '); continue; }
            if (!inTag) sb.Append(ch);
        }
        var text = string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > 300) text = text[..300];
        return string.IsNullOrEmpty(text) ? "VLC Lua error" : "VLC Lua error: " + text;
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
