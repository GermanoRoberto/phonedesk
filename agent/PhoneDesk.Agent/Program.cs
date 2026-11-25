using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhoneBox.Agent;

var endpoint = AgentEndpoint.FromArgs(args);
var Port = endpoint.Port;
string[] SameOrigins = [$"http://127.0.0.1:{Port}", $"http://localhost:{Port}"];

// Install scripts ask where the agent belongs (it differs per OS), then copy it there.
if (args.Contains("--print-install-dir")) { Console.WriteLine(AppPaths.InstallDir); return; }

// Single instance: a second launch opens the running instance's settings page and exits.
// After a restart the previous instance may still be shutting down, so wait for it briefly.
var single = new Mutex(false, endpoint.MutexName);
var waitMs = args.Contains("--restart") ? 15_000 : 0;
bool firstInstance;
try { firstInstance = single.WaitOne(waitMs); }
catch (AbandonedMutexException) { firstInstance = true; }
if (!firstInstance)
{
    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"http://127.0.0.1:{Port}/") { UseShellExecute = true }); } catch { }
    return;
}

if (!args.Contains("--config")) AppPaths.EnsureConfig();
// absolute from here on: a bare file name ("--config profile.json") has no folder, and the files kept next to the
// config (the Spotify login, backups) need one
var configPath = Path.GetFullPath(args.SkipWhile(a => a != "--config").Skip(1).FirstOrDefault() ?? AppPaths.ConfigPath);
var configDir = Path.GetDirectoryName(configPath) ?? AppPaths.ConfigDir;
AppPaths.RestrictConfig(configPath);
var configStore = new ConfigStore(configPath);
configStore.PersistGenerated(); // keep the Wi-Fi token stable across restarts
var lanCfg = configStore.Get().Lan;
var explicitLanWeb = Program.ConfigHasLanWebConfig(configPath);
var lanWebActive = lanCfg.WebEnabled
    && (Port == 8765 || explicitLanWeb)
    && lanCfg.WebPort != Port
    && (!lanCfg.Enabled || lanCfg.WebPort != lanCfg.Port);
var webRoot = Program.ResolveWebRoot(configDir, lanCfg.WebRoot);
var lanCert = lanCfg.Enabled && lanCfg.Port != Port ? Lan.Certificate(configDir) : null;
// USB uses the same certificate as Wi-Fi; the agent hands its fingerprint to the app over adb (see UsbLink.cs)
var usbCert = lanCert ?? Lan.Certificate(configDir);
var usb = new UsbLink(UsbLink.FreeLoopbackPort(), Lan.Fingerprint(usbCert));

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.WebHost.ConfigureKestrel(k =>
{
    // a dictation travels in the query (the phone's transport sends no request bodies): 1000 characters of any script,
    // percent-encoded, fit in 16 KB
    k.Limits.MaxRequestLineSize = 16 * 1024;
    k.Listen(IPAddress.Loopback, Port);
    if (lanWebActive) k.Listen(IPAddress.Any, lanCfg.WebPort);
    // USB: the end of the phone's adb reverse tunnel, on a port of its own, over TLS the app pins, with the app's key
    k.Listen(IPAddress.Loopback, usb.Port, l => l.UseHttps(usbCert));
    // Wi-Fi / Tailscale: HTTPS with a pinned self-signed certificate, token required (see Lan.cs).
    if (lanCert is not null) k.Listen(IPAddress.Any, lanCfg.Port, l => l.UseHttps(lanCert));
});
builder.Logging.ClearProviders();
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddProvider(new FileLoggerProvider());
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

builder.Services.AddSingleton(configStore);
builder.Services.AddSingleton(endpoint);
builder.Services.AddSingleton(usb);
builder.Services.AddSingleton<AudioControl>();
builder.Services.AddSingleton<AppCatalog>();
builder.Services.AddSingleton(_ => new ShortcutIconUploads(Path.Combine(configDir, "icons")));
builder.Services.AddSingleton(_ => new WallpaperStore(configDir));
builder.Services.AddSingleton<PairingCodes>();
builder.Services.AddSingleton<SpotifyControl>();
builder.Services.AddSingleton<PlexControl>();
builder.Services.AddSingleton<ControlDeck>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ControlDeck>());
builder.Services.AddSingleton<PushStore>();
builder.Services.AddSingleton<CommandRunner>();
builder.Services.AddSingleton<ApprovalStore>();
builder.Services.AddSingleton<HomeAssistant>();
builder.Services.AddSingleton<AppConsole>();
builder.Services.AddSingleton<Dictation>();
builder.Services.AddSingleton<SpectrumAnalyzer>();
AddMonitor<SystemMonitor>(builder.Services);
AddMonitor<MediaMonitor>(builder.Services);
AddMonitor<QuotaMonitor>(builder.Services);
AddMonitor<ProcessMonitor>(builder.Services);
AddMonitor<SessionMonitor>(builder.Services);
AddMonitor<OpenCodeApprovals>(builder.Services);
AddMonitor<FeedMonitor>(builder.Services);
builder.Services.AddSingleton<PhotoFrame>();
builder.Services.AddSingleton<CameraFeeds>();
AddMonitor<GitHubMonitor>(builder.Services);
AddMonitor<PingMonitor>(builder.Services);
AddMonitor<AdbReverse>(builder.Services);
AddMonitor<SessionLockMonitor>(builder.Services);
AddMonitor<UpdateService>(builder.Services);
AddMonitor<PrivacyMonitor>(builder.Services);
AddMonitor<CalendarMonitor>(builder.Services);
AddMonitor<DockerMonitor>(builder.Services);
AddMonitor<ObsMonitor>(builder.Services);
AddMonitor<DiscordMonitor>(builder.Services);
AddMonitor<SteamMonitor>(builder.Services);
builder.Services.AddSingleton<SteamLibrary>();
builder.Services.AddSingleton<WeatherService>();
builder.Services.AddSingleton(sp => new LyricsService(sp.GetRequiredService<ConfigStore>(), sp.GetRequiredService<ILogger<LyricsService>>()));
AddMonitor<BatteryMonitor>(builder.Services);
AddMonitor<RuleEngine>(builder.Services);
AddMonitor<ChargeGuard>(builder.Services);
// the phone's microphone into a virtual cable (off by default)
AddMonitor<PhoneMic>(builder.Services);
builder.Services.AddHostedService<Tray>();

var app = builder.Build();
{
    // the phone acts on it through a shortcut command and reads it in /api/state (see PhoneDevices)
    PhoneDevices.Mic = app.Services.GetRequiredService<PhoneMic>();
}

var adbReverse = app.Services.GetRequiredService<AdbReverse>();
Shortcuts.OnLocked = cfgNow =>
{
    if (cfgNow.Display.LockPhoneWithShortcut)
    {
        _ = Task.Run(() => adbReverse.SleepPhonesAsync());
    }
};

app.MapGet("/api/controls/state", async (ControlDeck deck, CancellationToken ct) => Results.Json(await deck.Snapshot(ct)));
app.MapPost("/api/controls/command/{kind}", async (string kind, string? id, string? op, int? value, bool? muted,
    string? lease, ControlDeck deck, CancellationToken ct) =>
{
    var res = await deck.Act(kind, id, op, value, muted, lease, ct);
    if (res.Ok) StateSignal.Poke();
    return Results.Json(res);
});
app.MapGet("/api/controls/listen", async (HttpResponse response, ControlDeck deck, CancellationToken ct) => await deck.Listen(response, ct));
app.MapPost("/api/controls/capture", (ControlDeck deck) => Results.Json(deck.CaptureProfile()));
app.MapGet("/api/controls/task-output/{id}", (string id, ControlDeck deck) => Results.Text(deck.TaskOutput(id), "text/plain"));
app.MapGet("/api/controls/radio-logo", async (string? id, ControlDeck deck, HttpContext ctx, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(id)) return Results.NotFound();
    var logo = await deck.RadioLogoAsync(id, ct);
    if (logo is null) return Results.NotFound();
    ctx.Response.Headers.CacheControl = logo.Value.IsFallback ? "no-store" : "public, max-age=3600";
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'";
    return Results.Bytes(logo.Value.Bytes, logo.Value.Mime);
});
app.MapGet("/api/controls/spotify/state", async (SpotifyControl spotify, CancellationToken ct) => Results.Json(await spotify.Snapshot(ct)));
app.MapPost("/api/controls/spotify/action/{action}", async (string action, string? id, SpotifyControl spotify, ControlDeck deck, CancellationToken ct) =>
{
    var res = action is "play-queued" or "playlist"
        ? await deck.RunWithMediaFocusAsync(async token =>
        {
            var result = await spotify.Act(action, id, token);
            if (result.Ok) deck.StopAllRadios();
            return result;
        }, ct)
        : await spotify.Act(action, id, ct);
    return Results.Json(res);
});
app.MapGet("/api/controls/plex/state", async (PlexControl plex, CancellationToken ct) => Results.Json(await plex.Snapshot(ct)));
app.MapPost("/api/controls/plex/action/{action}", async (string action, string? id, PlexControl plex, ControlDeck deck, CancellationToken ct) =>
{
    var res = action is "play-queued" or "playlist"
        ? await deck.RunWithMediaFocusAsync(async token =>
        {
            var result = await plex.Act(action, id, token);
            if (result.Ok) deck.StopAllRadios();
            return result;
        }, ct)
        : await plex.Act(action, id, ct);
    return Results.Json(res);
});
// settings page only (not in Lan.AllowedOverLan, not gated): Check connection and the playlist picker
app.MapGet("/api/controls/plex/check", async (PlexControl plex, CancellationToken ct) => Results.Json(await plex.Check(ct)));
app.MapGet("/api/controls/plex/playlists", async (PlexControl plex, CancellationToken ct) => Results.Json(await plex.Playlists(ct)));
app.MapGet("/api/controls/spotify/connect", (SpotifyControl spotify) =>
{ try { return Results.Json(new { url = spotify.Begin(), redirectUri = spotify.RedirectUri }); } catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); } });
app.MapPost("/api/controls/spotify/disconnect", async (SpotifyControl spotify, CancellationToken ct) => { await spotify.Disconnect(ct); return Results.Ok(); });
app.MapGet("/api/controls/spotify/callback", async (string? state, string? code, SpotifyControl spotify, CancellationToken ct) =>
{
    try { return state is not null && code is not null && await spotify.Complete(state, code, ct)
        ? Results.Text("Spotify connected. Return to PhoneBox settings; this tab can be closed.", "text/plain")
        : Results.BadRequest("Spotify sign-in expired or was refused. Start again from desktop settings."); }
    catch { return Results.BadRequest("Spotify sign-in failed. Start again from desktop settings."); }
});
app.MapGet("/api/controls/obs", async (ObsMonitor obs, CancellationToken ct) => Results.Json(await obs.DeckSnapshot(ct)));
app.MapPost("/api/controls/obs/{action}", async (string action, string? id, int? value, bool? enabled, ObsMonitor obs, CancellationToken ct) =>
    Results.Json(await obs.DeckAct(action, id, value, enabled, ct)));
app.MapGet("/api/controls/radio-directory/countries", async (CancellationToken ct) =>
{
    try { return Results.Json(new { countries = await RadioBrowserDirectory.GetCountriesAsync(ct) }); }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex is RadioDirectoryUnavailableException ? ex.Message : "Radio directory is temporarily unavailable." }, statusCode: StatusCodes.Status502BadGateway); }
});
app.MapGet("/api/controls/radio-directory/stations", async (string? country, int? limit, CancellationToken ct) =>
{
    try { return Results.Json(new { stations = await RadioBrowserDirectory.GetStationsByCountryAsync(country ?? "", limit ?? 40, ct) }); }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex is RadioDirectoryUnavailableException ? ex.Message : "Radio directory is temporarily unavailable." }, statusCode: StatusCodes.Status502BadGateway); }
});
app.MapPost("/api/controls/radio-directory/check", async (HttpRequest req, CancellationToken ct) =>
{
    var body = await req.ReadFromJsonAsync<RadioCheckRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
    var results = await RadioBrowserDirectory.CheckStreamsAsync(body?.Stations ?? [], ct);
    return Results.Json(new { results });
});

// The panel in a desktop browser (the layout editor's live preview, /panel/) calls the API as /agent/api/..., the same
// path the phone uses; on the local port that is simply /api/... Routing runs after this, so it sees the new path.
app.Use((ctx, next) =>
{
    if ((ctx.Connection.LocalPort == Port || (lanWebActive && ctx.Connection.LocalPort == lanCfg.WebPort))
        && ctx.Request.Path.StartsWithSegments("/agent/api", out var rest))
        ctx.Request.Path = "/api" + rest;
    return next(ctx);
});

