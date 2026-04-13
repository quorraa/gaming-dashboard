namespace Monitor.Server.Models;

public sealed record DashboardSnapshot(
    DateTimeOffset GeneratedAt,
    TempsSnapshot Temps,
    DiscordSnapshot Discord,
    SpotifySnapshot Spotify,
    NetworkSnapshot Network,
    SystemInfoSnapshot System,
    AudioMixerSnapshot Audio,
    ProcessesSnapshot Processes,
    UiSnapshot Ui)
{
    public static DashboardSnapshot Empty { get; } = new(
        DateTimeOffset.UtcNow,
        new TempsSnapshot([], "No sensor data yet."),
        DiscordSnapshot.Disabled("Discord integration disabled."),
        SpotifySnapshot.Disabled("Spotify integration disabled."),
        NetworkSnapshot.Empty,
        SystemInfoSnapshot.Empty,
        new AudioMixerSnapshot([], [], string.Empty, null),
        new ProcessesSnapshot([]),
        new UiSnapshot(
            ["temps", "network", "discord", "spotify", "audio", "processes", "system"],
            DashboardLayoutPreferencesSnapshot.Default,
            ThemePreferencesSnapshot.Default));
}

public sealed record TempsSnapshot(IReadOnlyList<TemperatureCard> Cards, string? Warning);

public sealed record TemperatureCard(
    string Key,
    string Label,
    double? Value,
    string Unit,
    double WarningThreshold,
    double DangerThreshold,
    string Severity,
    double FillPercent,
    string SourceName);

public sealed record DiscordSnapshot(
    bool Enabled,
    string ConnectionState,
    DiscordUserCard? TrackedUser,
    VoiceChannelSnapshot? VoiceChannel,
    IReadOnlyList<DiscordUserCard> FavoriteUsers,
    IReadOnlyList<DiscordMessageCard> LatestMessages,
    string? Warning)
{
    public static DiscordSnapshot Disabled(string warning) => new(false, "disabled", null, null, [], [], warning);
}

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

public sealed record SpotifySnapshot(
    bool Enabled,
    string ConnectionState,
    SpotifyNowPlayingSnapshot? NowPlaying,
    string? Warning)
{
    public static SpotifySnapshot Disabled(string warning) => new(false, "disabled", null, warning);
}

public sealed record SpotifyNowPlayingSnapshot(
    string ItemId,
    string MediaType,
    string Title,
    string Artist,
    string Album,
    string CoverUrl,
    string TrackUrl,
    string ArtistUrl,
    string AlbumUrl,
    bool IsPlaying,
    bool ShuffleEnabled,
    string RepeatState,
    int ProgressMs,
    int DurationMs,
    int VolumePercent,
    string DeviceId,
    string DeviceName,
    bool SupportsVolume,
    bool IsLiked);

public sealed record NetworkSnapshot(
    double DownloadMbps,
    double UploadMbps,
    double? PingMs,
    double? JitterMs,
    IReadOnlyList<double> DownloadHistory,
    IReadOnlyList<double> UploadHistory,
    string InterfaceLabel)
{
    public static NetworkSnapshot Empty { get; } = new(0, 0, null, null, [], [], "No active interface");
}

public sealed record SystemInfoSnapshot(
    string HostName,
    string Cpu,
    string Gpu,
    string Ram,
    string Board,
    string Os,
    string Monitor,
    string Uptime)
{
    public static SystemInfoSnapshot Empty { get; } = new(
        Environment.MachineName,
        "Detecting...",
        "Detecting...",
        "Detecting...",
        "Detecting...",
        "Detecting...",
        "Detecting...",
        "0m");
}

public sealed record AudioMixerSnapshot(
    IReadOnlyList<AudioSessionCard> Sessions,
    IReadOnlyList<AudioEndpointOption> Endpoints,
    string SelectedEndpointId,
    string? Warning);

public sealed record AudioEndpointOption(string Id, string Name, bool IsDefault);

public sealed record AudioSessionCard(
    string Id,
    string Name,
    int ProcessId,
    string Detail,
    int VolumePercent,
    bool IsMuted,
    bool IsSystemSound);

