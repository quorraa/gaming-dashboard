using Monitor.Server.Config;
using Monitor.Server.Models;
using Monitor.Server.Services;
using Monitor.Server.Services.Audio;
using Monitor.Server.Services.Discord;
using Monitor.Server.Services.Spotify;
using Monitor.Server.Services.System;
using Monitor.Server.Services.Temp;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

var baseDirectory = AppContext.BaseDirectory;
var bundledWebRoot = Path.Combine(baseDirectory, "wwwroot");
if (Directory.Exists(bundledWebRoot))
{
    Directory.SetCurrentDirectory(baseDirectory);
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Directory.Exists(bundledWebRoot) ? baseDirectory : Directory.GetCurrentDirectory()
});

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
    && string.IsNullOrWhiteSpace(builder.Configuration["urls"])
    && string.IsNullOrWhiteSpace(builder.Configuration["ASPNETCORE_URLS"]))
{
    builder.WebHost.UseUrls("http://0.0.0.0:5103");
}

var dashboardSettings = new DashboardSettings();
builder.Configuration.GetSection("Dashboard").Bind(dashboardSettings);
if (dashboardSettings.HwInfo.Sensors.Count == 0)
{
    dashboardSettings.HwInfo.Sensors = HwInfoSettings.DefaultSensors();
}
builder.Services.AddSingleton(dashboardSettings);
builder.Services.AddSingleton<DashboardPreferencesStore>();
builder.Services.AddSingleton<DashboardStateStore>();
builder.Services.AddSingleton<SystemInfoCollector>();
builder.Services.AddSingleton<NetworkCollector>();
builder.Services.AddSingleton<ProcessCollector>();
builder.Services.AddSingleton<AudioMixerService>();
builder.Services.AddSingleton<ThemeMediaService>();
builder.Services.AddSingleton<HwInfoProcessService>();
builder.Services.AddHttpClient<HwInfoClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(2);
});
builder.Services.AddHttpClient<DiscordCollector>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(4);
});
builder.Services.AddHttpClient(nameof(SpotifyService), client =>
{
    client.Timeout = TimeSpan.FromSeconds(8);
});
builder.Services.AddHttpClient(nameof(PexelsService), client =>
{
    client.Timeout = TimeSpan.FromSeconds(12);
});
builder.Services.AddSingleton<PexelsService>();
builder.Services.AddSingleton<SpotifyService>();
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 1024L * 1024 * 1024;
});
builder.Services.AddSingleton<DashboardSnapshotBuilder>();
builder.Services.AddSingleton<DashboardSocketServer>();
builder.Services.AddHostedService<DashboardPollingService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers[HeaderNames.CacheControl] = "no-store, no-cache, must-revalidate";
        context.Context.Response.Headers[HeaderNames.Pragma] = "no-cache";
        context.Context.Response.Headers[HeaderNames.Expires] = "0";
    }
});
app.UseWebSockets();

app.MapGet("/", () => Results.Redirect("/studio/index.html"));
app.MapGet("/favicon.ico", () => Results.NoContent());
app.MapGet("/api/snapshot", (DashboardStateStore store) => Results.Json(store.Current));
app.MapGet("/api/settings", (DashboardPreferencesStore preferencesStore, AudioMixerService audioMixerService) =>
    Results.Json(preferencesStore.GetEditorState(
        audioMixerService.GetAvailableSessionNames(),
        audioMixerService.GetEditorInventory())));
app.MapPost("/api/settings", async (
    DashboardPreferencesUpdate update,
    DashboardPreferencesStore preferencesStore,
    AudioMixerService audioMixerService,
    DashboardSnapshotBuilder snapshotBuilder,
    DashboardSocketServer socketServer,
    HttpContext context) =>
{
    preferencesStore.Update(update);
    var snapshot = await snapshotBuilder.BuildAsync(context.RequestAborted);
    await socketServer.BroadcastSnapshotAsync(snapshot, context.RequestAborted);
    return Results.Json(preferencesStore.GetEditorState(
        audioMixerService.GetAvailableSessionNames(),
        audioMixerService.GetEditorInventory()));
});
app.MapGet("/api/media/library", (ThemeMediaService themeMediaService) =>
    Results.Json(themeMediaService.ListAssets()));
app.MapPost("/api/media/upload", async (
    HttpRequest request,
    ThemeMediaService themeMediaService,
    CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "Multipart form data is required." });
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files["file"] ?? form.Files.FirstOrDefault();
    if (file is null)
    {
        return Results.BadRequest(new { error = "No file was uploaded." });
    }

    try
    {
        var asset = await themeMediaService.SaveUploadAsync(file, cancellationToken);
        return Results.Json(asset);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});