// How long each kind of request takes (GET /api/latency, local only), and a log line for the slow ones: tells a slow
// control action (osascript on a busy computer...) from a slow phone or network. The live stream is long by design.
var timings = new RequestTimings();
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/api/stream")) { await next(ctx); return; }
    var started = System.Diagnostics.Stopwatch.GetTimestamp();
    try { await next(ctx); }
    finally
    {
        var ms = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        // only a request that matched an endpoint (routing has run by now), so unknown paths cannot add rows
        if (RequestTimings.RouteOf(ctx.GetEndpoint(), ctx.Request.Method) is { } route)
        {
            timings.Add(route, ms);
            if (ms >= RequestTimings.SlowMs && ctx.Request.Method != "GET") app.Logger.LogInformation("slow request: {Route} took {Ms} ms", route, ms);
        }
    }
});
app.MapGet("/api/latency", () => Results.Json(timings.Summary()));

// Security:
//  - Local port 8765 listens on 127.0.0.1 only; the Host check blocks DNS rebinding. From browsers only the settings
//    page itself (same origin) is accepted (no CSRF).
//  - The phone's USB tunnel ends on a port of its own (UsbLink): every app on the phone can reach the tunnel, so it
//    is TLS with the certificate the app pins (handed over adb), needs the key the PhoneBox app made (read over adb),
//    and serves only the phone API.
//  - The optional Wi-Fi port only serves the phone API, only over TLS, and only with the paired token.
app.UseWebSockets();
app.UseRouting();
app.Use(async (ctx, next) =>
{
    var origin = ctx.Request.Headers.Origin.ToString();
    if (ctx.Connection.LocalPort == usb.Port && ctx.Request.IsHttps)
    {
        // the phone over USB: only with its app's key, and only the paths the phone uses
        var access = usb.Authorize(ctx.Request.Method, ctx.Request.Path, origin, ctx.Request.Headers.Authorization);
        if (access != UsbLink.Access.Allowed)
        {
            ctx.Response.StatusCode = access == UsbLink.Access.Unauthorized ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden;
            return;
        }
    }
    else if (lanCert is not null && ctx.Connection.LocalPort == lanCfg.Port && ctx.Request.IsHttps)
    {
        // The phone's key opens the panel API; the separate webhook key opens only the webhook endpoints, in a header
        // or, for senders that cannot set headers, in ?token= (URLs end up in logs, so it must not be the phone's key).
        var lan = configStore.Get().Lan;
        var access = Lan.AuthorizeRequest(lan, ctx.Request.Method, ctx.Request.Path, origin,
            ctx.Request.Headers.Authorization, ctx.Request.Query["token"]);
        if (access == Lan.LanAccess.Denied)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (access is Lan.LanAccess.PairingClaim or Lan.LanAccess.Webhook)
        {
            // pairing claims and webhooks must not touch phone registry, app version or "phone connected" updates
            if (access == Lan.LanAccess.PairingClaim)
                ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = 1024;
            ctx.Response.Headers.CacheControl = "no-store";
            await next();
            return;
        }
    }
    else if (lanWebActive && ctx.Connection.LocalPort == lanCfg.WebPort)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is not null && remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (remote is not null && IPAddress.IsLoopback(remote))
        {
            var host = ctx.Request.Host.Host;
            var webOriginOk = origin == ""
                || origin == $"http://127.0.0.1:{lanCfg.WebPort}"
                || origin == $"http://localhost:{lanCfg.WebPort}";
            if ((host != "127.0.0.1" && host != "localhost") || !webOriginOk
                || BrowserGuard.IsForeign(ctx.Request.Headers["Sec-Fetch-Site"], ctx.Request.Headers["Sec-Fetch-Mode"], ctx.Request.Method, ctx.Request.Path))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
        else
        {
            if (!Lan.IsPrivateLanRemote(ctx.Connection.RemoteIpAddress))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (!Lan.IsAllowedBrowserOrigin(origin))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            ctx.Response.Headers["Access-Control-Allow-Origin"] = !string.IsNullOrEmpty(origin) ? origin : "*";
            ctx.Response.Headers["Vary"] = "Origin";
            ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS";
            ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization, X-Config-Version, X-PhoneBox-Device, X-PhoneBox-Model, X-PhoneBox-App, X-PhoneBox-WebView";
            ctx.Response.Headers["Access-Control-Expose-Headers"] = "X-Config-Version";
            if (HttpMethods.IsOptions(ctx.Request.Method))
            {
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
            if (!Lan.AllowedOverLanWeb(ctx.Request.Path))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                if (ctx.Request.Path == "/settings" || ctx.Request.Path == "/settings.html" || ctx.Request.Path == "/config")
                {
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    await ctx.Response.WriteAsync("<h3>As configuracoes do PhoneDesk abrem no navegador do computador.</h3>");
                }
                return;
            }
            var lan = configStore.Get().Lan;
            if (lan.WebRequireToken && ctx.Request.Path.StartsWithSegments("/api") && !string.IsNullOrEmpty(lan.Token))
            {
                if (!Lan.Matches(ctx.Request.Headers.Authorization, lan.Token) && !Lan.MatchesQuery(ctx.Request.Query["token"], lan.Token))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }
        }
    }
    else
    {
        var host = ctx.Request.Host.Host;
        var spotifyCallback = ctx.Request.Method == "GET" && ctx.Request.Path == "/api/controls/spotify/callback"
            && app.Services.GetRequiredService<SpotifyControl>().HasPendingState(ctx.Request.Query["state"]);
        if ((host != "127.0.0.1" && host != "localhost") || (origin != "" && !SameOrigins.Contains(origin))
            || (!spotifyCallback && BrowserGuard.IsForeign(ctx.Request.Headers["Sec-Fetch-Site"], ctx.Request.Headers["Sec-Fetch-Mode"], ctx.Request.Method, ctx.Request.Path)))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }
    // any request from the app shows the phone is there, not only a state poll: an app on the live stream polls
    // nothing, and a stream that is not yet (or no longer) open must not hide the phone from a permission prompt (#497)
    if (ctx.Request.Headers.ContainsKey("X-PhoneBox-App"))
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    if (ctx.Request.Headers.TryGetValue("X-PhoneBox-Device", out var device))
    {
        var problem = PhoneRegistry.Touch(device.ToString(), ctx.Request.Headers["X-PhoneBox-Model"].ToString(),
            ctx.Connection.LocalPort == usb.Port ? "USB" : ctx.Connection.LocalPort == Port ? "local" : "Wi-Fi", ctx.Request.Headers["X-PhoneBox-WebView"].ToString());
        if (problem is not null) app.Logger.LogWarning("WebView: {Problem}", problem);
    }
    var onLanWeb = lanWebActive && ctx.Connection.LocalPort == lanCfg.WebPort;
    if (onLanWeb)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is not null && remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (remote is not null && !IPAddress.IsLoopback(remote) && Program.IsLanWebHeartbeatPath(ctx.Request.Path))
        {
            Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (!ctx.Request.Headers.ContainsKey("X-PhoneBox-Device"))
            {
                var digits = new string(remote.ToString().Where(char.IsAsciiLetterOrDigit).ToArray());
                var lanId = "lan" + digits;
                if (lanId.Length > 32) lanId = lanId[..32];
                var lanModel = Program.ModelFromUserAgent(ctx.Request.Headers.UserAgent.ToString(), remote);
                PhoneRegistry.Touch(lanId, lanModel, "Wi-Fi");
            }
        }
    }
    if (ctx.Request.Headers.TryGetValue("X-PhoneBox-App", out var appVersion) && appVersion.ToString().Length is > 0 and < 32)
        UpdateService.AppVersion = appVersion.ToString();
    ctx.Response.Headers.CacheControl = "no-store";
    // only the panel may be framed, and only by the settings page itself (the live preview)
    ctx.Response.Headers["X-Frame-Options"] = ctx.Request.Path.StartsWithSegments("/panel")
        || ctx.Request.Path == "/index.html"
        || (onLanWeb && (ctx.Request.Path == "/" || ctx.Request.Path == "/settings" || ctx.Request.Path == "/settings.html"))
        ? "SAMEORIGIN" : "DENY";
    await next();
});

app.Map("/api/trackpad", TrackpadHandler.HandleEndpoint);

// ---------------------------------------------------------------- Wi-Fi pairing (local / USB only)
// The phone calls this through the USB tunnel and stores everything it needs to connect over Wi-Fi.
app.MapGet("/api/pair", (ConfigStore cfg) =>
{
    var lan = cfg.Get().Lan;
    if (!lan.Enabled || lanCert is null) return Results.Json(new { enabled = false, pc = Environment.MachineName });
    return Results.Json(new
    {
        enabled = true,
        pc = Environment.MachineName,
        port = lanCfg.Port,
        token = lan.Token,
        fingerprint = Lan.Fingerprint(lanCert),
        hosts = Lan.Addresses(),
        macs = WakeOnLan.LocalMacs(),
    });
});

// The MAC addresses of this computer's LAN adapters: the phone keeps them to wake this PC while the agent is not running.
app.MapGet("/api/wake/macs", () => Results.Json(new { macs = WakeOnLan.LocalMacs() }));