public sealed record ProcessesSnapshot(IReadOnlyList<ProcessCard> TopProcesses);

public sealed record UiSnapshot(
    IReadOnlyList<string> VisiblePanels,
    DashboardLayoutPreferencesSnapshot Layout,
    ThemePreferencesSnapshot Theme);

public sealed record PanelOption(string Key, string Label);

public sealed record DashboardEditorState(
    IReadOnlyList<PanelOption> AvailablePanels,
    DashboardPreferencesSnapshot Preferences,
    IReadOnlyList<string> AvailableAudioApps,
    AudioEditorInventorySnapshot AudioInventory);

public sealed record DashboardPreferencesSnapshot(
    IReadOnlyList<string> VisiblePanels,
    AudioPreferencesSnapshot Audio,
    DiscordPreferencesSnapshot Discord,
    SpotifyPreferencesSnapshot Spotify,
    DashboardLayoutPreferencesSnapshot Layout,
    ThemePreferencesSnapshot Theme);

public sealed record AudioPreferencesSnapshot(
    bool IncludeSystemSounds,
    int MaxSessions,
    bool ShowDeviceLabels,
    IReadOnlyList<string> VisibleSessionMatches,
    string SelectedEndpointId,
    IReadOnlyList<AudioVisibleTargetSnapshot> VisibleDeviceSessions);

public sealed record AudioVisibleTargetSnapshot(
    string EndpointId,
    string SessionName);

public sealed record AudioEditorInventorySnapshot(
    IReadOnlyList<AudioOutputDeviceSnapshot> OutputDevices,
    IReadOnlyList<AudioInputDeviceSnapshot> InputDevices);

public sealed record AudioOutputDeviceSnapshot(
    string Id,
    string Name,
    bool IsDefault,
    bool IsSelected,
    AudioSessionCard Master,
    IReadOnlyList<AudioSessionCard> Sessions);

public sealed record AudioInputDeviceSnapshot(
    string Id,
    string Name,
    bool IsDefault,
    int VolumePercent,
    bool IsMuted);

public sealed record DiscordPreferencesSnapshot(
    bool Enabled,
    string RelayUrl,
    string ApiKey,
    string ApiKeyHint,
    string GuildId,
    string MessagesChannelId,
    string VoiceChannelId,
    string TrackedUserId,
    int LatestMessagesCount,
    IReadOnlyList<string> FavoriteUserIds);

public sealed record SpotifyPreferencesSnapshot(
    bool Enabled,
    string ClientId,
    string RefreshToken,
    bool IsAuthorized);

public sealed record DashboardPreferencesUpdate(
    IReadOnlyList<string>? VisiblePanels,
    AudioPreferencesUpdate? Audio,
    DiscordPreferencesUpdate? Discord,
    SpotifyPreferencesUpdate? Spotify,
    DashboardLayoutPreferencesUpdate? Layout,
    ThemePreferencesUpdate? Theme);

public sealed record AudioPreferencesUpdate(
    bool? IncludeSystemSounds,
    int? MaxSessions,
    bool? ShowDeviceLabels,
    IReadOnlyList<string>? VisibleSessionMatches,
    string? SelectedEndpointId,
    IReadOnlyList<AudioVisibleTargetUpdate>? VisibleDeviceSessions);

public sealed record AudioVisibleTargetUpdate(
    string EndpointId,
    string SessionName);

