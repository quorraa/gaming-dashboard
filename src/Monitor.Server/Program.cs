using Monitor.Server.Config;
using Monitor.Server.Models;
using Monitor.Server.Services;
using Monitor.Server.Services.Audio;
using Monitor.Server.Services.Discord;
using Monitor.Server.Services.System;
using Monitor.Server.Services.Temp;
using Microsoft.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton<HwInfoProcessService>();
builder.Services.AddHttpClient<HwInfoClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(2);
});
builder.Services.AddHttpClient<DiscordCollector>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(4);
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

app.MapGet("/api/snapshot", (DashboardStateStore store) => Results.Json(store.Current));
app.MapGet("/api/settings", (DashboardPreferencesStore preferencesStore, AudioMixerService audioMixerService) =>
    Results.Json(preferencesStore.GetEditorState(audioMixerService.GetAvailableSessionNames())));
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
    return Results.Json(preferencesStore.GetEditorState(audioMixerService.GetAvailableSessionNames()));
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