app.MapDelete("/api/media/local/{id}", (string id, ThemeMediaService themeMediaService) =>
    themeMediaService.DeleteAsset(id) ? Results.NoContent() : Results.NotFound());
app.MapGet("/api/media/local/{id}", (string id, ThemeMediaService themeMediaService) =>
{
    if (!themeMediaService.TryResolveAsset(id, out var fullPath, out _, out var downloadName))
    {
        return Results.NotFound();
    }

    var provider = new FileExtensionContentTypeProvider();
    if (!provider.TryGetContentType(fullPath, out var contentType))
    {
        contentType = "application/octet-stream";
    }

    return Results.File(fullPath, contentType, enableRangeProcessing: true);
});
app.MapGet("/api/media/pexels/search", async (
    string query,
    string? mediaKind,
    int? page,
    int? perPage,
    DashboardPreferencesStore preferencesStore,
    PexelsService pexelsService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var apiKey = preferencesStore.Current.Theme.PexelsApiKey;
        var results = await pexelsService.SearchAsync(
            apiKey,
            mediaKind ?? "image",
            query,
            page ?? 1,
            perPage ?? 18,
            cancellationToken);
        return Results.Json(results);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (HttpRequestException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});
app.MapGet("/api/media/pexels/stream", async (
    string url,
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    PexelsService pexelsService,
    CancellationToken cancellationToken) =>
{
    if (!pexelsService.IsAllowedProxyUrl(url))
    {
        return Results.BadRequest(new { error = "Unsupported media host." });
    }

    using var request = new HttpRequestMessage(HttpMethod.Get, url);
    using var response = await httpClientFactory.CreateClient(nameof(PexelsService))
        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        return Results.StatusCode((int)response.StatusCode);
    }

    var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
    var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    app.Logger.LogDebug("Proxying Pexels asset {Url}", url);
    context.Response.ContentType = contentType;
    if (response.Content.Headers.ContentLength is long contentLength)
    {
        context.Response.ContentLength = contentLength;
    }

    await using (stream)
    {
        await stream.CopyToAsync(context.Response.Body, cancellationToken);
    }

    return Results.Empty;
});
app.MapGet("/api/spotify/connect/start", (HttpContext context, SpotifyService spotifyService, string? returnUrl) =>
{
    try
    {
        return Results.Redirect(spotifyService.CreateAuthorizationUri(context.Request, returnUrl).ToString());
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});
app.MapGet("/api/spotify/connect/callback", async (
    HttpContext context,
    string? code,
    string? state,
    SpotifyService spotifyService,
    DashboardSnapshotBuilder snapshotBuilder,
    DashboardSocketServer socketServer,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
    {
        return Results.Redirect("/studio/index.html?spotify=error");
    }

    try
    {
        var returnUrl = await spotifyService.CompleteAuthorizationAsync(code, state, cancellationToken);
        var snapshot = await snapshotBuilder.RefreshSpotifyAsync(cancellationToken);
        await socketServer.BroadcastSnapshotAsync(snapshot, cancellationToken);
        return Results.Redirect(AppendStatusQuery(returnUrl, "spotify", "connected"));
    }
    catch
    {
        return Results.Redirect("/studio/index.html?spotify=error");
    }
});
app.MapPost("/api/spotify/disconnect", async (
    DashboardPreferencesStore preferencesStore,
    DashboardSnapshotBuilder snapshotBuilder,
    DashboardSocketServer socketServer,
    CancellationToken cancellationToken) =>
{
    preferencesStore.ClearSpotifyAuthorization();
    var snapshot = await snapshotBuilder.RefreshSpotifyAsync(cancellationToken);
    await socketServer.BroadcastSnapshotAsync(snapshot, cancellationToken);
    return Results.Json(snapshot.Spotify);
});
app.MapPost("/api/spotify/command", async (
    SpotifyCommandRequest command,
    SpotifyService spotifyService,
    DashboardSnapshotBuilder snapshotBuilder,
    DashboardSocketServer socketServer,
    CancellationToken cancellationToken) =>
{
    try
    {
        await spotifyService.ExecuteCommandAsync(command, cancellationToken);
        var snapshot = await snapshotBuilder.RefreshSpotifyAsync(cancellationToken);
        await socketServer.BroadcastSnapshotAsync(snapshot, cancellationToken);
        return Results.Json(snapshot.Spotify);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});
app.Map("/ws", async (HttpContext context, DashboardSocketServer socketServer) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket request expected.");
        return;
    }

    await socketServer.AcceptAsync(context);
});

app.Run();

static string AppendStatusQuery(string path, string key, string value)
{
    var separator = path.Contains('?', StringComparison.Ordinal) ? "&" : "?";
    return $"{path}{separator}{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
}