// Pairing without USB (see PairingCodes). Start and cancel are local only (settings page); the phone claims over Wi-Fi.
app.MapPost("/api/pair/start", (ConfigStore cfg, PairingCodes codes) =>
{
    if (!cfg.Get().Lan.Enabled || lanCert is null)
        return Results.Json(new { enabled = false, configured = cfg.Get().Lan.Enabled });
    var (code, expiresAt, attempt) = codes.Start(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    var hosts = Lan.Addresses();
    var fingerprint = Lan.Fingerprint(lanCert);
    var link = PairingCodes.Link(Environment.MachineName, fingerprint, lanCfg.Port, hosts, code);
    var apk = $"https://github.com/ersinkoc/phonebox/releases/download/v{AppPaths.Version}/PhoneBox-{AppPaths.Version}.apk";
    return Results.Json(new
    {
        enabled = true,
        pc = Environment.MachineName,
        hosts,
        port = lanCfg.Port,
        expiresAt,
        claims = codes.Claims,
        attempt,
        check = PairingCodes.Check(fingerprint),
        link,
        qr = PairingCodes.Svg(link),
        app = apk,
        appQr = PairingCodes.Svg(apk),
    });
});
app.MapPost("/api/pair/cancel", (long attempt, PairingCodes codes) => { codes.Cancel(attempt); return Results.Ok(); });
app.MapGet("/api/pair/status", (PairingCodes codes) => Results.Json(new { claims = codes.Claims }));
app.MapPost("/api/pair/claim", (HttpRequest req, ConfigStore cfg, PairingCodes codes) =>
    ClaimPairingAsync(req, cfg, codes, lanCert, lanCfg.Port, app.Logger));

app.MapGet("/api/lan", (ConfigStore cfg) =>
{
    var lan = cfg.Get().Lan;
    return Results.Json(new
    {
        configured = lan.Enabled,
        active = lanCert is not null,
        port = lanCfg.Port,
        restartNeeded = lan.Enabled != (lanCert is not null) || (lanCert is not null && lan.Port != lanCfg.Port)
            || lan.WebEnabled != lanWebActive || (lanWebActive && lan.WebPort != lanCfg.WebPort),
        hosts = Lan.Addresses(),
        fingerprint = lanCert is null ? null : Lan.Fingerprint(lanCert),
        webEnabled = lan.WebEnabled,
        webActive = lanWebActive,
        webPort = lanCfg.WebPort,
        webUrls = lanWebActive ? Lan.Addresses().Select(h => $"http://{h}:{lanCfg.WebPort}/").ToArray() : Array.Empty<string>(),
    });
});

app.MapGet("/api/update", (UpdateService upd, ConfigStore cfg) => Results.Json(new { status = upd.Status(), auto = cfg.Get().Updates.Auto, installed = UpdateService.CanUpdateAgent }));
// Settings › General: install the phone app on a phone that does not have it yet (local only).
app.MapPost("/api/phone/install", async (UpdateService upd) =>
    await upd.InstallAppAsync() is { } error ? Results.BadRequest(new { error }) : Results.Ok(new { ok = true }));
app.MapPost("/api/update/check", async (UpdateService upd) => { await upd.CheckAsync(); return Results.Json(upd.Status()); });
app.MapPost("/api/update/apply", (UpdateService upd) => { _ = Task.Run(() => upd.ApplyAsync()); return Results.Accepted(); });

app.MapPost("/api/restart", (IHostApplicationLifetime life) =>
{
    AppRestart.Requested = true;
    life.StopApplication();
    return Results.Ok();
});

// ---------------------------------------------------------------- settings & universal web UI (browser)
app.MapGet("/", (HttpContext ctx) =>
{
    if (lanWebActive && ctx.Connection.LocalPort == lanCfg.WebPort)
        return Program.ServeWebAsset(webRoot, "index.html", "www/index.html");
    using var s = AppPaths.Resource("settings.html")!;
    using var r = new StreamReader(s);
    return Results.Content(r.ReadToEnd(), "text/html; charset=utf-8");
});
app.MapGet("/index.html", () => Program.ServeWebAsset(webRoot, "index.html", "www/index.html"));
app.MapGet("/settings", () => Program.ServeWebAsset(null, "settings.html", "settings.html"));
app.MapGet("/settings.html", () => Program.ServeWebAsset(null, "settings.html", "settings.html"));
app.MapGet("/config", () => Program.ServeWebAsset(null, "settings.html", "settings.html"));
app.MapGet("/manifest.json", () => Program.ServeWebAsset(webRoot, "manifest.json", null));
app.MapGet("/sw.js", () => Program.ServeWebAsset(webRoot, "sw.js", null));
app.MapGet("/icon.svg", () => Program.ServeWebAsset(webRoot, "icon.svg", null));
app.MapGet("/app.js", () => Program.ServeWebAsset(webRoot, "app.js", null));
app.MapGet("/style.css", () => Program.ServeWebAsset(webRoot, "style.css", null));
// The phone's panel (embedded at build time) for the live preview in the layout editor; also available on LAN web.
app.MapGet("/panel/{*file}", (string? file, HttpRequest req) =>
{
    if (string.IsNullOrEmpty(file) && !req.Path.Value!.EndsWith('/')) return Results.Redirect("/panel/" + req.QueryString);
    return Program.ServePanelAsset(file);
});
app.MapGet("/favicon.ico", () => AppPaths.Resource("phonedesk.ico") is { } s ? Results.Stream(s, "image/x-icon") : Results.NotFound());

// PhoneDesk web panel helpers + loopback-only Windows installed-app discovery
app.MapGet("/api/phonedesk/weather", async (ConfigStore cfg, WeatherService weather, CancellationToken ct) =>
{
    var wCfg = cfg.Get().Weather;
    var city = string.IsNullOrWhiteSpace(wCfg.City) ? "Juiz de Fora" : wCfg.City.Trim();
    if (string.Equals(city, "JF", StringComparison.OrdinalIgnoreCase)) city = "Juiz de Fora";
    var (status, rawJson) = await weather.GetForecastAsync(wCfg, false, ct);
    if (status != 200)
        return Results.Json(new { city, temp = (int?)null, feelsLike = (int?)null, code = 0, isDay = true, alert = (object?)null });
    try
    {
        using var doc = JsonDocument.Parse(rawJson);
        var cur = doc.RootElement.GetProperty("current");
        var temp = (int)Math.Round(cur.GetProperty("temperature_2m").GetDouble());
        var feels = cur.TryGetProperty("apparent_temperature", out var fl) ? (int)Math.Round(fl.GetDouble()) : temp;
        var code = cur.TryGetProperty("weather_code", out var wc) ? wc.GetInt32() : 0;
        var isDay = !cur.TryGetProperty("is_day", out var id) || id.GetInt32() != 0;
        var humidity = cur.TryGetProperty("relative_humidity_2m", out var hm) ? hm.GetInt32() : 0;
        var wind = cur.TryGetProperty("wind_gusts_10m", out var wg) ? (int)Math.Round(wg.GetDouble()) : 0;
        var minTemp = (int?)null;
        var maxTemp = (int?)null;
        if (doc.RootElement.TryGetProperty("daily", out var dy))
        {
            if (dy.TryGetProperty("temperature_2m_min", out var dMin) && dMin.GetArrayLength() > 0)
                minTemp = (int)Math.Round(dMin[0].GetDouble());
            if (dy.TryGetProperty("temperature_2m_max", out var dMax) && dMax.GetArrayLength() > 0)
                maxTemp = (int)Math.Round(dMax[0].GetDouble());
        }
        object? alert = null;
        if (doc.RootElement.TryGetProperty("alert", out var al) && al.ValueKind == JsonValueKind.Object)
        {
            alert = new
            {
                level = al.TryGetProperty("level", out var lv) ? lv.GetString() : "warning",
                icon = al.TryGetProperty("icon", out var ic) ? ic.GetString() : "⚠️",
                title = al.TryGetProperty("title", out var tt) ? tt.GetString() : "",
                desc = al.TryGetProperty("desc", out var ds) ? ds.GetString() : ""
            };
        }
        return Results.Json(new { city, temp, feelsLike = feels, minTemp, maxTemp, code, isDay, humidity, wind, alert });
    }
    catch
    {
        return Results.Json(new { city, temp = (int?)null, feelsLike = (int?)null, code = 0, isDay = true, alert = (object?)null });
    }
});

app.MapGet("/api/phonedesk/version", () =>
{
    var indexPath = !string.IsNullOrWhiteSpace(webRoot) ? Path.Combine(webRoot, "index.html") : null;
    var ver = indexPath is not null && File.Exists(indexPath)
        ? new DateTimeOffset(File.GetLastWriteTimeUtc(indexPath), TimeSpan.Zero).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
        : AppPaths.Version;
    return Results.Json(new { version = ver });
});

app.MapGet("/api/phonedesk/apps", (HttpContext ctx) =>
{
    var remote = ctx.Connection.RemoteIpAddress;
    if (remote is not null && remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
    if (remote is null || !IPAddress.IsLoopback(remote))
        return Results.Json(new { error = "Rota disponivel apenas no computador" }, statusCode: StatusCodes.Status403Forbidden);
    return Results.Json(Program.ScanInstalledWindowsApps());
});

app.MapPost("/api/action", async (HttpRequest req, ConfigStore cfg, PushStore push, AudioControl audio, ILogger<Program> log) =>
{
    JsonElement body;
    try { body = await req.ReadFromJsonAsync<JsonElement>(); }
    catch { return Results.BadRequest(new { ok = false }); }
    var action = body.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
    var hasPayload = body.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object;
    if (action == "launch" && hasPayload)
    {
        var target = payload.TryGetProperty("target", out var tgt) && tgt.ValueKind == JsonValueKind.String ? tgt.GetString()?.Trim() : null;
        if (target is not null && (
            string.Equals(target, $"http://localhost:{lanCfg.WebPort}/settings", StringComparison.OrdinalIgnoreCase)
            || string.Equals(target, $"http://127.0.0.1:{lanCfg.WebPort}/settings", StringComparison.OrdinalIgnoreCase)
            || string.Equals(target, $"http://127.0.0.1:{Port}/", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
                return Results.Json(new { ok = true });
            }
            catch { return Results.BadRequest(new { ok = false }); }
        }
    }
    else if (action == "lock")
    {
        var err = Shortcuts.Execute(new PhoneBox.Agent.Shortcut { Id = "lock", Label = "Lock", Cmd = "builtin:lock" }, log, cfg.Get(), push: push, audio: audio);
        return err is null ? Results.Json(new { ok = true }) : Results.BadRequest(new { ok = false });
    }
    return Results.BadRequest(new { ok = false });
});

// The version lets the settings page save only over what it loaded (X-Config-Version, see ConfigStore.Save).
app.MapGet("/api/settings", (HttpRequest req, ConfigStore cfg, HttpResponse res) =>
{
    var config = cfg.Get();
    var json = JsonSerializer.Serialize(config, ConfigStore.WriteOpts);
    // Only a same-origin browser fetch from the desktop settings page gets credentials. The phone's native USB proxy
    // does not send Fetch Metadata headers; redact for every other request on the local port, including callers that
    // omit the phone headers or try to supply them themselves.
    var remoteIp = req.HttpContext.Connection.RemoteIpAddress;
    if (remoteIp is not null && remoteIp.IsIPv4MappedToIPv6) remoteIp = remoteIp.MapToIPv4();
    var onLanWebPort = lanWebActive && req.HttpContext.Connection.LocalPort == lanCfg.WebPort;
    var isDesktopPort = req.HttpContext.Connection.LocalPort == Port
        || (onLanWebPort && remoteIp is not null && IPAddress.IsLoopback(remoteIp));
    var desktopSettingsFetch = isDesktopPort
        && req.Headers["Sec-Fetch-Site"] == "same-origin"
        && req.Headers["Sec-Fetch-Mode"] == "cors"
        && req.Headers["Sec-Fetch-Dest"] == "empty";
    if ((isDesktopPort && !desktopSettingsFetch) || (onLanWebPort && !desktopSettingsFetch))
        json = Program.RedactSettingsJson(config, onLanWebPort && !desktopSettingsFetch) ?? json;
    res.Headers["X-Config-Version"] = cfg.Version.ToString();
    return Results.Text(json, "application/json; charset=utf-8");
});

app.MapPut("/api/settings", async (HttpRequest req, ConfigStore cfg, ControlDeck deck, PhotoFrame photos, CameraFeeds cameras, ShortcutIconUploads icons, WeatherService weatherSvc, PushStore push, ILogger<Program> log, CancellationToken ct) =>
{
    if (req.ContentType?.StartsWith("application/json") != true) return Results.StatusCode(415);
    using var r = new StreamReader(req.Body);
    long? expected = long.TryParse(req.Headers["X-Config-Version"], out var v) ? v : null; // scripts may leave it out
    var body = await r.ReadToEndAsync();
    if (LanPortCollidesWithLocal(body, Port))
        return Results.BadRequest(new { error = $"Wi-Fi port cannot be the same as the local port ({Port})" });
    if (LanKeysShared(body))
        return Results.BadRequest(new { error = "The Wi-Fi key and the webhook key must be different: make a new key for one of them" });
    AgentConfig? proposed = null;
    try { proposed = JsonSerializer.Deserialize<AgentConfig>(body, ConfigStore.ReadOpts); }
    catch (JsonException) { }
    var current = cfg.Get();
    // /api/settings redacts secrets for callers that are not the same-origin desktop page.
    // Keep the configured secrets if that redacted (or older) client sends them back as missing/null.
    var modifiedProposed = ConfigStore.PreserveSecrets(proposed, current);
    if (proposed?.Weather is { } w && !string.IsNullOrWhiteSpace(w.City))
    {
        var cityTrimmed = w.City.Trim();
        var cityChanged = !string.Equals(cityTrimmed, current.Weather.City, StringComparison.OrdinalIgnoreCase);
        var coordsUnchanged = (Math.Abs(w.Lat - current.Weather.Lat) < 0.0001 && Math.Abs(w.Lon - current.Weather.Lon) < 0.0001)
            || (w.Lat == 0 && w.Lon == 0);
        if (cityChanged && coordsUnchanged)
        {
            if (await weatherSvc.TryGeocodeAsync(cityTrimmed, ct) is { } geo)
            {
                w.Lat = geo.Lat;
                w.Lon = geo.Lon;
                modifiedProposed = true;
            }
        }
    }
    if (modifiedProposed)
        body = JsonSerializer.Serialize(proposed, ConfigStore.WriteOpts);
    if (proposed?.Steam is { } steam)
    {
        steam.SteamId = string.IsNullOrWhiteSpace(steam.SteamId) ? null : steam.SteamId.Trim();
        if (steam.SteamId is { } profile && (profile.Length != 17 || !profile.All(char.IsAsciiDigit)))
        {
            if (!steam.Enabled || !SteamMonitor.WidgetWanted(proposed))
                return Results.BadRequest(new { error = "Add a Steam widget to an enabled page and enable Steam before saving a profile URL" });
            if (string.IsNullOrWhiteSpace(steam.ApiKey))
                return Results.BadRequest(new { error = "Enter your Steam Web API key before saving a profile URL" });
            try
            {
                steam.SteamId = await SteamMonitor.ResolveAccountId(profile, steam.ApiKey, ct);
                body = JsonSerializer.Serialize(proposed, ConfigStore.WriteOpts);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return Results.BadRequest(new { error = SteamMonitor.Redact(SteamMonitor.UserError(ex), steam.ApiKey) }); }
        }
    }
    string? err;
    long savedVersion;
    lock (IconCommit) err = cfg.Save(body, expected, out savedVersion);
    if (err == ConfigStore.Conflict) return Results.Json(new { error = err }, statusCode: StatusCodes.Status409Conflict);
    if (err is not null) return Results.BadRequest(new { error = err });
    deck.StopDisabledControls();
    photos.SettingsSaved();
    cameras.SettingsSaved();
    deck.ReconcileRadios(waitForGate: true);
    icons.Cleanup(() => cfg.Get().Shortcuts.Select(s => s.Id)); // drop uploads for shortcuts that got deleted
    ShortcutIcons.ClearHasIconCache();
    StateSignal.Poke();
    push.Emit("refresh");
    log.LogInformation("settings saved");
    return Results.Ok(new { version = savedVersion.ToString(CultureInfo.InvariantCulture) });
});

// Narrow write path for the phone: only the text of a Note widget may be changed.
// The text travels in the query string: the Android proxy forwards no request body.
app.MapPost("/api/widget/{pageId}/{widgetId}/note", (string pageId, string widgetId, string? text, string? version, ConfigStore cfg) =>
{
    long? expected = long.TryParse(version, out var v) ? v : null;
    // the same lock as the settings save and the icon commits, so an icon upload can't report a version that
    // already holds a Note edit it never checked
    NoteEdit.Result r;
    lock (IconCommit) r = NoteEdit.Apply(cfg, pageId, widgetId, text, expected);
    return r.Status switch
    {
        200 => Results.Ok(new { version = r.Version }),
        404 => Results.NotFound(new { error = r.Error }),
        409 => Results.Json(new { error = r.Error }, statusCode: StatusCodes.Status409Conflict),
        _ => Results.BadRequest(new { error = r.Error }),
    };
});

// The phone's other narrow write: a Discord widget's favourite servers and voice channels. Only an id the agent itself
// reported, only into that widget's two lists (DiscordFavorites). Query string only, like the note above.
app.MapPost("/api/widget/{pageId}/{widgetId}/discord-favorite", (string pageId, string widgetId, string? kind, string? id, bool? on, string? version, ConfigStore cfg, DiscordMonitor dc) =>
{
    // always over the settings the phone has loaded, never over whatever is current
    if (!long.TryParse(version, out var expected)) return Results.BadRequest(new { error = "version is missing" });
    DiscordFavorites.Result r;
    lock (IconCommit) r = DiscordFavorites.Apply(cfg, pageId, widgetId, kind, id, on ?? true, dc.Knows, expected);
    return r.Status switch
    {
        200 => Results.Ok(new { version = r.Version }),
        404 => Results.NotFound(new { error = r.Error }),
        409 => Results.Json(new { error = r.Error }, statusCode: StatusCodes.Status409Conflict),
        _ => Results.BadRequest(new { error = r.Error }),
    };
});

app.MapPost("/api/shortcuts/test", async (HttpRequest req, ConfigStore cfg, PushStore push, AudioControl audio, ILogger<Program> log) =>
{
    if (req.ContentType?.StartsWith("application/json") != true) return Results.StatusCode(415);
    var sc = await req.ReadFromJsonAsync<PhoneBox.Agent.Shortcut>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    if (sc is null || string.IsNullOrWhiteSpace(sc.Cmd)) return Results.BadRequest(new { error = "command is empty" });
    // the same pushed values and audio state as a phone run, so a macro's nested toggles pick the same command
    var err = Shortcuts.Execute(sc, log, cfg.Get(), push: push, audio: audio);
    return err is null ? Results.Ok() : Results.BadRequest(new { error = err });
});

app.MapGet("/api/info", (AdbReverse adb, ConfigStore cfg) =>
{
    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var recentPhone = PhoneRegistry.All().OrderByDescending(p => p.LastSeen).FirstOrDefault(p => nowMs - p.LastSeen < 15000);
    return Results.Json(new
    {
        version = AppPaths.Version,
        os = AppPaths.Os,
        configPath = cfg.Path,
        logPath = AppPaths.LogPath,
        adb = AdbReverse.FindAdb(cfg.Get().AdbPath),
        // USB (adb) or a live stream from the phone in the last few seconds, which covers Wi-Fi / Tailscale too
        connected = adb.Connected || ApprovalStore.PhoneConnected,
        via = adb.Connected ? "USB" : ApprovalStore.PhoneConnected ? "Wi-Fi" : null,
        phoneModel = recentPhone?.Model,
        devices = adb.AllDevices,
        appMissing = adb.MissingApp,
        usbProblem = adb.UsbProblem,
        webviewProblem = WebViewDoctor.DiagnoseRecent(PhoneRegistry.All(), nowMs),
    });
});

// Phone mic status for the settings page and scripts (local port only: not a phone path, see Lan.AllowedOverLan).
app.MapGet("/api/phone-mic", (PhoneMic mic) => Results.Json(new { mic = mic.Snapshot() }));
app.MapPost("/api/phone-mic/mute", (bool? muted, PhoneMic mic) =>
{
    if (muted is null) mic.Toggle(); else mic.SetMuted(muted.Value);
    return Results.Json(new { muted = mic.Muted });
});
// Set up: the only way the mic starts (the user presses it in the settings page; local port only).
app.MapPost("/api/phone-mic/setup", async (ConfigStore cfg, CancellationToken ct) =>
{
    if (!OperatingSystem.IsWindows()) return Results.Json(new PhoneDeviceSetup.Result(false, "The phone mic needs Windows."));
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
    var r = await PhoneDeviceSetup.SetupMicAsync(cfg, PhoneDeviceSetup.PlaybackDevices, (url, t) => PhoneDeviceSetup.DownloadAsync(http, url, t), ct);
    return Results.Json(r);
});
app.MapPost("/api/phone/open", async (AdbReverse adb) => { await adb.OpenPanelAsync(); return Results.Ok(); });

// Widget types and page templates for the settings page's layout editor.
app.MapGet("/api/catalog", () => Results.Text($"{{\"cols\":{Catalog.Cols},\"rows\":{Catalog.Rows},\"widgets\":{Catalog.WidgetsJson},\"templates\":{Catalog.TemplatesJson}}}",
    "application/json; charset=utf-8"));

// ---------------------------------------------------------------- webhook API: push data and events into the panel
// POST /api/push/{key}       body: JSON or plain text (or ?value=); optional ?ttl=seconds. Shown by "push" widgets.
// DELETE /api/push/{key}     removes a value; GET /api/push lists them.
// POST /api/notify           {"title","text","level":"info|ok|warn|error","vibrate":true,"page":"<page id>","seconds":5} or plain text
// POST /api/goto/{page}      switches the phone to a page (id, or 1-based number)
// POST /api/refresh          refreshes quotas/GitHub now and makes the phone reload its settings and data; ?reload=1 reloads the whole panel
app.MapGet("/api/push", (PushStore push) => Results.Json(new { version = push.Version, items = push.Snapshot() }));
app.MapGet("/api/push/{key}", (string key, PushStore push) => push.Get(key) is { } item ? Results.Json(item) : Results.NotFound());
app.MapMethods("/api/push/{key}", ["POST", "PUT"], async (string key, int? ttl, HttpRequest req, PushStore push) =>
{
    var (data, error) = await PushStore.ReadBody(req);
    if (error is not null) return Results.BadRequest(new { error });
    error = push.Set(key, data!.Value, ttl);
    return error is null ? Results.Json(new { ok = true, key, version = push.Version }) : Results.BadRequest(new { error });
});
app.MapDelete("/api/push/{key}", (string key, PushStore push) => push.Remove(key) ? Results.Ok() : Results.NotFound());
app.MapDelete("/api/push", (PushStore push) => { push.Clear(); return Results.Ok(); });

app.MapPost("/api/notify", async (HttpRequest req, PushStore push) =>
{
    var (data, error) = await PushStore.ReadBody(req);
    if (error is not null) return Results.BadRequest(new { error });
    var d = data!.Value;
    var payload = d.ValueKind == JsonValueKind.Object ? d : JsonSerializer.SerializeToElement(new { text = d.ToString() });
    if (!payload.TryGetProperty("text", out _) && !payload.TryGetProperty("title", out _)) return Results.BadRequest(new { error = "give a text or a title" });
    return Results.Json(push.Emit("notify", payload));
});

// ?countdown=0 switches at once; otherwise the phone counts down like for a rule.
app.MapPost("/api/goto/{page}", (string page, int? countdown, PushStore push) =>
    Results.Json(push.Emit("goto", JsonSerializer.SerializeToElement(new { page, countdown = countdown is { } c ? Math.Clamp(c, 0, 30) : (int?)null }))));

app.MapPost("/api/refresh", (bool? reload, PushStore push) =>
{
    Wake.Now();
    return Results.Json(push.Emit(reload == true ? "reload" : "refresh"));
});

// ---------------------------------------------------------------- phone panel API
object State(IServiceProvider sp, long? ev)
{
    var upd = sp.GetRequiredService<UpdateService>();
    var push = sp.GetRequiredService<PushStore>();
    var cfgStore = sp.GetRequiredService<ConfigStore>();
    var cfgNow = cfgStore.Get();
    return new
    {
        update = upd.Status() is { } u && (u.AppUpdate || u.AgentUpdate) ? new { latest = u.Latest, app = u.AppUpdate, agent = u.AgentUpdate } : null,
        ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        host = Environment.MachineName,
        deviceName = cfgNow.DeviceName,
        telemetryMode = cfgNow.TelemetryMode,
        os = AppPaths.Os, // the Trackpad's gesture vibration is only for the computers that act on a gesture (macOS)
        cfgVersion = cfgStore.Version.ToString(),
        pushVersion = push.Version,
        evSeq = push.Seq,
        events = ev is { } since ? push.Since(since) : [],
        pc = sp.GetRequiredService<SystemMonitor>().Snapshot(),
        // the last reading only: a widget that shows the media asks for fresh ones through GET /api/media (#523)
        media = sp.GetRequiredService<ControlDeck>().EffectiveMedia(sp.GetRequiredService<MediaMonitor>().Peek()),
        audio = sp.GetRequiredService<AudioControl>().Snapshot(),
        ping = sp.GetRequiredService<PingMonitor>().Snapshot(),
        rules = sp.GetRequiredService<RuleEngine>().Active(),
        rulesPaused = sp.GetRequiredService<RuleEngine>().PausedUntil(),
        privacy = sp.GetRequiredService<PrivacyMonitor>().Snapshot(),
        obs = sp.GetRequiredService<ObsMonitor>().Snapshot() is { Connected: true } o ? new { o.Streaming, o.Recording, o.RecordPaused } : null,
        toggles = Shortcuts.States(cfgNow, push, sp.GetRequiredService<AudioControl>()),
        cycles = Shortcuts.CycleStates(cfgNow),
        phoneMic = sp.GetRequiredService<PhoneMic>().PhoneState(),
    };
}

// Upcoming calendar events (Agenda widget); the feed URLs stay here.
app.MapGet("/api/calendar", (CalendarMonitor cal) => Results.Json(cal.Snapshot()));

// Cached Open-Meteo weather forecast and city geocoding search.
app.MapGet("/api/weather", async (bool? force, ConfigStore cfg, WeatherService weather, CancellationToken ct) =>
{
    var (status, json) = await weather.GetForecastAsync(cfg.Get().Weather, force == true, ct);
    return Results.Text(json, "application/json; charset=utf-8", statusCode: status);
});
app.MapGet("/api/weather/search", async (string? q, string? lang, WeatherService weather, CancellationToken ct) =>
{
    var (status, json) = await weather.SearchAsync(q, lang, ct);
    return Results.Text(json, "application/json; charset=utf-8", statusCode: status);
});

// Headlines for the Feeds widget; the feed and article addresses stay here, the phone opens an article by its id.
app.MapGet("/api/feeds", (FeedMonitor feeds) =>
{
    var s = feeds.Snapshot();
    return Results.Json(new { s.Items, s.Updated, s.Errors, canOpen = feeds.OpenAllowed() });
});
// Photo frame: ids of the photos in the chosen folder, then one (scaled-down) photo at a time by id.
app.MapGet("/api/photos", (PhotoFrame photos) =>
{
    var (ids, error, scanning) = photos.List();
    return Results.Json(new { ids, error, scanning });
});
app.MapGet("/api/photos/{id}", async (string id, PhotoFrame photos, HttpResponse res, CancellationToken ct) =>
{
    var (bytes, type, error) = await photos.Get(id, ct);
    if (bytes is null) return Results.Json(new { error }, statusCode: 404);
    res.Headers.CacheControl = "private, max-age=86400";
    return Results.File(bytes, type!);
});
// Camera widget: the latest picture of the camera a widget shows, as a JPEG. Pulling a camera starts with these requests
// and stops a few seconds after the last one; without a fresh picture the answer is a state, never an older picture.
// "fresh" is read as text, not bound as a bool: a value the bool binder can't parse ("1") would answer 400 instead
app.MapGet("/api/camera", (string? widget, string? fresh, HttpRequest req, HttpResponse res, CameraFeeds cameras) =>
{
    var (status, jpeg, state) = cameras.Frame(req.Headers["X-PhoneBox-Device"].ToString(), widget, fresh is "true" or "1");
    res.Headers.CacheControl = "no-store";
    res.Headers["X-Content-Type-Options"] = "nosniff";
    if (status == 204) return Results.NoContent(); // no newer picture than the one this widget has
    return jpeg is null ? Results.Json(new { state }, statusCode: status) : Results.Bytes(jpeg, "image/jpeg");
});
app.MapPost("/api/feeds/open", (string? id, FeedMonitor feeds) =>
    feeds.Open(id) is { } error ? Results.BadRequest(new { error }) : Results.Ok(new { ok = true }));

// Docker / Podman containers (Docker widget); start / stop / restart only if a widget allows it.
app.MapGet("/api/docker", async (DockerMonitor docker, CancellationToken ct) =>
    Results.Json(new { snapshot = await docker.SnapshotAsync(ct), control = docker.ControlAllowed() }));
app.MapPost("/api/docker/{id}/{action}", async (string id, string action, DockerMonitor docker) =>
    await docker.ActAsync(id, action) is { } error ? Results.BadRequest(new { error }) : Results.Ok(new { ok = true }));

// Home Assistant (widget): states of the entities the widgets list, and switching those a widget allows.
app.MapGet("/api/ha/states", async (string? ids, HomeAssistant ha) =>
    Results.Json(await ha.StatesAsync((ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))));
app.MapPost("/api/ha/toggle/{id}", async (string id, HomeAssistant ha) =>
    await ha.ToggleAsync(id) is { } error ? Results.BadRequest(new { error }) : Results.Ok(new { ok = true }));
// Settings › General › Ping & network: the connected adapters to pick from (local only, not on the Wi-Fi list)
app.MapGet("/api/network/adapters", (ConfigStore cfg) => Results.Json(SystemMonitor.Adapters(cfg.Get().Ping.Adapters)));

// Settings › widget › Home Assistant entities: the picker's list (local only, not on the Wi-Fi allow-list)
app.MapGet("/api/ha/entities", async (HomeAssistant ha) => Results.Json(await ha.ListEntitiesAsync()));
// Settings › General › Home Assistant › Test (local only)
app.MapPost("/api/ha/test", async (HomeAssistantConfig body) =>
    Results.Json(new { result = await HomeAssistant.TestAsync(body.Url ?? "", body.Token ?? "") }));

// Settings › General › Photo frame › Immich › Load albums (local only: not on the Wi-Fi allow-list). The key in the body
// is used for this request only; the saved one stands in when the address is unchanged and the field was left empty.
app.MapPost("/api/immich/albums", async (ImmichAlbumsRequest body, PhotoFrame photos, CancellationToken ct) =>
{
    var (albums, error) = await photos.ImmichAlbums(body.Url, body.Key, ct);
    return albums is null ? Results.Json(new { error }) : Results.Json(new { albums });
});

// Dictation (widget): text the phone recognized, typed into the app in front (only if that widget allows it).
// The text is in the query: the phone's WebView sees no request bodies, so its transport (USB and Wi-Fi) sends POSTs
// without one, like every other panel request. Kestrel's request line (16 KB) bounds it; request URLs aren't logged.
app.MapPost("/api/dictate", async (string? widget, string? text, HttpRequest req, Dictation dictation, CancellationToken ct) =>
{
    var (status, error) = await dictation.TypeAsync(req.Headers["X-PhoneBox-Device"].ToString(), widget, text, ct);
    return error is null ? Results.Ok(new { ok = true }) : Results.Json(new { error }, statusCode: status);
});

// App console (widget): the focused app's profile (labels only) and its keys, by profile id and slot.
app.MapGet("/api/console", (string? widget, HttpRequest req, AppConsole console) =>
{
    var (status, body) = console.State(req.Headers["X-PhoneBox-Device"].ToString(), widget);
    return Results.Json(body, statusCode: status);
});
app.MapPost("/api/console/{profile}/{slot}", async (string profile, string slot, string? widget, int? times, HttpRequest req, AppConsole console, CancellationToken ct) =>
{
    var (status, error) = await console.Act(req.Headers["X-PhoneBox-Device"].ToString(), widget, profile, slot, times ?? 1, ct);
    return error is null ? Results.Ok(new { ok = true }) : Results.Json(new { error }, statusCode: status);
});

// OBS Studio (widget): state, scenes and the widget's buttons (only if a widget allows control).
app.MapGet("/api/obs", (ObsMonitor obs, ConfigStore cfg) => Results.Json(new
{
    enabled = cfg.Get().Obs.Enabled,
    snapshot = obs.Snapshot(),
    control = cfg.Get().Widgets(enabledPagesOnly: true).Any(w => (w.Type == "obs" || w.Type.StartsWith("obs-")) && w.Bool("control") == true),
}));
app.MapPost("/api/obs/{action}", async (string action, string? scene, ObsMonitor obs) =>
    await obs.ActAsync(action, scene) is { } error ? Results.BadRequest(new { error }) : Results.Ok(new { ok = true }));
// Settings › General › OBS › Test (local only: deliberately not under /api/obs, which the phone may reach over Wi-Fi)
app.MapPost("/api/obs-test", async (ObsConfig body) => Results.Json(new { result = await ObsMonitor.TestAsync(body.Url, body.Password) }));

// Discord (widget): voice channel, mute / deafen, joinable voice channels, notifications; buttons only if a widget allows control.
app.MapGet("/api/discord", (DiscordMonitor dc, ConfigStore cfg) => Results.Json(new
{
    enabled = cfg.Get().Discord.Enabled,
    snapshot = dc.Snapshot(),
    control = DiscordMonitor.ControlAllowed(cfg.Get()),
    share = DiscordMonitor.ShareWanted(cfg.Get()),
    widgetControl = DiscordMonitor.WidgetControl(cfg.Get()),
    chat = DiscordMonitor.ChatWanted(cfg.Get()),
    chatUnread = dc.ChatUnread(),
}));
// Screen share (its own switch, off by default): the state, and the running apps (names only) while
// the phone's share view is open. It needs a widget's control (the global switch is not enough). code: what went wrong, for the panel to say in its language.
app.MapGet("/api/discord/share", (bool? apps, DiscordMonitor dc) => Results.Json(dc.Share(apps == true)));
app.MapPost("/api/discord/share/{action}", async (string action, int? pid, DiscordMonitor dc) =>
    await dc.ShareActAsync(action, pid) is { } e ? Results.BadRequest(new { error = e.Error, code = e.Code }) : Results.Ok(new { ok = true }));
// Chat (read-only, Settings › General › Discord › chat, off by default): the chosen channel's last messages, the servers
// or, for the phone's channel picker (?picker=true), no messages but the text channels of ?guild=. Only while the setting
// is on and a Discord widget is on an enabled page; message text comes from memory only.
app.MapGet("/api/discord/chat", async (string? guild, bool? picker, DiscordMonitor dc, CancellationToken ct) =>
    Results.Json(await dc.ChatAsync(guild, picker == true, ct)));
app.MapPost("/api/discord/chat/choose", (string? channel, DiscordMonitor dc) =>
    dc.ChatChoose(channel) is { } error ? Results.BadRequest(new { error }) : Results.Ok(new { ok = true }));
app.MapPost("/api/discord/chat/open", async (DiscordMonitor dc) =>
    await dc.ChatOpenAsync() is { } error ? Results.BadRequest(new { error }) : Results.Ok(new { ok = true }));
// Ids go in the query string: the Android app lets image requests through by exact path (READ_ONLY_IMAGES).
app.MapGet("/api/discord/img", async (string kind, string id, string hash, DiscordMonitor dc, CancellationToken ct) =>
    await dc.ImageAsync(kind, id, hash, ct) is { } img
        ? Results.File(img.Data, img.Type, lastModified: null, entityTag: null, enableRangeProcessing: false) : Results.NotFound());
app.MapPost("/api/discord/{action}", async (string action, string? channel, DiscordMonitor dc) =>
    await dc.ActAsync(action, channel) is { } error ? Results.BadRequest(new { error }) : Results.Ok(new { ok = true }));

// Steam friends presence; the API key stays on the computer and only the public presence snapshot reaches the phone.
app.MapGet("/api/steam", (SteamMonitor steam, ConfigStore cfg) =>
{
    var current = cfg.Get();
    var widgets = current.Widgets(enabledPagesOnly: true).ToArray();
    var includeFriends = widgets.Any(w => w.Type == "steam");
    var includeOffline = widgets.Any(w => w.Type == "steam" && w.Bool("offline") == true);
    var includeActivity = widgets.Any(w => w.Type == "steam-activity");
    return Results.Json(new
    {
        enabled = current.Steam.Enabled,
        snapshot = steam.Snapshot(includeFriends, includeOffline, includeActivity),
    });
});
app.MapGet("/api/steam/avatar", async (string id, SteamMonitor steam, CancellationToken ct) =>
    await steam.AvatarAsync(id, ct) is { } img
        ? Results.File(img.Data, img.Type, lastModified: null, entityTag: null, enableRangeProcessing: false) : Results.NotFound());

// Steam library from Steam's own files on this computer (no API key, no account): installed games, covers, the game
// being played. Nothing is read while no Steam library widget is on an enabled page.
app.MapGet("/api/steam/library", (SteamLibrary library) => Results.Json(library.View()));
// The installed games (id and name) for the widget's favorites picker in settings. Not in Lan.cs: the computer only.
app.MapGet("/api/steam/games", (SteamLibrary library) => Results.Json(library.Installed()));
// A cover from Steam's appcache/librarycache, for an installed app id only (SteamLibrary.FindCover checks the path).
// The id goes in the query string: the Android app lets image requests through by exact path (READ_ONLY_IMAGES).
app.MapGet("/api/steam/cover", (string? id, SteamLibrary library, HttpResponse res) =>
{
    if (library.CoverPath(id) is not { } path) return Results.NotFound();
    byte[] bytes;
    try { bytes = File.ReadAllBytes(path); } catch { return Results.NotFound(); }
    if (bytes.Length > SteamLibrary.MaxCoverBytes) return Results.NotFound();
    res.Headers.CacheControl = "private, max-age=86400";
    return Results.File(bytes, path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
});
// Starts an installed game through Steam (steam://rungameid/<id>) for the asking widget ("widget=pageId/widgetId"),
// only with that widget's "Let the phone start games" on (off by default), like the App console and Dictation.
app.MapPost("/api/steam/launch", (string? id, string? widget, HttpRequest req, SteamLibrary library) =>
    library.Launch(req.Headers["X-PhoneBox-Device"].ToString(), widget, id) is { } refused
        ? Results.BadRequest(new { error = refused.Error, code = refused.Code })
        : Results.Ok(new { ok = true }));
// Synced lyrics of the track playing now, from LRCLIB, for the asking widget ("widget=pageId/widgetId"): only when that
// widget is on an enabled page with "Look up lyrics on LRCLIB" turned on (off by default). The track is the one Now playing
// shows (the agent's own media snapshot, with PhoneBox's Internet Radio in its place while that plays or is paused, which
// is never looked up): the phone sends no text that could reach LRCLIB.
app.MapGet("/api/lyrics", async (string? widget, HttpRequest req, MediaMonitor media, ControlDeck deck, LyricsService lyrics, CancellationToken ct) =>
{
    var shown = deck.EffectiveMedia(media.Snapshot());
    if (shown?.App == "Internet Radio") shown = null;
    return Results.Json(await lyrics.ForAsync(shown, req.Headers["X-PhoneBox-Device"].ToString(), widget, ct));
});

// Battery levels of wireless devices (headphones, mice, keyboards…), for the Device batteries widget.
app.MapGet("/api/batteries", (BatteryMonitor b) => Results.Json(new { devices = b.Snapshot() }));

// Settings › Rules › Test: runs a rule's action once (the rule may be unsaved).
app.MapPost("/api/rules/test", (RuleConfig rule, RuleEngine rules) =>
{
    if (!RuleConfig.Actions.Contains(rule.Then)) return Results.BadRequest(new { error = "unknown action" });
    if (string.IsNullOrWhiteSpace(rule.Id)) rule.Id = "test";
    rules.Test(rule);
    return Results.Ok(new { ok = true });
});

// The phone reports its battery (charge guard); the settings show the guard's state and can switch the power to test it.
app.MapPost("/api/phone/battery", (int level, bool plugged, double? temp, string? source, string? usb) => { ChargeGuard.Report(level, plugged, temp, source, usb); return Results.Ok(); });
app.MapGet("/api/charge", (ChargeGuard guard) => Results.Json(guard.Status()));
app.MapPost("/api/charge/test", async (string action, ChargeGuard guard, ConfigStore cfg) =>
{
    if (action is not ("on" or "off")) return Results.BadRequest(new { error = "unknown action" });
    return await guard.Switch(cfg.Get().Charge, action) is { } error ? Results.BadRequest(new { error }) : Results.Ok(new { ok = true });
});

// Pause all rules for a while (the phone's overview, the settings); minutes=0 resumes.
app.MapPost("/api/rules/pause", (int? minutes, RuleEngine rules) =>
{
    rules.Pause(minutes ?? 60);
    return Results.Ok(new { pausedUntil = rules.PausedUntil() });
});

app.MapGet("/api/state", (long? ev, HttpContext ctx) => Results.Json(State(ctx.RequestServices, ev)));

// Live stream (server-sent events) for the phone: "state" every second, "event" as soon as one happens, "work" when it
// changes. The Android shell keeps one connection open instead of polling; the panel falls back to polling without it.
// One "state" frame for all open streams: they wake on the same second (below), so the state is built and serialized once
// per tick, not once per phone. Everything State() reads is a singleton, so the root provider will do.
var stateFrames = new StateFrameCache(() => State(app.Services, null), new JsonSerializerOptions(JsonSerializerDefaults.Web));
app.MapGet("/api/stream", async (HttpContext ctx, PushStore push, SessionMonitor sessions, GitHubMonitor gh, SpectrumAnalyzer spectrum,
    IHostApplicationLifetime life, ILogger<Program> log) =>
{
    // Ends when the agent begins to stop, too: the server otherwise waits up to 30 s for this never-ending response
    // before it lets a restart, an update or Quit finish.
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, life.ApplicationStopping);
    var ct = linked.Token;
    var isPhone = ctx.Request.Headers.ContainsKey("X-PhoneBox-App");
    var via = ctx.Connection.LocalPort == usb.Port ? "USB" : ctx.Connection.LocalPort == Port ? "local" : "Wi-Fi";
    log.LogInformation("live stream opened ({Who}, {Via})", isPhone ? "phone" : "other client", via);
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    async Task Send(string name, object data)
    {
        await ctx.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, opts)}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
    using var wake = new SemaphoreSlim(0);
    var poked = 0;
    void OnEmit() { try { wake.Release(); } catch (ObjectDisposedException) { } }
    // something the phone shows just changed (see StateSignal): send a state now, not at the next whole second
    void OnPoke() { Volatile.Write(ref poked, 1); OnEmit(); }
    push.Emitted += OnEmit;
    StateSignal.Changed += OnPoke;
    try
    {
        var seq = push.Seq;
        var lastWork = "";
        var next = 0L;
        var lastSpec = 0L;
        var lastState = 0L;
        var tick = 0;
        while (!ct.IsCancellationRequested)
        {
            foreach (var e in push.Since(seq)) { await Send("event", e); seq = e.Seq; }
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var tickDue = now >= next;
            var pokeDue = Volatile.Read(ref poked) == 1 && now - lastState >= 100;
            if (tickDue || pokeDue)
            {
                Volatile.Write(ref poked, 0); // before the frame is built: a change during the build is answered by the next pass
                if (isPhone) Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, now);
                await ctx.Response.WriteAsync(stateFrames.Get(push.Seq, StateSignal.Version), ct);
                await ctx.Response.Body.FlushAsync(ct);
                lastState = now;
            }
            if (tickDue)
            {
                if (tick++ % 2 == 0)
                {
                    var work = WorkFeed.Build(sessions, gh);
                    var json = JsonSerializer.Serialize(work, opts);
                    if (json != lastWork) { lastWork = json; await Send("work", work); }
                }
                next = (now / 1000 + 1) * 1000; // the next whole second, the same for every stream (see stateFrames)
            }
            // spectrum frames for a visualizer on screen (SpectrumAnalyzer runs only while a panel asks for it). While
            // capture runs, every open stream gets them, also a second phone without a visualizer: the stream is opened
            // by the app shell and /api/spectrum by the panel, and nothing ties the two together (over USB both phones
            // are 127.0.0.1). The frames are small (48 numbers) and capture stops ~5 s after the last visible widget.
            var specActive = spectrum.Active;
            if (specActive && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastSpec >= 1000 / SpectrumAnalyzer.Fps - 3)
            {
                lastSpec = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (spectrum.Frame() is { } bands) await Send("spectrum", new { b = bands });
            }
            var waitMs = next - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // a change that arrived within 100 ms of the last state waits out the rest of that gap
            if (Volatile.Read(ref poked) == 1) waitMs = Math.Min(waitMs, lastState + 100 - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (specActive) waitMs = Math.Min(waitMs, lastSpec + 1000 / SpectrumAnalyzer.Fps - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await wake.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(5, waitMs)), ct);
        }
    }
    catch (OperationCanceledException) { }
    catch (IOException) { }
    finally
    {
        push.Emitted -= OnEmit;
        StateSignal.Changed -= OnPoke;
        log.LogInformation("live stream closed ({Via})", via);
    }
});

// Claude Code PermissionRequest hook (local only): waits for Allow / Deny from the phone, prints nothing otherwise.
app.MapPost(ClaudeHook.Endpoint, async (HttpRequest req, ConfigStore cfg, ApprovalStore approvals, SessionMonitor sessions) =>
{
    JsonElement input;
    try { using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted); input = doc.RootElement.Clone(); }
    catch (JsonException) { return Results.Text(""); }
    var output = await ClaudeHook.HandleAsync(input, cfg, approvals, sessions, req.HttpContext.RequestAborted);
    return output is null ? Results.Text("") : Results.Json(output);
});
app.MapGet("/api/claude/hook", () => Results.Json(new { installed = ClaudeHook.Installed(), path = ClaudeHook.SettingsPath }));
app.MapPost("/api/claude/hook", (ConfigStore cfg) => ClaudeHook.Configure(true, Port, cfg.Get().Claude.TimeoutSec) is { } err ? Results.BadRequest(new { error = err }) : Results.Ok());
app.MapDelete("/api/claude/hook", (ConfigStore cfg) => ClaudeHook.Configure(false, Port, cfg.Get().Claude.TimeoutSec) is { } err ? Results.BadRequest(new { error = err }) : Results.Ok());

