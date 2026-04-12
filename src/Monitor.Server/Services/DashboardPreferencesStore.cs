using System.Text.Json;
using Monitor.Server.Config;
using Monitor.Server.Models;

namespace Monitor.Server.Services;

public sealed class DashboardPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly PanelOption[] PanelCatalog =
    [
        new("temps", "Temps"),
        new("network", "Network"),
        new("discord", "Discord"),
        new("audio", "Audio"),
        new("processes", "Processes"),
        new("system", "System")
    ];

    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly ILogger<DashboardPreferencesStore> _logger;
    private DashboardPreferencesSnapshot _current;

    public DashboardPreferencesStore(
        DashboardSettings settings,
        IHostEnvironment environment,
        ILogger<DashboardPreferencesStore> logger)
    {
        _logger = logger;
        _path = Path.Combine(environment.ContentRootPath, "dashboard.user.json");
        _current = Load(settings);
    }

    public DashboardPreferencesSnapshot Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    public DashboardEditorState GetEditorState(IReadOnlyList<string> availableAudioApps)
    {
        var current = Current;
        var mergedAudioApps = NormalizeAudioApps(current.Audio.VisibleSessionMatches.Concat(availableAudioApps));
        return new DashboardEditorState(PanelCatalog, SanitizeForEditor(current), mergedAudioApps);
    }

    public DashboardPreferencesSnapshot Update(DashboardPreferencesUpdate update)
    {
        lock (_lock)
        {
            var visiblePanels = update.VisiblePanels is null
                ? _current.VisiblePanels
                : NormalizePanels(update.VisiblePanels, _current.VisiblePanels);

            var audioUpdate = update.Audio;
            var currentAudio = _current.Audio;
            var audio = new AudioPreferencesSnapshot(
                audioUpdate?.IncludeSystemSounds ?? currentAudio.IncludeSystemSounds,
                Math.Clamp(audioUpdate?.MaxSessions ?? currentAudio.MaxSessions, 1, 24),
                audioUpdate?.VisibleSessionMatches is null
                    ? currentAudio.VisibleSessionMatches
                    : NormalizeAudioApps(audioUpdate.VisibleSessionMatches),
                audioUpdate?.SelectedEndpointId?.Trim() ?? currentAudio.SelectedEndpointId);

            var discordUpdate = update.Discord;
            var currentDiscord = _current.Discord;
            var apiKey = discordUpdate?.ApiKey switch
            {
                null => currentDiscord.ApiKey,
                _ => discordUpdate.ApiKey.Trim()
            };

            var discord = new DiscordPreferencesSnapshot(
                discordUpdate?.Enabled ?? currentDiscord.Enabled,
                discordUpdate?.RelayUrl?.Trim() ?? currentDiscord.RelayUrl,
                apiKey,
                BuildSecretHint(apiKey),
                NormalizeDiscordId(discordUpdate?.GuildId, currentDiscord.GuildId),
                NormalizeDiscordId(discordUpdate?.MessagesChannelId, currentDiscord.MessagesChannelId),
                NormalizeDiscordId(discordUpdate?.VoiceChannelId, currentDiscord.VoiceChannelId),
                NormalizeDiscordId(discordUpdate?.TrackedUserId, currentDiscord.TrackedUserId),
                Math.Clamp(discordUpdate?.LatestMessagesCount ?? currentDiscord.LatestMessagesCount, 1, 20),
                discordUpdate?.FavoriteUserIds is null
                    ? currentDiscord.FavoriteUserIds
                    : NormalizeDiscordIds(discordUpdate.FavoriteUserIds));

            var layout = update.Layout is null
                ? _current.Layout
                : MergeLayoutPreferences(_current.Layout, update.Layout);

            _current = new DashboardPreferencesSnapshot(visiblePanels, audio, discord, layout);
            PersistUnsafe();
            return _current;
        }
    }

    private DashboardPreferencesSnapshot Load(DashboardSettings settings)
    {
        var defaults = CreateDefaults(settings);
        if (!File.Exists(_path))
        {
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<DashboardPreferencesSnapshot>(json, JsonOptions);
            if (loaded is null)
            {
                return defaults;
            }

            var loadedAudio = loaded.Audio ?? defaults.Audio;
            var loadedDiscord = loaded.Discord ?? defaults.Discord;
            return new DashboardPreferencesSnapshot(
                NormalizePanels(loaded.VisiblePanels, defaults.VisiblePanels),
                new AudioPreferencesSnapshot(
                    loadedAudio.IncludeSystemSounds,
                    Math.Clamp(loadedAudio.MaxSessions, 1, 24),
                    NormalizeAudioApps(loadedAudio.VisibleSessionMatches),
                    loadedAudio.SelectedEndpointId?.Trim() ?? string.Empty),
                new DiscordPreferencesSnapshot(
                    loadedDiscord.Enabled,
                    loadedDiscord.RelayUrl,
                    loadedDiscord.ApiKey,
                    BuildSecretHint(loadedDiscord.ApiKey),
                    NormalizeDiscordId(loadedDiscord.GuildId, defaults.Discord.GuildId),
                    NormalizeDiscordId(loadedDiscord.MessagesChannelId, defaults.Discord.MessagesChannelId),
                    NormalizeDiscordId(loadedDiscord.VoiceChannelId, defaults.Discord.VoiceChannelId),
                    NormalizeDiscordId(loadedDiscord.TrackedUserId, defaults.Discord.TrackedUserId),
                    Math.Clamp(loadedDiscord.LatestMessagesCount, 1, 20),
                    NormalizeDiscordIds(loadedDiscord.FavoriteUserIds)),
                NormalizeLayoutPreferences(loaded.Layout, defaults.Layout));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load dashboard.user.json. Using defaults.");
            return defaults;
        }
    }

    private static DashboardPreferencesSnapshot CreateDefaults(DashboardSettings settings)
    {
        return new DashboardPreferencesSnapshot(
            NormalizePanels(settings.Ui.VisiblePanels, PanelCatalog.Select(panel => panel.Key).ToArray()),
            new AudioPreferencesSnapshot(
                settings.Audio.IncludeSystemSounds,
                Math.Clamp(settings.Audio.MaxSessions, 1, 24),
                NormalizeAudioApps(settings.Audio.VisibleSessionMatches),
                settings.Audio.SelectedEndpointId?.Trim() ?? string.Empty),
            new DiscordPreferencesSnapshot(
                settings.Discord.Enabled,
                settings.Discord.RelayUrl,
                settings.Discord.ApiKey,
                BuildSecretHint(settings.Discord.ApiKey),
                settings.Discord.GuildId == 0 ? string.Empty : settings.Discord.GuildId.ToString(),
                settings.Discord.MessagesChannelId == 0 ? string.Empty : settings.Discord.MessagesChannelId.ToString(),
                settings.Discord.VoiceChannelId == 0 ? string.Empty : settings.Discord.VoiceChannelId.ToString(),
                settings.Discord.TrackedUserId == 0 ? string.Empty : settings.Discord.TrackedUserId.ToString(),
                Math.Clamp(settings.Discord.LatestMessagesCount, 1, 20),
                settings.Discord.FavoriteUserIds.Select(id => id.ToString()).ToArray()),
            DashboardLayoutPreferencesSnapshot.Default);
    }

    private void PersistUnsafe()
    {
        try
        {
            var json = JsonSerializer.Serialize(_current, JsonOptions);
            File.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist dashboard.user.json.");
        }
    }

    private static string[] NormalizePanels(IEnumerable<string> candidatePanels, IEnumerable<string> fallback)
    {
        var allowed = new HashSet<string>(PanelCatalog.Select(panel => panel.Key), StringComparer.OrdinalIgnoreCase);
        var normalized = candidatePanels
            .Where(panel => !string.IsNullOrWhiteSpace(panel))
            .Select(panel => panel.Trim().ToLowerInvariant())
            .Where(panel => allowed.Contains(panel))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized.Length > 0
            ? normalized
            : fallback
                .Where(panel => allowed.Contains(panel))
                .Select(panel => panel.ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    private static string[] NormalizeAudioApps(IEnumerable<string> matches)
    {
        return matches
            .Where(match => !string.IsNullOrWhiteSpace(match))
            .Select(match => match.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(match => match, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeDiscordId(string? candidate, string fallback)
    {
        if (candidate is null)
        {
            return fallback;
        }

        var normalized = new string(candidate.Where(char.IsDigit).ToArray());
        return normalized;
    }

    private static string[] NormalizeDiscordIds(IEnumerable<string> ids)
    {
        return ids
            .Select(id => NormalizeDiscordId(id, string.Empty))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static DashboardLayoutPreferencesSnapshot NormalizeLayoutPreferences(
        DashboardLayoutPreferencesSnapshot? candidate,
        DashboardLayoutPreferencesSnapshot fallback)
    {
        if (candidate is null)
        {
            return fallback;
        }

        return new DashboardLayoutPreferencesSnapshot(
            NormalizeLayoutMode(candidate.Desktop, fallback.Desktop),
            NormalizeLayoutMode(candidate.TabletLandscape, fallback.TabletLandscape),
            NormalizeLayoutMode(candidate.PhoneLandscape, fallback.PhoneLandscape));
    }

    private static DashboardLayoutPreferencesSnapshot MergeLayoutPreferences(
        DashboardLayoutPreferencesSnapshot current,
        DashboardLayoutPreferencesUpdate update)
    {
        if (update.Reset is true)
        {
            return DashboardLayoutPreferencesSnapshot.Default;
        }

        var profile = NormalizeLayoutProfile(update.Profile);
        if (profile is null)
        {
            return current;
        }

        return profile switch
        {
            "desktop" => current with
            {
                Desktop = MergeLayoutMode(current.Desktop, update, DashboardLayoutPreferencesSnapshot.Default.Desktop)
            },
            "tablet-landscape" => current with
            {
                TabletLandscape = MergeLayoutMode(current.TabletLandscape, update, DashboardLayoutPreferencesSnapshot.Default.TabletLandscape)
            },
            "phone-landscape" => current with
            {
                PhoneLandscape = MergeLayoutMode(current.PhoneLandscape, update, DashboardLayoutPreferencesSnapshot.Default.PhoneLandscape)
            },
            _ => current
        };
    }

    private static DashboardLayoutModeSnapshot MergeLayoutMode(
        DashboardLayoutModeSnapshot current,
        DashboardLayoutPreferencesUpdate update,
        DashboardLayoutModeSnapshot defaults)
    {
        if (update.Reset is true)
        {
            return defaults;
        }

        var columns = Math.Clamp(update.Columns ?? current.Columns, 1, 24);
        var rows = Math.Clamp(update.Rows ?? current.Rows, 1, 24);
        var panels = update.Panels is null
            ? current.Panels
            : MergePanelLayouts(current.Panels, update.Panels, columns, rows);

        return NormalizeLayoutMode(new DashboardLayoutModeSnapshot(columns, rows, panels), defaults);
    }

    private static DashboardPanelLayoutSnapshot[] MergePanelLayouts(
        IReadOnlyList<DashboardPanelLayoutSnapshot> current,
        IReadOnlyList<DashboardPanelLayoutUpdate> updates,
        int columns,
        int rows)
    {
        var byKey = current.ToDictionary(panel => panel.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var update in updates)
        {
            if (!byKey.TryGetValue(update.Key, out var existing))
            {
                continue;
            }

            var x = ClampPosition(update.X ?? existing.X, columns);
            var y = ClampPosition(update.Y ?? existing.Y, rows);
            var w = ClampSpan(update.W ?? existing.W, columns, x);
            var h = ClampSpan(update.H ?? existing.H, rows, y);
            byKey[update.Key] = existing with { X = x, Y = y, W = w, H = h };
        }

        return PanelCatalog
            .Select(panel => byKey.TryGetValue(panel.Key, out var layout)
                ? layout
                : new DashboardPanelLayoutSnapshot(panel.Key, 1, 1, 1, 1))
            .ToArray();
    }

    private static DashboardLayoutModeSnapshot NormalizeLayoutMode(
        DashboardLayoutModeSnapshot? candidate,
        DashboardLayoutModeSnapshot fallback)
    {
        if (candidate is null)
        {
            return fallback;
        }

        var columns = Math.Clamp(candidate.Columns, 1, 24);
        var rows = Math.Clamp(candidate.Rows, 1, 24);
        var source = candidate.Panels?.ToDictionary(panel => panel.Key, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, DashboardPanelLayoutSnapshot>(StringComparer.OrdinalIgnoreCase);

        var panels = PanelCatalog.Select(panel =>
        {
            if (!source.TryGetValue(panel.Key, out var layout))
            {
                layout = fallback.Panels.First(item => string.Equals(item.Key, panel.Key, StringComparison.OrdinalIgnoreCase));
            }

            var x = ClampPosition(layout.X, columns);
            var y = ClampPosition(layout.Y, rows);
            var w = ClampSpan(layout.W, columns, x);
            var h = ClampSpan(layout.H, rows, y);
            return layout with { Key = panel.Key, X = x, Y = y, W = w, H = h };
        }).ToArray();

        return new DashboardLayoutModeSnapshot(columns, rows, panels);
    }

    private static int ClampPosition(int value, int max) => Math.Clamp(value, 1, max);

    private static int ClampSpan(int value, int max, int start) => Math.Clamp(value, 1, Math.Max(1, max - start + 1));

    private static string? NormalizeLayoutProfile(string? profile)
    {
        var normalized = profile?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "desktop" or "tablet-landscape" or "phone-landscape" => normalized,
            _ => null
        };
    }

    private static DashboardPreferencesSnapshot SanitizeForEditor(DashboardPreferencesSnapshot current)
    {
        return current with
        {
            Discord = current.Discord with
            {
                ApiKey = string.Empty,
                ApiKeyHint = BuildSecretHint(current.Discord.ApiKey)
            }
        };
    }

    private static string BuildSecretHint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.Length <= 8)
        {
            return new string('*', trimmed.Length);
        }

        return $"{trimmed[..4]}...{trimmed[^4..]}";
    }
}