public sealed record DashboardLayoutPreferencesSnapshot(
    DashboardLayoutModeSnapshot Desktop,
    DashboardLayoutModeSnapshot TabletLandscape,
    DashboardLayoutModeSnapshot PhoneLandscape)
{
    public static DashboardLayoutPreferencesSnapshot Default { get; } = new(
        new DashboardLayoutModeSnapshot(
            48,
            40,
            [
                new DashboardPanelLayoutSnapshot("temps", 1, 1, 32, 10),
                new DashboardPanelLayoutSnapshot("network", 33, 1, 16, 10),
                new DashboardPanelLayoutSnapshot("discord", 1, 11, 32, 10),
                new DashboardPanelLayoutSnapshot("spotify", 33, 11, 16, 10),
                new DashboardPanelLayoutSnapshot("audio", 1, 21, 16, 10),
                new DashboardPanelLayoutSnapshot("processes", 17, 21, 16, 10),
                new DashboardPanelLayoutSnapshot("system", 33, 21, 16, 10)
            ]),
        new DashboardLayoutModeSnapshot(
            48,
            40,
            [
                new DashboardPanelLayoutSnapshot("temps", 1, 1, 24, 20),
                new DashboardPanelLayoutSnapshot("network", 25, 1, 12, 10),
                new DashboardPanelLayoutSnapshot("discord", 37, 1, 12, 20),
                new DashboardPanelLayoutSnapshot("spotify", 25, 11, 12, 10),
                new DashboardPanelLayoutSnapshot("system", 25, 21, 12, 10),
                new DashboardPanelLayoutSnapshot("audio", 1, 21, 24, 10),
                new DashboardPanelLayoutSnapshot("processes", 37, 21, 12, 10)
            ]),
        new DashboardLayoutModeSnapshot(
            60,
            40,
            [
                new DashboardPanelLayoutSnapshot("temps", 1, 1, 24, 20),
                new DashboardPanelLayoutSnapshot("network", 25, 1, 12, 10),
                new DashboardPanelLayoutSnapshot("discord", 37, 1, 24, 20),
                new DashboardPanelLayoutSnapshot("spotify", 25, 11, 12, 10),
                new DashboardPanelLayoutSnapshot("audio", 1, 21, 36, 10),
                new DashboardPanelLayoutSnapshot("processes", 37, 21, 24, 10),
                new DashboardPanelLayoutSnapshot("system", 1, 31, 60, 10)
            ]));
}

public sealed record DashboardLayoutModeSnapshot(
    int Columns,
    int Rows,
    IReadOnlyList<DashboardPanelLayoutSnapshot> Panels,
    DashboardFloatingDockSnapshot? Dock = null,
    IReadOnlyList<DashboardLayoutVariantSnapshot>? Variants = null);

public sealed record DashboardLayoutVariantSnapshot(
    string ViewportKey,
    int ViewportWidth,
    int ViewportHeight,
    int Columns,
    int Rows,
    IReadOnlyList<DashboardPanelLayoutSnapshot> Panels,
    DashboardFloatingDockSnapshot Dock);

public sealed record DashboardPanelLayoutSnapshot(
    string Key,
    int X,
    int Y,
    int W,
    int H,
    bool Locked = false);

public sealed record DashboardFloatingDockSnapshot(
    int X,
    int Y,
    bool Locked = false,
    string Orientation = "horizontal")
{
    public static DashboardFloatingDockSnapshot Default { get; } = new(10, 10, false, "horizontal");
}

public sealed record DashboardLayoutPreferencesUpdate(
    string? Profile,
    int? Columns,
    int? Rows,
    IReadOnlyList<DashboardPanelLayoutUpdate>? Panels,
    DashboardFloatingDockUpdate? Dock,
    string? ViewportKey,
    int? ViewportWidth,
    int? ViewportHeight,
    bool? Reset);

public sealed record DashboardPanelLayoutUpdate(
    string Key,
    int? X,
    int? Y,
    int? W,
    int? H,
    bool? Locked);

public sealed record DashboardFloatingDockUpdate(
    int? X,
    int? Y,
    bool? Locked,
    string? Orientation);

public sealed record ThemePreferencesSnapshot(
    string PresetId,
    string PexelsApiKey,
    string PexelsApiKeyHint,
    ThemeBackgroundSnapshot Background,
    ThemeVisualsSnapshot Visuals,
    ThemeTypographySnapshot Typography,
    IReadOnlyList<ThemeVariantSnapshot> Variants,
    IReadOnlyList<string> RecentSearches,
    IReadOnlyList<PexelsAssetSnapshot> FavoriteAssets,
    IReadOnlyList<ThemeTypographyPresetSnapshot> TypographyPresets)
{
    public static ThemePreferencesSnapshot Default { get; } = new(
        "neon-grid",
        string.Empty,
        string.Empty,
        ThemeBackgroundSnapshot.Empty,
        ThemeVisualsSnapshot.Default,
        ThemeTypographySnapshot.Default,
        [],
        [],
        [],
        []);
}