// The recent messages and steps of one running Claude Code session, for the phone's session details. Off until the user
// turns on Settings › Claude Code › "Show the conversation on the phone"; only a session the monitor currently sees.
app.MapGet("/api/work/conversation", (string id, ConfigStore cfg, SessionMonitor sessions) =>
{
    // a session inside WSL keeps its chat file in the distro's home; a distro that stopped is not read (that would start it)
    var home = sessions.ConversationHome(id, out var usable);
    return Results.Json(ClaudeTranscript.ForPhone(cfg.Get().Claude.ShareConversation, usable ? sessions.Snapshot() : [], id, home));
});

// Codex uses the same PermissionRequest decision contract, through ~/.codex/hooks.json.
app.MapPost(CodexHook.Endpoint, async (HttpRequest req, ConfigStore cfg, ApprovalStore approvals, SessionMonitor sessions) =>
{
    JsonElement input;
    try { using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted); input = doc.RootElement.Clone(); }
    catch (JsonException) { return Results.Text(""); }
    var output = await CodexHook.HandleAsync(input, cfg, approvals, sessions, req.HttpContext.RequestAborted);
    return output is null ? Results.Text("") : Results.Json(output);
});
app.MapGet("/api/codex/hook", () => Results.Json(new { installed = CodexHook.Installed(), path = CodexHook.HooksPath }));
app.MapPost("/api/codex/hook", (ConfigStore cfg) => CodexHook.Configure(true, Port, cfg.Get().Claude.TimeoutSec) is { } err ? Results.BadRequest(new { error = err }) : Results.Ok());
app.MapDelete("/api/codex/hook", (ConfigStore cfg) => CodexHook.Configure(false, Port, cfg.Get().Claude.TimeoutSec) is { } err ? Results.BadRequest(new { error = err }) : Results.Ok());

