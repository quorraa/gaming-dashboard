namespace Monitor.DiscordRelay.Models;

public sealed record DiscordSnapshot(
    bool Enabled,
    string ConnectionState,
    DiscordUserCard? TrackedUser,
    VoiceChannelSnapshot? VoiceChannel,
    IReadOnlyList<DiscordUserCard> FavoriteUsers,
    IReadOnlyList<DiscordMessageCard> LatestMessages,
    string? Warning);

public sealed record DiscordUserCard(
    string Id,
    string Name,
    string Status,
    string Accent,
    string? Activity,
    bool IsMuted,
    bool IsDeafened,
    bool? IsSpeaking);

public sealed record VoiceChannelSnapshot(
    string Name,
    int MemberCount,
    IReadOnlyList<DiscordUserCard> Members);

public sealed record DiscordMessageCard(
    string Id,
    string Author,
    string Content,
    string RelativeTime);

public sealed class DiscordQuery
{
    public string GuildId { get; set; } = string.Empty;
    public string? MessagesChannelId { get; set; }
    public string? VoiceChannelId { get; set; }
    public string TrackedUserId { get; set; } = string.Empty;
    public int? LatestMessagesCount { get; set; }
    public string? FavoriteUserIds { get; set; }
}

public sealed class DiscordRelaySettings
{
    public string BotToken { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public int StartupDelayMs { get; set; } = 2500;
    public int MessageCacheSeconds { get; set; } = 4;
}
