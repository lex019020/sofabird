using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Sofabird.Companion.Vlc;
using Sofabird.Core.Protocol;
using Sofabird.Core.Vlc;

namespace Sofabird.Companion.Net;

/// <summary>
/// Kestrel: <c>/ws</c> (WebSocket → NDJSON session), <c>/info</c> (anonymous JSON), <c>/art</c> (Bearer token → proxy
/// of VLC artwork) over TLS on <see cref="Settings.TlsPort"/> with the certificate (<see cref="TlsCertificate"/>). The
/// plain HTTP port <see cref="Settings.WsPort"/> answers only <c>/info</c> (what the beacon says anyway: discovery and
/// the scripts); since 0.5 nothing else goes unencrypted.
/// </summary>
public sealed class WebServer : IAsyncDisposable
{
    private readonly Settings _settings;
    private readonly CompanionHost _host;
    private readonly SessionManager _sessions;
    private readonly VlcManager _vlc;
    private readonly Microsoft.Extensions.Logging.ILogger _log;
    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2? _certificate;
    private WebApplication? _app;

    public WebServer(Settings settings, CompanionHost host, SessionManager sessions, VlcManager vlc,
        System.Security.Cryptography.X509Certificates.X509Certificate2? certificate, Microsoft.Extensions.Logging.ILogger<WebServer> log)
    {
        _certificate = certificate;
        _settings = settings;
        _host = host;
        _sessions = sessions;
        _vlc = vlc;
        _log = log;
    }


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
            if (_certificate is not null)
                k.Listen(IPAddress.Parse(_settings.ListenAddress), _settings.TlsPort, o => o.UseHttps(_certificate));
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
            if (!ctx.Request.IsHttps) return EncryptedOnly(ctx);
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
            if (!ctx.Request.IsHttps)
            {
                await EncryptedOnly(ctx).ExecuteAsync(ctx).ConfigureAwait(false);
                return;
            }
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("WebSocket expected").ConfigureAwait(false);
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var desc = $"{(ctx.Request.IsHttps ? "wss" : "ws")} {ctx.Connection.RemoteIpAddress}:{ctx.Connection.RemotePort}";
            _log.LogInformation("WebSocket connected: {Desc}", desc);
            var lifetime = ctx.RequestServices.GetRequiredService<IHostApplicationLifetime>();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, lifetime.ApplicationStopping);
            await _sessions.RunSessionAsync(new WebSocketLineTransport(ws, desc), linked.Token).ConfigureAwait(false);
            _log.LogInformation("WebSocket closed: {Desc}", desc);
        });

        app.MapGet("/", () => Results.Text("Sofabird. Endpoints: /info, /ws, /art (the last two over TLS only)", "text/plain; charset=utf-8"));

        await app.StartAsync(ct).ConfigureAwait(false);
        _app = app;
        if (_certificate is null)
            _log.LogError("No certificate: phones cannot connect (only /info answers on {Port})", _settings.WsPort);
        else
            _log.LogInformation("Kestrel listening on {Addr}:{TlsPort} (TLS) and {Port} (/info only)", _settings.ListenAddress,
                _settings.TlsPort, _settings.WsPort);
        if (_certificate is not null) _host.Tls = new TlsInfo { Port = _settings.TlsPort, Sha256 = TlsCertificate.Fingerprint(_certificate) };
    }

    /// <summary>A plain HTTP request for <c>/ws</c> or <c>/art</c> (an app older than 0.5): 426 with where to go.</summary>
    private IResult EncryptedOnly(HttpContext ctx)
    {
        _log.LogDebug("Plain {Path} from {Remote} refused: TLS only", ctx.Request.Path, ctx.Connection.RemoteIpAddress);
        ctx.Response.Headers.Upgrade = "TLS/1.3";
        return Results.Text($"Use https/wss on port {_settings.TlsPort}", "text/plain; charset=utf-8", statusCode: StatusCodes.Status426UpgradeRequired);
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