// Cursor beforeShellExecution / beforeMCPExecution hooks (local only): the same phone decision in Cursor's own shape.
app.MapPost(CursorHook.Endpoint, async (HttpRequest req, ConfigStore cfg, ApprovalStore approvals, SessionMonitor sessions) =>
{
    JsonElement input;
    try { using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted); input = doc.RootElement.Clone(); }
    catch (JsonException) { return Results.Text(""); }
    var output = await CursorHook.HandleAsync(input, cfg, approvals, sessions, req.HttpContext.RequestAborted);
    return output is null ? Results.Text("") : Results.Json(output);
});
// installing in the distros (projects opened over Remote - WSL run their hooks there) follows "Also watch Cursor in
// WSL"; the distros installed to are recorded, so status and removal still find a hook there after turning it off
app.MapGet("/api/cursor/hook", (ConfigStore cfg) => Results.Json(CursorHook.Status(cfg.Get().Cursor.WatchWsl)));
app.MapPost("/api/cursor/hook", (ConfigStore cfg) => CursorHook.ConfigureAll(true, Port, cfg.Get().Claude.TimeoutSec, cfg.Get().Cursor.WatchWsl) is { } err ? Results.BadRequest(new { error = err }) : Results.Ok());
app.MapDelete("/api/cursor/hook", (ConfigStore cfg) => CursorHook.ConfigureAll(false, Port, cfg.Get().Claude.TimeoutSec, cfg.Get().Cursor.WatchWsl) is { } err ? Results.BadRequest(new { error = err }) : Results.Ok());

