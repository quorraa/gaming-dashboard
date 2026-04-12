namespace Monitor.Server.Models;

public sealed record DashboardSnapshot(
    DateTimeOffset GeneratedAt,
    TempsSnapshot Temps,
    DiscordSnapshot Discord,
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
        NetworkSnapshot.Empty,
        SystemInfoSnapshot.Empty,
        new AudioMixerSnapshot([], [], string.Empty, null),
        new ProcessesSnapshot([]),
        new UiSnapshot(
            ["temps", "network", "discord", "audio", "processes", "system"],
            DashboardLayoutPreferencesSnapshot.Default));
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
    DashboardLayoutPreferencesSnapshot Layout);

public sealed record PanelOption(string Key, string Label);

public sealed record DashboardEditorState(
    IReadOnlyList<PanelOption> AvailablePanels,
    DashboardPreferencesSnapshot Preferences,
    IReadOnlyList<string> AvailableAudioApps);

public sealed record DashboardPreferencesSnapshot(
    IReadOnlyList<string> VisiblePanels,
    AudioPreferencesSnapshot Audio,
    DiscordPreferencesSnapshot Discord,
    DashboardLayoutPreferencesSnapshot Layout);

public sealed record AudioPreferencesSnapshot(
    bool IncludeSystemSounds,
    int MaxSessions,
    IReadOnlyList<string> VisibleSessionMatches,
    string SelectedEndpointId);

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

public sealed record DashboardPreferencesUpdate(
    IReadOnlyList<string>? VisiblePanels,
    AudioPreferencesUpdate? Audio,
    DiscordPreferencesUpdate? Discord,
    DashboardLayoutPreferencesUpdate? Layout);

public sealed record AudioPreferencesUpdate(
    bool? IncludeSystemSounds,
    int? MaxSessions,
    IReadOnlyList<string>? VisibleSessionMatches,
    string? SelectedEndpointId);

public sealed record DashboardLayoutPreferencesSnapshot(
    DashboardLayoutModeSnapshot Desktop,
    DashboardLayoutModeSnapshot TabletLandscape,
    DashboardLayoutModeSnapshot PhoneLandscape)
{
    public static DashboardLayoutPreferencesSnapshot Default { get; } = new(
        new DashboardLayoutModeSnapshot(
            3,
            3,
            [
                new DashboardPanelLayoutSnapshot("temps", 1, 1, 2, 1),
                new DashboardPanelLayoutSnapshot("network", 3, 1, 1, 2),
                new DashboardPanelLayoutSnapshot("discord", 1, 2, 2, 1),
                new DashboardPanelLayoutSnapshot("audio", 1, 3, 1, 1),
                new DashboardPanelLayoutSnapshot("processes", 2, 3, 1, 1),
                new DashboardPanelLayoutSnapshot("system", 3, 3, 1, 1)
            ]),
        new DashboardLayoutModeSnapshot(
            4,
            3,
            [
                new DashboardPanelLayoutSnapshot("temps", 1, 1, 2, 2),
                new DashboardPanelLayoutSnapshot("network", 3, 1, 1, 1),
                new DashboardPanelLayoutSnapshot("discord", 4, 1, 1, 2),
                new DashboardPanelLayoutSnapshot("system", 3, 2, 1, 1),
                new DashboardPanelLayoutSnapshot("audio", 1, 3, 2, 1),
                new DashboardPanelLayoutSnapshot("processes", 3, 3, 2, 1)
            ]),
        new DashboardLayoutModeSnapshot(
            5,
            3,
            [
                new DashboardPanelLayoutSnapshot("temps", 1, 1, 2, 2),
                new DashboardPanelLayoutSnapshot("network", 3, 1, 1, 1),
                new DashboardPanelLayoutSnapshot("discord", 4, 1, 2, 2),
                new DashboardPanelLayoutSnapshot("system", 3, 2, 1, 1),
                new DashboardPanelLayoutSnapshot("audio", 1, 3, 3, 1),
                new DashboardPanelLayoutSnapshot("processes", 4, 3, 2, 1)
            ]));
}

public sealed record DashboardLayoutModeSnapshot(
    int Columns,
    int Rows,
    IReadOnlyList<DashboardPanelLayoutSnapshot> Panels);

public sealed record DashboardPanelLayoutSnapshot(
    string Key,
    int X,
    int Y,
    int W,
    int H);

public sealed record DashboardLayoutPreferencesUpdate(
    string? Profile,
    int? Columns,
    int? Rows,
    IReadOnlyList<DashboardPanelLayoutUpdate>? Panels,
    bool? Reset);

public sealed record DashboardPanelLayoutUpdate(
    string Key,
    int? X,
    int? Y,
    int? W,
    int? H);

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

public sealed record ProcessCard(
    int ProcessId,
    string Name,
    double CpuPercent,
    double MemoryMb);

public sealed record DashboardCommand(string Type, string? SessionId, double? Value);
