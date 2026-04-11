using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Monitor.DiscordRelay.Models;

namespace Monitor.DiscordRelay.Services;

public sealed class DiscordGatewayService(
    DiscordRelaySettings settings,
    ILogger<DiscordGatewayService> logger) : IHostedService, IAsyncDisposable
{
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly ConcurrentDictionary<string, MessageCacheEntry> _messageCache = new(StringComparer.Ordinal);
    private DiscordSocketClient? _client;
    private bool _attemptedStartup;

    public string ConnectionState =>
        _client?.ConnectionState.ToString().ToLowerInvariant()
        ?? (_attemptedStartup ? "connecting" : "setup");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await ResetClientAsync();
    }

    public async Task<DiscordSnapshot> ReadAsync(DiscordQuery query, CancellationToken cancellationToken)
    {
        var configurationWarning = ValidateQuery(query);
        if (configurationWarning is not null)
        {
            return new DiscordSnapshot(true, "setup", null, null, [], [], configurationWarning);
        }

        var startupWarning = await EnsureConnectedAsync(cancellationToken);
        if (startupWarning is not null)
        {
            return new DiscordSnapshot(true, "setup", null, null, [], [], startupWarning);
        }

        if (_client is null)
        {
            return new DiscordSnapshot(true, "connecting", null, null, [], [], "Discord client not initialized.");
        }

        var guild = _client.GetGuild(ParseId(query.GuildId));
        if (guild is null)
        {
            return new DiscordSnapshot(true, ConnectionState, null, null, [], [], "Configured guild not found.");
        }

        var trackedUserId = ParseId(query.TrackedUserId);
        var trackedUser = ResolveUser(guild, trackedUserId);
        var favoriteUsers = ParseIds(query.FavoriteUserIds)
            .Select(userId => ResolveUser(guild, userId))
            .Where(user => user is not null)
            .Cast<DiscordUserCard>()
            .ToArray();

        VoiceChannelSnapshot? voice = null;
        var trackedMember = trackedUserId == 0 ? null : guild.GetUser(trackedUserId);
        var voiceChannel = trackedMember?.VoiceChannel ?? guild.GetVoiceChannel(ParseId(query.VoiceChannelId));
        if (voiceChannel is not null)
        {
            var members = voiceChannel.ConnectedUsers
                .Select(MapUser)
                .OrderBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            voice = new VoiceChannelSnapshot(voiceChannel.Name, members.Length, members);
        }

        var messages = await ReadMessagesAsync(guild, query, cancellationToken);
        return new DiscordSnapshot(
            true,
            ConnectionState,
            trackedUser,
            voice,
            favoriteUsers,
            messages,
            BuildWarning());
    }

    private async Task<string?> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.BotToken))
        {
            return "Discord relay bot token missing.";
        }

        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (_attemptedStartup && _client is not null)
            {
                return null;
            }

            _attemptedStartup = true;
            var socketConfig = new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds
                    | GatewayIntents.GuildMembers
                    | GatewayIntents.GuildMessages
                    | GatewayIntents.GuildVoiceStates
                    | GatewayIntents.GuildPresences
                    | GatewayIntents.MessageContent
            };

            _client = new DiscordSocketClient(socketConfig);
            _client.Log += message =>
            {
                logger.LogInformation("Discord relay {Severity}: {Message}", message.Severity, message.Message);
                return Task.CompletedTask;
            };

            await _client.LoginAsync(TokenType.Bot, settings.BotToken);
            await _client.StartAsync();
            await Task.Delay(settings.StartupDelayMs, cancellationToken);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Discord relay startup failed.");
            await ResetClientAsync();
            return "Discord relay could not connect to Discord.";
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task<IReadOnlyList<DiscordMessageCard>> ReadMessagesAsync(SocketGuild guild, DiscordQuery query, CancellationToken cancellationToken)
    {
        var channelId = ParseId(query.MessagesChannelId);
        var limit = Math.Clamp(query.LatestMessagesCount ?? 6, 1, 20);
        if (channelId == 0)
        {
            return [];
        }

        var cacheKey = $"{guild.Id}:{channelId}:{limit}";
        if (_messageCache.TryGetValue(cacheKey, out var cacheEntry)
            && cacheEntry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cacheEntry.Messages;
        }

        var channel = guild.GetTextChannel(channelId);
        if (channel is null)
        {
            return [];
        }

        try
        {
            var messages = await channel.GetMessagesAsync(limit: limit).FlattenAsync();
            var items = messages
                .Select(message => new DiscordMessageCard(
                    message.Id.ToString(),
                    message.Author.GlobalName ?? message.Author.Username,
                    string.IsNullOrWhiteSpace(message.Content) ? "[embed or attachment]" : message.Content,
                    FormatRelativeTime(message.Timestamp)))
                .OrderByDescending(message => ulong.Parse(message.Id))
                .ToArray();

            _messageCache[cacheKey] = new MessageCacheEntry(
                DateTimeOffset.UtcNow.AddSeconds(Math.Max(settings.MessageCacheSeconds, 1)),
                items);
            return items;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Discord relay failed reading messages.");
            return [];
        }
    }

    private static DiscordUserCard? ResolveUser(SocketGuild guild, ulong userId)
    {
        if (userId == 0)
        {
            return null;
        }

        var user = guild.GetUser(userId);
        return user is null ? null : MapUser(user);
    }

    private static DiscordUserCard MapUser(SocketGuildUser user)
    {
        var activity = user.Activities.FirstOrDefault()?.Name;
        return new DiscordUserCard(
            user.Id.ToString(),
            user.GlobalName ?? user.DisplayName ?? user.Username,
            user.Status.ToString().ToLowerInvariant(),
            StatusAccent(user.Status),
            activity,
            user.IsMuted || user.IsSelfMuted,
            user.IsDeafened || user.IsSelfDeafened,
            null);
    }

    private string? BuildWarning()
    {
        if (_client is null || _client.LoginState != LoginState.LoggedIn)
        {
            return "Discord relay is not logged in.";
        }

        return null;
    }

    private async Task ResetClientAsync()
    {
        if (_client is not null)
        {
            try
            {
                await _client.StopAsync();
                await _client.LogoutAsync();
            }
            catch
            {
            }

            _client.Dispose();
        }

        _client = null;
        _attemptedStartup = false;
        _messageCache.Clear();
    }

    private static string? ValidateQuery(DiscordQuery query)
    {
        if (ParseId(query.GuildId) == 0)
        {
            return "Discord guild ID missing.";
        }

        if (ParseId(query.TrackedUserId) == 0 && ParseId(query.VoiceChannelId) == 0)
        {
            return "Tracked user ID or fallback voice channel ID required.";
        }

        return null;
    }

    private static ulong ParseId(string? value)
    {
        return ulong.TryParse(value, out var parsed) ? parsed : 0;
    }

    private static IReadOnlyList<ulong> ParseIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseId)
            .Where(id => id != 0)
            .Distinct()
            .ToArray();
    }

    private static string StatusAccent(UserStatus status) => status switch
    {
        UserStatus.Online => "good",
        UserStatus.Idle => "warning",
        UserStatus.DoNotDisturb => "danger",
        _ => "muted"
    };

    private static string FormatRelativeTime(DateTimeOffset timestamp)
    {
        var delta = DateTimeOffset.UtcNow - timestamp;
        if (delta.TotalMinutes < 1)
        {
            return "now";
        }

        if (delta.TotalHours < 1)
        {
            return $"{(int)delta.TotalMinutes}m ago";
        }

        if (delta.TotalDays < 1)
        {
            return $"{(int)delta.TotalHours}h ago";
        }

        return $"{(int)delta.TotalDays}d ago";
    }

    public async ValueTask DisposeAsync()
    {
        await ResetClientAsync();
    }

    private sealed record MessageCacheEntry(DateTimeOffset ExpiresAt, IReadOnlyList<DiscordMessageCard> Messages);
}