// Permission prompts waiting for the phone (posted by an AI coding agent hook).
app.MapGet("/api/approvals", (ApprovalStore a) => Results.Json(new { items = a.Snapshot() }));
app.MapPost("/api/approvals/{id}", (string id, string decision, string? value, ApprovalStore a) =>
    a.Decide(id, decision, value) ? Results.Ok() : Results.NotFound());

// Spectrum bars widget: each call keeps audio capture running for a few seconds and returns the latest bands
// (the live stream also sends them as "spectrum" events; panels without the stream poll this instead).
// Only band levels leave the agent, never audio; nothing is captured unless an enabled page has the widget.
app.MapGet("/api/spectrum", (SpectrumAnalyzer spectrum) =>
{
    var shown = spectrum.Want();
    return Results.Json(new { shown, active = spectrum.Active, fps = SpectrumAnalyzer.Fps, b = shown ? spectrum.Frame() : null });
});

// Output of a command widget (the command itself stays here).
app.MapGet("/api/widget/{page}/{widget}", (string page, string widget, bool? force, CommandRunner runner) =>
    runner.Get(page, widget, force == true) is { } r ? Results.Json(r) : Results.NotFound());

app.MapGet("/api/config", (ConfigStore cfg, HttpRequest req, ShortcutIconUploads icons, WallpaperStore wallpaper) =>
    Results.Json(new { version = cfg.Version.ToString(), config = cfg.Get().ToPublic(req.Headers["X-PhoneBox-Device"].ToString(),
        s => icons.Revision(s.Id) is { } up ? (up, true) : (ShortcutIcons.Revision(s), false), wallpaper.Revision()) }));

