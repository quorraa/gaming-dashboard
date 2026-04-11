using Microsoft.AspNetCore.Mvc;
using Monitor.DiscordRelay.Models;
using Monitor.DiscordRelay.Services;

var builder = WebApplication.CreateBuilder(args);

var relaySettings = new DiscordRelaySettings();
builder.Configuration.GetSection("DiscordRelay").Bind(relaySettings);
builder.Services.AddSingleton(relaySettings);
builder.Services.AddSingleton<DiscordGatewayService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DiscordGatewayService>());

var app = builder.Build();

app.MapGet("/health", (DiscordGatewayService gatewayService) =>
    Results.Json(new
    {
        status = gatewayService.ConnectionState
    }));

app.MapGet("/api/discord", async (
    HttpContext context,
    [AsParameters] DiscordQuery query,
    DiscordGatewayService gatewayService,
    DiscordRelaySettings settings,
    CancellationToken cancellationToken) =>
{
    if (!IsAuthorized(context, settings))
    {
        return Results.Unauthorized();
    }

    var snapshot = await gatewayService.ReadAsync(query, cancellationToken);
    return Results.Json(snapshot);
});

app.Run();

static bool IsAuthorized(HttpContext context, DiscordRelaySettings settings)
{
    if (string.IsNullOrWhiteSpace(settings.ApiKey))
    {
        return true;
    }

    return context.Request.Headers.TryGetValue("X-Relay-Key", out var provided)
        && string.Equals(provided.ToString(), settings.ApiKey, StringComparison.Ordinal);
}