public sealed record ThemeBackgroundSnapshot(
    string Source,
    string MediaKind,
    string AssetId,
    string Label,
    string RenderUrl,
    string PreviewUrl,
    string Attribution,
    string AttributionUrl)
{
    public static ThemeBackgroundSnapshot Empty { get; } = new(
        "none",
        "none",
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty);
}

public sealed record ThemeVisualsSnapshot(
    int DimPercent,
    int BlurPx,
    int MediaOpacityPercent)
{
    public static ThemeVisualsSnapshot Default { get; } = new(42, 12, 100);
}

public sealed record ThemeTypographySnapshot(
    IReadOnlyList<ThemeTextStyleSnapshot> Styles)
{
    public static ThemeTypographySnapshot Default { get; } = new([]);
}

public sealed record ThemeTextStyleSnapshot(
    string Role,
    string FontFamily,
    string Color,
    int FontWeight,
    bool Italic,
    bool Uppercase,
    double LetterSpacingEm,
    double FontSizeRem);

public sealed record ThemeTypographyPresetSnapshot(
    string Id,
    string Label,
    ThemeTypographySnapshot Typography);

public sealed record ThemeVariantSnapshot(
    string ViewportKey,
    string ProfileKey,
    int ViewportWidth,
    int ViewportHeight,
    string PresetId,
    ThemeBackgroundSnapshot Background,
    ThemeVisualsSnapshot Visuals,
    ThemeTypographySnapshot Typography);

public sealed record ThemePreferencesUpdate(
    string? PresetId,
    string? PexelsApiKey,
    ThemeBackgroundUpdate? Background,
    ThemeVisualsUpdate? Visuals,
    ThemeTypographySnapshot? Typography,
    string? ViewportKey,
    string? ProfileKey,
    int? ViewportWidth,
    int? ViewportHeight,
    string? RecentSearch,
    bool? ClearRecentSearches,
    IReadOnlyList<PexelsAssetSnapshot>? FavoriteAssets,
    IReadOnlyList<ThemeTypographyPresetSnapshot>? TypographyPresets);

public sealed record ThemeBackgroundUpdate(
    string? Source,
    string? MediaKind,
    string? AssetId,
    string? Label,
    string? RenderUrl,
    string? PreviewUrl,
    string? Attribution,
    string? AttributionUrl);

public sealed record ThemeVisualsUpdate(
    int? DimPercent,
    int? BlurPx,
    int? MediaOpacityPercent);

public sealed record DiscordPreferencesUpdate(
    bool? Enabled,
    string? RelayUrl,
    string? ApiKey,
    string? GuildId,
    string? MessagesChannelId,
    string? VoiceChannelId,
    string? TrackedUserId,
    int? LatestMessagesCount,
    IReadOnlyList<string>? FavoriteUserIds);

public sealed record SpotifyPreferencesUpdate(
    bool? Enabled,
    string? ClientId);

public sealed record ProcessCard(
    int ProcessId,
    string Name,
    double CpuPercent,
    double MemoryMb);

public sealed record DashboardCommand(string Type, string? SessionId, double? Value);

public sealed record MediaAssetSnapshot(
    string Id,
    string Name,
    string MediaKind,
    string Url,
    string PreviewUrl,
    long SizeBytes,
    DateTimeOffset AddedAt,
    bool IsLinked);

public sealed record LocalMediaLinkRequest(string Path);

public sealed record PexelsSearchResponseSnapshot(
    string MediaKind,
    string Query,
    int Page,
    int PerPage,
    int TotalResults,
    string? PrevPage,
    string? NextPage,
    IReadOnlyList<PexelsAssetSnapshot> Results);

public sealed record PexelsAssetSnapshot(
    string Id,
    string MediaKind,
    string Label,
    string PreviewUrl,
    string RenderUrl,
    int Width,
    int Height,
    int? DurationSeconds,
    string Attribution,
    string AttributionUrl,
    string PexelsUrl);

public sealed record SpotifyCommandRequest(
    string Action,
    string? ItemId,
    string? DeviceId,
    double? Value,
    string? RepeatState);