// The panel's wallpaper (Settings › Display). The panel asks with ?v=<revision>, so the picture may be kept for good.
app.MapGet("/api/wallpaper", (WallpaperStore wallpaper, HttpResponse res, string? v) =>
{
    if (wallpaper.Read() is not { } found) return Results.NotFound();
    // kept for good only when these very bytes are the revision asked for (the revision is their hash): an upload
    // between the config and this request must not leave the new picture cached under the old revision's address
    res.Headers.CacheControl = v == WallpaperStore.ContentRevision(found.Bytes).ToString(System.Globalization.CultureInfo.InvariantCulture)
        ? "private, max-age=31536000, immutable" : "no-store";
    res.Headers.XContentTypeOptions = "nosniff";
    return Results.File(found.Bytes, found.ContentType);
});
// Uploading and removing it: under /api/settings, so local only like the rest of the settings API. The version bump
// tells connected phones to load the config, and with it the new revision.
app.MapPost("/api/settings/wallpaper", async (HttpRequest req, ConfigStore cfg, WallpaperStore wallpaper) =>
{
    var configVersion = req.Headers["X-Config-Version"].ToString();
    if (cfg.VersionConflict(configVersion)) return Results.Json(new { error = ConfigStore.Conflict }, statusCode: StatusCodes.Status409Conflict);
    var bytes = await WallpaperStore.ReadBounded(req.Body);
    lock (IconCommit)
    {
        if (cfg.VersionConflict(configVersion)) return Results.Json(new { error = ConfigStore.Conflict }, statusCode: StatusCodes.Status409Conflict);
        var before = wallpaper.Revision();
        if (wallpaper.Save(bytes) is { } err)
        {
            // a save that failed halfway can still have changed the picture served: the panels must hear of it
            if (wallpaper.Revision() == before) return Results.BadRequest(new { error = err });
            cfg.Touch();
            return Results.Json(new { error = err, version = cfg.VersionText }, statusCode: StatusCodes.Status409Conflict);
        }
        cfg.Touch();
        return Results.Ok(new { version = cfg.VersionText });
    }
});
app.MapDelete("/api/settings/wallpaper", (HttpRequest req, ConfigStore cfg, WallpaperStore wallpaper) =>
{
    lock (IconCommit)
    {
        if (cfg.VersionConflict(req.Headers["X-Config-Version"])) return Results.Json(new { error = ConfigStore.Conflict }, statusCode: StatusCodes.Status409Conflict);
        var error = wallpaper.Remove();
        cfg.Touch(); // also when a file stayed: the panel must load whatever is there now
        return error is null ? Results.Ok(new { version = cfg.VersionText })
            : Results.Json(new { error, version = cfg.VersionText }, statusCode: StatusCodes.Status409Conflict);
    }
});

// Phones that have talked to this agent, with their settings (Settings › Phones).
app.MapGet("/api/phones", (ConfigStore cfg) =>
{
    var c = cfg.Get();
    var seen = PhoneRegistry.All().ToDictionary(p => p.Id);
    var ids = seen.Keys.Union(c.Phones.Select(p => p.Id));
    return Results.Json(ids.Select(id => new
    {
        id,
        model = seen.TryGetValue(id, out var s) ? s.Model : null,
        lastSeen = s?.LastSeen,
        via = s?.Via,
        webView = s?.WebView,
        webviewProblem = s is null ? null : WebViewDoctor.Diagnose(s.WebView, s.Model),
        name = c.Phones.FirstOrDefault(p => p.Id == id)?.Name,
        pages = c.Phones.FirstOrDefault(p => p.Id == id)?.Pages,
    }).OrderByDescending(p => p.lastSeen ?? 0));
});

app.MapGet("/api/art", (MediaMonitor media) =>
    media.Art is { } art ? Results.File(art.Bytes, art.Mime) : Results.NotFound());

// A Now playing or Synced lyrics widget on screen asks every few seconds: the agent reads the players every second
// only while one does (on macOS each reading starts nowplaying-cli, #523)
app.MapGet("/api/media", (MediaMonitor media, ControlDeck deck) => Results.Json(new { media = deck.EffectiveMedia(media.Snapshot()) }));
app.MapPost("/api/media/{action}", async (string action, long? ms, MediaMonitor media, ControlDeck deck, CancellationToken ct) =>
{
    var outcome = await deck.RunWithMediaFocusAsync(async token =>
    {
        var sysSnapshot = media.Snapshot();
        if (await deck.TryControlRadioMediaCoreAsync(action, sysSnapshot, token) is { } radioHandled)
            return (Ok: radioHandled, Media: deck.EffectiveMedia(media.Snapshot()));
        var starting = action is "next" or "prev" || (action == "playpause" && sysSnapshot?.Playing != true);
        var ok = await media.Control(action, ms);
        if (ok && starting) deck.StopAllRadios();
        return (Ok: ok, Media: deck.EffectiveMedia(media.Snapshot()));
    }, ct);
    StateSignal.Poke();
    return outcome.Ok ? Results.Json(new { ok = true, media = outcome.Media }) : Results.BadRequest();
});

app.MapPost("/api/volume", (int? level, string? op, int? step, AudioControl audio) =>
    Program.HandleVolume(level, op, step, audio));

app.MapGet("/api/quotas", (QuotaMonitor q) => Results.Json(new { providers = q.Snapshot(), overview = q.Overview() }));

app.MapGet("/api/work", (SessionMonitor s, GitHubMonitor gh) => Results.Json(WorkFeed.Build(s, gh)));

app.MapGet("/api/proxy", (string? url, CancellationToken ct) => WidgetFeedProxy.FetchAsync(url, ct));

app.MapGet("/api/procs", (ProcessMonitor procs, int? top) =>
{
    var all = procs.Snapshot();
    return Results.Json(new { total = all.Sum(g => g.Count), groupCount = all.Count, groups = all.Take(Math.Clamp(top ?? 40, 1, 200)) });
});

app.MapGet("/api/procs/icon", (string name, ProcessMonitor procs) =>
    procs.Icon(name) is { } png ? Results.File(png, "image/png") : Results.NotFound());

app.MapGet("/api/procs/detail", (string name, ProcessMonitor procs) => Results.Json(procs.Detail(name)));

// All apps widget: the installed applications, their icons, and opening one by id (never by path).
app.MapGet("/api/apps", (AppCatalog apps) => Results.Json(new
{
    supported = AppCatalog.Supported,
    canOpen = AppCatalog.Supported && apps.OpenAllowed(),
    apps = apps.List().Select(a => new { id = a.Id, name = a.Name }),
}));
app.MapGet("/api/apps/icon", async (string id, AppCatalog apps, HttpResponse res, CancellationToken ct) =>
{
    if (await apps.IconAsync(id, ct) is not { } png) return Results.NotFound();
    res.Headers.CacheControl = "private, max-age=86400"; // icons do not change; the phone may keep them
    return Results.File(png, "image/png");
});
app.MapPost("/api/apps/open", (string id, AppCatalog apps) =>
    apps.Open(id) is { } error ? Results.BadRequest(new { error }) : Results.Ok(new { ok = true }));

// ?name=X ends every instance of X; ?pid=N ends one process together with its children.
app.MapPost("/api/procs/kill", (string? name, int? pid, ProcessMonitor procs, ConfigStore cfg) =>
{
    if (!cfg.Get().AllowKill())
        return Results.Json(new KillResult(0, 0, "Terminating processes is disabled in the settings"), statusCode: 403);
    var r = pid is { } id ? procs.KillPid(id) : procs.Kill(name ?? "");
    return r.Killed > 0 ? Results.Json(r) : Results.Json(r, statusCode: 409);
});

app.MapPost("/api/run/{id}", (string id, bool? hold, ConfigStore cfg, PushStore push, AudioControl audio, ILogger<Program> log) =>
    RunShortcutForPhone(cfg.Get(), id, log, push, audio, hold == true));

// Real icon for a shortcut: an image the user uploaded (settings page) takes priority; otherwise its program's own
// icon, when its command is an actual file on disk. 404 when neither exists (shell addresses, protocol handlers,
// URLs, builtin: commands, and App Execution Alias stubs with no icon of their own), so the phone shows the emoji.
// upload=true: the upload only (see ShortcutIcons.Picture).
app.MapGet("/api/shortcuts/icon", (string id, bool? upload, ConfigStore cfg, ShortcutIconUploads icons, HttpResponse res) =>
{
    if (cfg.Get().Shortcuts.FirstOrDefault(s => s.Id == id) is not { } sc) return Results.NotFound();
    if (ShortcutIcons.Picture(sc, icons, upload == true) is not { } found) return Results.NotFound();
    res.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
    res.Headers["X-Content-Type-Options"] = "nosniff";
    return Results.File(found.Bytes, found.ContentType);
});

// Uploads a custom icon for a shortcut (PNG, WEBP or SVG, 512 KB max); replaces any previous upload for it.
// Under /api/settings, not /api/shortcuts: local-only like the rest of the settings API, since over Wi-Fi the
// phone only ever needs to read an icon (GET /api/shortcuts/icon, above), never write one.
app.MapPost("/api/settings/shortcut-icon", (string id, HttpRequest req, ConfigStore cfg, ShortcutIconUploads icons) =>
    UploadShortcutIconAsync(id, req.Headers["X-Config-Version"], req.Body, req.ContentType, cfg, icons));

// Removes a shortcut's uploaded icon; it falls back to its auto-detected program icon, or the emoji.
app.MapDelete("/api/settings/shortcut-icon", (string id, HttpRequest req, ConfigStore cfg, ShortcutIconUploads icons) =>
{
    lock (IconCommit)
    {
        if (!cfg.Get().Shortcuts.Any(s => s.Id == id)) return Results.NotFound();
        if (cfg.VersionConflict(req.Headers["X-Config-Version"])) return Results.Json(new { error = ConfigStore.Conflict }, statusCode: StatusCodes.Status409Conflict);
        icons.Remove(id);
        cfg.Touch();
        return Results.Ok(new { version = cfg.VersionText });
    }
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogInformation("PhoneDesk agent {Version} ready on http://127.0.0.1:{Port}, phone USB port {UsbPort} (config: {Path})",
        AppPaths.Version, Port, usb.Port, configPath);
    if (lanWebActive)
        app.Logger.LogInformation("PhoneDesk LAN web ready on http://0.0.0.0:{WebPort}", lanCfg.WebPort);
});

// macOS, running as PhoneBox.app: the main thread belongs to the menu bar icon and the host runs beside it (MenuBarHost)
if (!MenuBarHost.TryRun(app, args, endpoint, configDir)) app.Run();

// Restart requested from the settings page (e.g. Wi-Fi turned on): hand over to a fresh instance.
single.ReleaseMutex();
single.Dispose();
if (AppRestart.Requested && (AppRestart.Executable ?? Environment.ProcessPath) is { } exe)
{
    if (AppRestart.ServiceManaged(Environment.GetEnvironmentVariable, Environment.ProcessId, AppRestart.ParentName)) Environment.ExitCode = AppRestart.ServiceRestartCode;
    else System.Diagnostics.Process.Start(AppRestart.StartInfo(exe, args));
}

static void AddMonitor<T>(IServiceCollection s) where T : class, IHostedService
{
    s.AddSingleton<T>();
    s.AddHostedService(sp => sp.GetRequiredService<T>());
}

/// <summary>Body of POST /api/pair/claim.</summary>
internal sealed record PairClaim(string? Code);

public partial class Program
{
    internal static IResult HandleVolume(int? level, string? op, int? step, AudioControl audio)
    {
        if (step is <= 0 or > 100) return Results.BadRequest();
        var delta = step ?? 5;
        var ok = op switch
        {
            "mute" => audio.ToggleMute(),
            "up" => audio.Step(+delta),
            "down" => audio.Step(-delta),
            null or "" when level is { } l => audio.SetVolume(l),
            _ => false,
        };
        if (ok) StateSignal.Poke(); // the phone's other screens and widgets see it now, not at the next second
        return ok ? Results.Json(audio.Current()) : Results.BadRequest();
    }

