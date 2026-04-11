using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Monitor.Server.Models;

namespace Monitor.Server.Services.Discord;

public sealed class DiscordCollector(
    HttpClient httpClient,
    DashboardPreferencesStore preferencesStore,
    ILogger<DiscordCollector> logger)
{
    public async Task<DiscordSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var config = preferencesStore.Current.Discord;
        if (!config.Enabled)
        {
            return DiscordSnapshot.Disabled(string.Empty);
        }

        var configurationWarning = ValidateConfiguration(config);
        if (configurationWarning is not null)
        {
            return new DiscordSnapshot(true, "setup", null, null, [], [], configurationWarning);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildRelayUri(config));
            if (!string.IsNullOrWhiteSpace(config.ApiKey))
            {
                request.Headers.Add("X-Relay-Key", config.ApiKey);
            }

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new DiscordSnapshot(true, "relay", null, null, [], [], "Relay API key rejected.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new DiscordSnapshot(true, "relay", null, null, [], [], $"Relay request failed ({(int)response.StatusCode}).");
            }

            var snapshot = await response.Content.ReadFromJsonAsync<DiscordSnapshot>(cancellationToken: cancellationToken);
            return snapshot ?? new DiscordSnapshot(true, "relay", null, null, [], [], "Relay returned no Discord data.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Discord relay request failed.");
            return new DiscordSnapshot(true, "relay", null, null, [], [], "Discord relay unavailable.");
        }
    }

    private static string? ValidateConfiguration(DiscordPreferencesSnapshot config)
    {
        if (string.IsNullOrWhiteSpace(config.RelayUrl))
        {
            return "Discord relay URL missing.";
        }

        if (ParseId(config.GuildId) == 0)
        {
            return "Discord guild ID missing.";
        }

        if (ParseId(config.TrackedUserId) == 0 && ParseId(config.VoiceChannelId) == 0)
        {
            return "Tracked user ID or fallback voice channel ID required.";
        }

        return null;
    }

    private static string BuildRelayUri(DiscordPreferencesSnapshot config)
    {
        var baseUrl = config.RelayUrl.Trim().TrimEnd('/');
        var query = new Dictionary<string, string?>
        {
            ["guildId"] = NormalizeQueryId(config.GuildId),
            ["messagesChannelId"] = NormalizeQueryId(config.MessagesChannelId),
            ["voiceChannelId"] = NormalizeQueryId(config.VoiceChannelId),
            ["trackedUserId"] = NormalizeQueryId(config.TrackedUserId),
            ["latestMessagesCount"] = config.LatestMessagesCount.ToString(),
            ["favoriteUserIds"] = string.Join(",", config.FavoriteUserIds.Where(id => !string.IsNullOrWhiteSpace(id)))
        };

        return QueryHelpers.AddQueryString($"{baseUrl}/api/discord", query);
    }

    private static ulong ParseId(string value)
    {
        return ulong.TryParse(value, out var parsed) ? parsed : 0;
    }

    private static string? NormalizeQueryId(string value)
    {
        return ParseId(value) == 0 ? null : value;
    }
}
