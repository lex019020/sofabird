using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using VlcRemote.Companion.Vlc;
using VlcRemote.Core.Protocol;
using VlcRemote.Core.Vlc;

namespace VlcRemote.Companion.Net;

/// <summary>
/// Kestrel: <c>/ws</c> (WebSocket → NDJSON session), <c>/info</c> (anonymous JSON),
/// <c>/art</c> (Bearer token → proxy of VLC artwork).
/// </summary>
public sealed class WebServer : IAsyncDisposable
{
    private readonly Settings _settings;
    private readonly CompanionHost _host;
    private readonly SessionManager _sessions;
    private readonly VlcManager _vlc;
    private readonly Microsoft.Extensions.Logging.ILogger _log;
    private WebApplication? _app;

    public WebServer(Settings settings, CompanionHost host, SessionManager sessions, VlcManager vlc, Microsoft.Extensions.Logging.ILogger<WebServer> log)
    {
        _settings = settings;
        _host = host;
        _sessions = sessions;
        _vlc = vlc;
        _log = log;
    }

    public bool IsRunning => _app is not null;

    public async Task StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, dispose: false);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Parse(_settings.ListenAddress), _settings.WsPort);
            k.AddServerHeader = false;
        });

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

        app.MapGet("/info", (HttpContext ctx) =>
        {
            var json = ProtocolJson.SerializeInfo(_host.BuildInfo());
            return Results.Text(json, "application/json; charset=utf-8");
        });

        app.MapGet("/art", async (HttpContext ctx) =>
        {
            if (!IsAuthorized(ctx))
            {
                ctx.Response.Headers.WWWAuthenticate = "Bearer";
                return Results.Unauthorized();
            }
            int? plId = null;
            if (ctx.Request.Query.TryGetValue("plId", out var raw) && int.TryParse(raw, out var parsed)) plId = parsed;
            try
            {
                var art = await _vlc.GetArtAsync(plId, ctx.RequestAborted).ConfigureAwait(false);
                if (art is null) return Results.NotFound();
                ctx.Response.Headers.CacheControl = "private, max-age=600";
                return Results.Bytes(art.Data, art.ContentType);
            }
            catch (VlcException ex)
            {
                _log.LogDebug("art proxy failed: {Error}", ex.Message);
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
            catch (Core.Session.ProtocolException ex)
            {
                _log.LogDebug("art proxy: {Error}", ex.Message);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.Map("/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("WebSocket expected").ConfigureAwait(false);
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var desc = $"ws {ctx.Connection.RemoteIpAddress}:{ctx.Connection.RemotePort}";
            _log.LogInformation("WebSocket connected: {Desc}", desc);
            var lifetime = ctx.RequestServices.GetRequiredService<IHostApplicationLifetime>();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, lifetime.ApplicationStopping);
            await _sessions.RunSessionAsync(new WebSocketLineTransport(ws, desc), linked.Token).ConfigureAwait(false);
            _log.LogInformation("WebSocket closed: {Desc}", desc);
        });

        app.MapGet("/", () => Results.Text("VLC Remote Companion. Endpoints: /info, /ws, /art", "text/plain; charset=utf-8"));

        await app.StartAsync(ct).ConfigureAwait(false);
        _app = app;
        _log.LogInformation("Kestrel listening on {Addr}:{Port}", _settings.ListenAddress, _settings.WsPort);
    }

    private bool IsAuthorized(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return _host.Pairing.ValidateToken(auth[7..].Trim());
        if (ctx.Request.Query.TryGetValue("token", out var q))
            return _host.Pairing.ValidateToken(q.ToString());
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _app.StopAsync(cts.Token).ConfigureAwait(false);
        }
        catch { /* ignore */ }
        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
    }
}