    internal static bool ConfigHasLanWebConfig(string configPath)
    {
        try
        {
            if (!File.Exists(configPath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!prop.Name.Equals("lan", StringComparison.OrdinalIgnoreCase) || prop.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var lp in prop.Value.EnumerateObject())
                {
                    if (lp.Name.Equals("webEnabled", StringComparison.OrdinalIgnoreCase) || lp.Name.Equals("webPort", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch { }
        return false;
    }

    internal static string? ResolveWebRoot(string configDir, string? configuredWebRoot)
    {
        static bool HasIndex(string? dir) => !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) && File.Exists(Path.Combine(dir, "index.html"));
        if (HasIndex(configuredWebRoot)) return Path.GetFullPath(configuredWebRoot!);
        if (HasIndex(AppContext.BaseDirectory)) return Path.GetFullPath(AppContext.BaseDirectory);
        if (HasIndex(configDir)) return Path.GetFullPath(configDir);
        var baseWww = Path.Combine(AppContext.BaseDirectory, "www");
        if (HasIndex(baseWww)) return Path.GetFullPath(baseWww);
        var cur = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && cur is not null; i++, cur = cur.Parent)
        {
            if (File.Exists(Path.Combine(cur.FullName, "index.html")))
                return cur.FullName;
        }
        return null;
    }

    internal static string? RedactSettingsJson(AgentConfig config, bool includeLanWebSecrets)
    {
        var publicConfig = JsonSerializer.SerializeToNode(config, ConfigStore.WriteOpts)?.AsObject();
        if (publicConfig is null) return null;
        if (publicConfig["steam"] is JsonObject steam) steam.Remove("apiKey");
        if (includeLanWebSecrets)
        {
            if (publicConfig["lan"] is JsonObject lanObj) { lanObj.Remove("token"); lanObj.Remove("webhookToken"); }
            if (publicConfig["homeAssistant"] is JsonObject haObj) haObj.Remove("token");
            if (publicConfig["discord"] is JsonObject dcObj) dcObj.Remove("clientSecret");
            if (publicConfig["obs"] is JsonObject obsObj) obsObj.Remove("password");
            if (publicConfig["photos"] is JsonObject phObj) phObj.Remove("immichKey");
            if (publicConfig["controls"] is JsonObject ctrlObj) ctrlObj.Remove("plexToken");
            if (publicConfig["calendars"] is JsonArray calArr)
            {
                foreach (var item in calArr)
                    if (item is JsonObject calObj) calObj["url"] = "";
            }
            if (publicConfig["cameras"] is JsonObject camCfg && camCfg["sources"] is JsonArray camArr)
            {
                foreach (var item in camArr)
                    if (item is JsonObject camObj) camObj["url"] = "";
            }
        }
        return publicConfig.ToJsonString(ConfigStore.WriteOpts);
    }

    internal static bool IsBlockedWebAsset(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains("..") || Path.IsPathRooted(relativePath))
            return true;
        var fileName = Path.GetFileName(relativePath);
        if (string.IsNullOrEmpty(fileName)) return true;
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is ".bak" or ".tmp" or ".pfx") return true;
        if (fileName.StartsWith("config", StringComparison.OrdinalIgnoreCase) && ext == ".json") return true;
        if (ext == ".json" && !relativePath.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static IResult ServePanelAsset(string? file)
    {
        var target = string.IsNullOrEmpty(file) ? "index.html" : file;
        // index.html, main.js, style.css… and the widget modules in widgets/ (embedded www/ resources only)
        if (!System.Text.RegularExpressions.Regex.IsMatch(target, @"^(widgets/)?[A-Za-z0-9_.-]+$") || target.Contains(".."))
            return Results.NotFound();
        if (IsBlockedWebAsset(target)) return Results.NotFound();
        var type = Path.GetExtension(target).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".ico" => "image/x-icon",
            ".webp" => "image/webp",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream",
        };
        if (AppPaths.Resource("www/" + target) is { } s) return Results.Stream(s, type);
        return Results.NotFound();
    }

    internal static IResult ServeWebAsset(string? webRoot, string relativePath, string? embeddedFallback)
    {
        if (IsBlockedWebAsset(relativePath)) return Results.NotFound();
        var ext = Path.GetExtension(relativePath).ToLowerInvariant();
        var mime = ext switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" when relativePath.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) => "application/manifest+json; charset=utf-8",
            ".webmanifest" => "application/manifest+json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".ico" => "image/x-icon",
            ".webp" => "image/webp",
            ".woff2" => "font/woff2",
            _ => null,
        };
        if (mime is null) return Results.NotFound();
        if (!string.IsNullOrWhiteSpace(webRoot))
        {
            var rootFull = Path.GetFullPath(webRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(webRoot, relativePath));
            if (candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                return Results.File(candidate, mime);
        }
        if (embeddedFallback is not null && AppPaths.Resource(embeddedFallback) is { } s)
            return Results.Stream(s, mime);
        return Results.NotFound();
    }

    internal static bool LanPortCollidesWithLocal(string json, int localPort)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            // ConfigStore.Save deserializes a repeated member (in any letter case) last-wins, so the last "lan" is the
            // one that gets saved; a last one that is not an object leaves Wi-Fi off
            var collides = false;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!prop.Name.Equals("lan", StringComparison.OrdinalIgnoreCase)) continue;
                collides = false;
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                var enabled = false;
                var port = 8766;
                var webEnabled = true;
                var hasWebConfig = false;
                var webPort = 8770;
                foreach (var lp in prop.Value.EnumerateObject())
                {
                    if (lp.Name.Equals("enabled", StringComparison.OrdinalIgnoreCase) && lp.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        enabled = lp.Value.GetBoolean();
                    else if (lp.Name.Equals("port", StringComparison.OrdinalIgnoreCase) && lp.Value.ValueKind == JsonValueKind.Number && lp.Value.TryGetInt32(out var p))
                        port = Math.Clamp(p, 1024, 65535);
                    else if (lp.Name.Equals("webEnabled", StringComparison.OrdinalIgnoreCase) && lp.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        webEnabled = lp.Value.GetBoolean();
                        hasWebConfig = true;
                    }
                    else if (lp.Name.Equals("webPort", StringComparison.OrdinalIgnoreCase) && lp.Value.ValueKind == JsonValueKind.Number && lp.Value.TryGetInt32(out var wp))
                    {
                        webPort = Math.Clamp(wp <= 0 ? 8770 : wp, 1024, 65535);
                        hasWebConfig = true;
                    }
                }
                var effectiveWebEnabled = webEnabled && (localPort == 8765 || hasWebConfig);
                collides = (enabled && port == localPort) || (effectiveWebEnabled && webPort == localPort) || (enabled && effectiveWebEnabled && port == webPort);
            }
            return collides;
        }
        catch (JsonException) { }
        return false;
    }

    /// <summary>
    /// True when settings JSON gives the phone key and the webhook key the same value: every webhook sender would then
    /// hold the phone's key (Lan.AuthorizeRequest no longer trusts it as one). Checked on the settings page's save
    /// only, so other writes (a Note from the phone) still go through on a config that was edited by hand.
    /// </summary>
    internal static bool LanKeysShared(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            string? token = null, webhook = null;
            // last-wins for repeated members, like ConfigStore.Save; blank keys are replaced by new ones there
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!prop.Name.Equals("lan", StringComparison.OrdinalIgnoreCase)) continue;
                token = webhook = null;
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var lp in prop.Value.EnumerateObject())
                {
                    var text = lp.Value.ValueKind == JsonValueKind.String ? lp.Value.GetString() : null;
                    if (lp.Name.Equals("token", StringComparison.OrdinalIgnoreCase)) token = text;
                    else if (lp.Name.Equals("webhookToken", StringComparison.OrdinalIgnoreCase)) webhook = text;
                }
            }
            return !string.IsNullOrWhiteSpace(token) && string.Equals(token, webhook, StringComparison.Ordinal);
        }
        catch (JsonException) { }
        return false;
    }

    internal static async Task<IResult> UploadShortcutIconAsync(
        string id, string? configVersion, Stream body, string? contentType, ConfigStore cfg, ShortcutIconUploads icons)
    {
        if (!cfg.Get().Shortcuts.Any(s => s.Id == id)) return Results.NotFound(new { error = "Unknown shortcut (save it once first)" });
        if (cfg.VersionConflict(configVersion)) return Results.Json(new { error = ConfigStore.Conflict }, statusCode: StatusCodes.Status409Conflict);
        var bytes = await ShortcutIconUploads.ReadBounded(body);
        // The body is read outside the lock; the checks, the write and the version bump are one step, so of two
        // uploads that started from the same version the second gets 409 instead of replacing the first one's icon.
        lock (IconCommit)
        {
            if (cfg.VersionConflict(configVersion)) return Results.Json(new { error = ConfigStore.Conflict }, statusCode: StatusCodes.Status409Conflict);
            if (!cfg.Get().Shortcuts.Any(s => s.Id == id)) return Results.NotFound(new { error = "Unknown shortcut (save it once first)" });
            var err = icons.Save(id, contentType, bytes);
            if (err is not null) return Results.BadRequest(new { error = err });
            if (!cfg.Get().Shortcuts.Any(s => s.Id == id))
            {
                icons.Cleanup(() => cfg.Get().Shortcuts.Select(s => s.Id));
                return cfg.VersionConflict(configVersion)
                    ? Results.Json(new { error = ConfigStore.Conflict }, statusCode: StatusCodes.Status409Conflict)
                    : Results.NotFound(new { error = "Unknown shortcut (save it once first)" });
            }
            cfg.Touch();
            return Results.Ok(new { version = cfg.VersionText });
        }
    }

    /// <summary>
    /// Serializes the writes that move <see cref="ConfigStore.Version"/> for shortcut icons (upload, removal) with the
    /// settings save. Taken before the icon store's own lock, never inside it (its cleanup reads the settings).
    /// </summary>
    internal static readonly Lock IconCommit = new();

    /// <summary>
    /// POST /api/pair/claim. Wi-Fi is checked again once the body has arrived, with the settings that answer the claim:
    /// a claim still waiting for its body when Wi-Fi is turned off is refused and does not use up the code.
    /// </summary>
    internal static async Task<IResult> ClaimPairingAsync(HttpRequest req, ConfigStore cfg, PairingCodes codes,
        System.Security.Cryptography.X509Certificates.X509Certificate2? lanCert, int lanPort, ILogger log)
    {
        if (!cfg.Get().Lan.Enabled || lanCert is null || req.ContentLength is > 1024) return Results.StatusCode(StatusCodes.Status403Forbidden);
        var code = await PairingCodes.ReadClaimCodeAsync(req);
        var lan = cfg.Get().Lan;
        if (!lan.Enabled || code is null || !codes.Claim(code, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        log.LogInformation("phone paired over Wi-Fi ({Model})", req.Headers["X-PhoneBox-Model"].ToString());
        return Results.Json(new
        {
            enabled = true,
            pc = Environment.MachineName,
            port = lanPort,
            token = lan.Token,
            fingerprint = Lan.Fingerprint(lanCert),
            hosts = Lan.Addresses(),
            macs = WakeOnLan.LocalMacs(),
        });
    }

    /// <summary>
    /// POST /api/run/{id}. Why a shortcut failed stays in the agent log: an exception text can carry the configured
    /// command, its arguments, a key sequence or a working folder (Process.Start names the program and the folder).
    /// </summary>
    internal static IResult RunShortcutForPhone(AgentConfig cfg, string id, ILogger log, PushStore push, AudioControl audio, bool hold = false) =>
        Shortcuts.Run(cfg, id, log, push, audio, hold) is { } error ? Results.Problem(HintOrGeneric(error)) : Results.Ok();

    /// <summary>
    /// What the phone may see of a failed shortcut: an error can carry configured command text, so only the agent's own
    /// fixed hint (it tells the user what to do) goes through as it is; anything else becomes a generic message.
    /// </summary>
    internal static bool IsLanWebHeartbeatPath(PathString path) =>
        path == "/api/state" || path == "/api/config" || path == "/api/media"
        || path == "/api/controls/state" || path == "/api/calendar" || path == "/api/feeds"
        || path == "/api/phonedesk/weather" || path == "/api/phonedesk/version"
        || path == "/api/volume" || path.StartsWithSegments("/api/run");

    internal static string ModelFromUserAgent(string? ua, IPAddress remote)
    {
        if (!string.IsNullOrWhiteSpace(ua))
        {
            var m = System.Text.RegularExpressions.Regex.Match(ua, @"Android[^;)]*;\s*([^;)]+?)\s*(?:Build/|[;)])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var token = m.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(token) && !token.Equals("K", StringComparison.OrdinalIgnoreCase))
                    return token.Length > 40 ? token[..40] : token;
            }
        }
        return $"Web Panel ({remote})";
    }

    internal static List<object> ScanInstalledWindowsApps()
    {
        if (!OperatingSystem.IsWindows()) return [];
        string?[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        ];
        string[] skip = ["uninstall", "desinstalar", "remover", "help", "ajuda", "readme", "leia-me", "documentation"];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apps = new List<(string Name, string Path)>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(file);
                    if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var name = Path.GetFileNameWithoutExtension(file).Trim();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (skip.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
                    if (!seen.Add(name)) continue;
                    apps.Add((name, file));
                }
            }
            catch { }
        }
        return apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => (object)new { name = a.Name, path = a.Path })
            .ToList();
    }

    internal static string HintOrGeneric(string error) =>
        error.Contains(KeySender.AccessibilityHint, StringComparison.Ordinal) ? KeySender.AccessibilityHint
        : error.Contains(KeySender.UipiHint, StringComparison.Ordinal) ? KeySender.UipiHint
        : "The shortcut failed. The agent log on the computer has the details.";
}
