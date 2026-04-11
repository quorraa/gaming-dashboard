using System.Security.Cryptography;
using System.Text;
using Discord;
using Discord.WebSocket;
using Monitor.Server.Models;

namespace Monitor.Server.Services.Discord;

public sealed class DiscordCollector(DashboardPreferencesStore preferencesStore, ILogger<DiscordCollector> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _sync = new(1, 1);
    private DiscordSocketClient? _client;
    private bool _attemptedStartup;
    private string? _configSignature;

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

        await EnsureConnectedAsync(config, cancellationToken);
        if (_client is null)
        {
            return new DiscordSnapshot(true, "connecting", null, null, [], [], "Discord client not initialized.");
        }

        var guild = _client.GetGuild(ParseId(config.GuildId));
        if (guild is null)
        {
            return new DiscordSnapshot(true, _client.ConnectionState.ToString().ToLowerInvariant(), null, null, [], [], "Configured guild not found.");
        }

        var trackedUserId = ParseId(config.TrackedUserId);
        var trackedUser = ResolveUser(guild, trackedUserId);
        var favoriteUsers = config.FavoriteUserIds
            .Select(ParseId)
            .Select(userId => ResolveUser(guild, userId))
            .Where(user => user is not null)
            .Cast<DiscordUserCard>()
            .ToArray();

        VoiceChannelSnapshot? voice = null;
        var trackedMember = trackedUserId == 0 ? null : guild.GetUser(trackedUserId);
        var voiceChannel = trackedMember?.VoiceChannel ?? guild.GetVoiceChannel(ParseId(config.VoiceChannelId));
        if (voiceChannel is not null)
        {
            var members = voiceChannel.ConnectedUsers
                .Select(MapUser)
                .OrderBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            voice = new VoiceChannelSnapshot(voiceChannel.Name, members.Length, members);
        }

        var messages = await ReadMessagesAsync(guild, config);
        return new DiscordSnapshot(
            true,
            _client.ConnectionState.ToString().ToLowerInvariant(),
            trackedUser,
            voice,
            favoriteUsers,
            messages,
            BuildWarning(_client));
    }

    private async Task EnsureConnectedAsync(DiscordPreferencesSnapshot config, CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            var signature = BuildConfigSignature(config);
            if (_configSignature is not null && !string.Equals(_configSignature, signature, StringComparison.Ordinal))
            {
                await ResetClientAsync();
            }

            if (_attemptedStartup && _client is not null)
            {
                return;
            }

            _attemptedStartup = true;
            _configSignature = signature;
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
                logger.LogInformation("Discord {Severity}: {Message}", message.Severity, message.Message);
                return Task.CompletedTask;
            };

            await _client.LoginAsync(TokenType.Bot, config.Token);
            await _client.StartAsync();
            await Task.Delay(2500, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Discord startup failed.");
            await ResetClientAsync();
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task<IReadOnlyList<DiscordMessageCard>> ReadMessagesAsync(SocketGuild guild, DiscordPreferencesSnapshot config)
    {
        var channel = guild.GetTextChannel(ParseId(config.MessagesChannelId));
        if (channel is null)
        {
            return [];
        }

        try
        {
            var messages = await channel.GetMessagesAsync(limit: config.LatestMessagesCount).FlattenAsync();
            return messages
                .Select(message => new DiscordMessageCard(
                    message.Id.ToString(),
                    message.Author.GlobalName ?? message.Author.Username,
                    string.IsNullOrWhiteSpace(message.Content) ? "[embed or attachment]" : message.Content,
                    FormatRelativeTime(message.Timestamp)))
                .OrderByDescending(message => ulong.Parse(message.Id))
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed reading Discord messages.");
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

    private static string StatusAccent(UserStatus status) => status switch
    {
        UserStatus.Online => "good",
        UserStatus.Idle => "warning",
        UserStatus.DoNotDisturb => "danger",
        _ => "muted"
    };

    private static string? BuildWarning(DiscordSocketClient client)
    {
        if (client.LoginState != LoginState.LoggedIn)
        {
            return "Discord bot is not logged in.";
        }

        return null;
    }

    private static string? ValidateConfiguration(DiscordPreferencesSnapshot config)
    {
        if (string.IsNullOrWhiteSpace(config.Token))
        {
            return "Discord token missing.";
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
        _configSignature = null;
    }

    private static ulong ParseId(string value)
    {
        return ulong.TryParse(value, out var parsed) ? parsed : 0;
    }

    private static string BuildConfigSignature(DiscordPreferencesSnapshot config)
    {
        var payload = string.Join("|",
            config.Enabled,
            config.Token,
            config.GuildId,
            config.MessagesChannelId,
            config.VoiceChannelId,
            config.TrackedUserId,
            config.LatestMessagesCount,
            string.Join(",", config.FavoriteUserIds));

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes);
    }

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
}
