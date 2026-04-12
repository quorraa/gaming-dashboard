using System.Text.Json;
using Monitor.Server.Config;
using Monitor.Server.Models;

namespace Monitor.Server.Services;

public sealed class DashboardPreferencesStore
{
    private const string AppDataFolderName = "GamingDashboard";
    private const string PreferencesFileName = "dashboard.user.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly PanelOption[] PanelCatalog =
    [
        new("temps", "Temps"),
        new("network", "Network"),
        new("discord", "Discord"),
        new("spotify", "Spotify"),
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
        _path = ResolvePreferencesPath(environment);
        TryMigrateLegacyPreferences(environment.ContentRootPath, _path);
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

            var spotifyUpdate = update.Spotify;
            var currentSpotify = _current.Spotify;
            var nextSpotifyClientId = spotifyUpdate?.ClientId?.Trim() ?? currentSpotify.ClientId;
            var spotifyRefreshToken = currentSpotify.RefreshToken;
            if (spotifyUpdate?.ClientId is not null
                && !string.Equals(nextSpotifyClientId, currentSpotify.ClientId, StringComparison.Ordinal))
            {
                spotifyRefreshToken = string.Empty;
            }

            var spotify = new SpotifyPreferencesSnapshot(
                spotifyUpdate?.Enabled ?? currentSpotify.Enabled,
                nextSpotifyClientId,
                spotifyRefreshToken,
                !string.IsNullOrWhiteSpace(spotifyRefreshToken));

            var layout = update.Layout is null
                ? _current.Layout
                : MergeLayoutPreferences(_current.Layout, update.Layout);

            var themeUpdate = update.Theme;
            var currentTheme = _current.Theme;
            var pexelsApiKey = themeUpdate?.PexelsApiKey switch
            {
                null => currentTheme.PexelsApiKey,
                _ => themeUpdate.PexelsApiKey.Trim()
            };
            var theme = new ThemePreferencesSnapshot(
                NormalizeThemePreset(themeUpdate?.PresetId, currentTheme.PresetId),
                pexelsApiKey,
                BuildSecretHint(pexelsApiKey),
                themeUpdate?.Background is null
                    ? currentTheme.Background
                    : NormalizeThemeBackground(currentTheme.Background, themeUpdate.Background));

            _current = new DashboardPreferencesSnapshot(visiblePanels, audio, discord, spotify, layout, theme);
            PersistUnsafe();
            return _current;
        }
    }

    public void SetSpotifyAuthorization(string refreshToken)
    {
        lock (_lock)
        {
            var trimmed = refreshToken.Trim();
            _current = _current with
            {
                Spotify = _current.Spotify with
                {
                    RefreshToken = trimmed,
                    IsAuthorized = !string.IsNullOrWhiteSpace(trimmed)
                }
            };
            PersistUnsafe();
        }
    }

    public void ClearSpotifyAuthorization()
    {
        lock (_lock)
        {
            _current = _current with
            {
                Spotify = _current.Spotify with
                {
                    RefreshToken = string.Empty,
                    IsAuthorized = false
                }
            };
            PersistUnsafe();
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
            var loadedSpotify = loaded.Spotify ?? defaults.Spotify;
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
                new SpotifyPreferencesSnapshot(
                    loadedSpotify.Enabled,
                    loadedSpotify.ClientId?.Trim() ?? defaults.Spotify.ClientId,
                    loadedSpotify.RefreshToken?.Trim() ?? string.Empty,
                    !string.IsNullOrWhiteSpace(loadedSpotify.RefreshToken)),
                NormalizeLayoutPreferences(loaded.Layout, defaults.Layout),
                NormalizeThemePreferences(loaded.Theme, defaults.Theme));
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
            new SpotifyPreferencesSnapshot(
                settings.Spotify.Enabled,
                settings.Spotify.ClientId?.Trim() ?? string.Empty,
                string.Empty,
                false),
            DashboardLayoutPreferencesSnapshot.Default,
            new ThemePreferencesSnapshot(
                NormalizeThemePreset(settings.Theme.DefaultPresetId, ThemePreferencesSnapshot.Default.PresetId),
                settings.Theme.PexelsApiKey,
                BuildSecretHint(settings.Theme.PexelsApiKey),
                ThemeBackgroundSnapshot.Empty));
    }

    private void PersistUnsafe()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
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

    private static ThemePreferencesSnapshot NormalizeThemePreferences(
        ThemePreferencesSnapshot? candidate,
        ThemePreferencesSnapshot fallback)
    {
        if (candidate is null)
        {
            return fallback;
        }

        return new ThemePreferencesSnapshot(
            NormalizeThemePreset(candidate.PresetId, fallback.PresetId),
            candidate.PexelsApiKey?.Trim() ?? fallback.PexelsApiKey,
            BuildSecretHint(candidate.PexelsApiKey?.Trim() ?? fallback.PexelsApiKey),
            NormalizeThemeBackgroundSnapshot(candidate.Background));
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

        var hasViewportOverride = !string.IsNullOrWhiteSpace(update.ViewportKey)
            && update.ViewportWidth.GetValueOrDefault() > 0
            && update.ViewportHeight.GetValueOrDefault() > 0;

        if (hasViewportOverride)
        {
            var variants = MergeLayoutVariants(current, update, defaults);
            return NormalizeLayoutMode(current with { Variants = variants }, defaults);
        }

        var columns = Math.Clamp(update.Columns ?? current.Columns, 1, 120);
        var rows = Math.Clamp(update.Rows ?? current.Rows, 1, 120);
        var panels = update.Panels is null
            ? current.Panels
            : MergePanelLayouts(current.Panels, update.Panels, columns, rows);
        var dock = MergeDock(current.Dock ?? DashboardFloatingDockSnapshot.Default, update.Dock);

        return NormalizeLayoutMode(new DashboardLayoutModeSnapshot(columns, rows, panels, dock, current.Variants), defaults);
    }

    private static DashboardLayoutVariantSnapshot[] MergeLayoutVariants(
        DashboardLayoutModeSnapshot current,
        DashboardLayoutPreferencesUpdate update,
        DashboardLayoutModeSnapshot defaults)
    {
        var viewportKey = update.ViewportKey!.Trim();
        var variants = (current.Variants ?? [])
            .ToDictionary(variant => variant.ViewportKey, StringComparer.OrdinalIgnoreCase);

        var currentVariant = variants.TryGetValue(viewportKey, out var existing)
            ? existing
            : new DashboardLayoutVariantSnapshot(
                viewportKey,
                update.ViewportWidth!.Value,
                update.ViewportHeight!.Value,
                current.Columns,
                current.Rows,
                current.Panels,
                current.Dock ?? DashboardFloatingDockSnapshot.Default);

        var columns = Math.Clamp(update.Columns ?? currentVariant.Columns, 1, 120);
        var rows = Math.Clamp(update.Rows ?? currentVariant.Rows, 1, 120);
        var panels = update.Panels is null
            ? currentVariant.Panels
            : MergePanelLayouts(currentVariant.Panels, update.Panels, columns, rows);
        var dock = MergeDock(currentVariant.Dock, update.Dock);

        variants[viewportKey] = NormalizeLayoutVariant(new DashboardLayoutVariantSnapshot(
            viewportKey,
            update.ViewportWidth!.Value,
            update.ViewportHeight!.Value,
            columns,
            rows,
            panels,
            dock), defaults);

        return variants.Values
            .OrderBy(item => item.ViewportWidth)
            .ThenBy(item => item.ViewportHeight)
            .ToArray();
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
            byKey[update.Key] = existing with
            {
                X = x,
                Y = y,
                W = w,
                H = h,
                Locked = update.Locked ?? existing.Locked
            };
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

        var columns = Math.Clamp(candidate.Columns, 1, 120);
        var rows = Math.Clamp(candidate.Rows, 1, 120);
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

        var dock = NormalizeDock(candidate.Dock, fallback.Dock ?? DashboardFloatingDockSnapshot.Default);
        var variants = NormalizeLayoutVariants(candidate.Variants, fallback, dock);

        return new DashboardLayoutModeSnapshot(columns, rows, panels, dock, variants);
    }

    private static DashboardLayoutVariantSnapshot[] NormalizeLayoutVariants(
        IReadOnlyList<DashboardLayoutVariantSnapshot>? candidates,
        DashboardLayoutModeSnapshot fallback,
        DashboardFloatingDockSnapshot fallbackDock)
    {
        if (candidates is null || candidates.Count == 0)
        {
            return [];
        }

        return candidates
            .Where(item => !string.IsNullOrWhiteSpace(item.ViewportKey) && item.ViewportWidth > 0 && item.ViewportHeight > 0)
            .Select(item => NormalizeLayoutVariant(item, fallback with { Dock = fallbackDock }))
            .GroupBy(item => item.ViewportKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(item => item.ViewportWidth)
            .ThenBy(item => item.ViewportHeight)
            .ToArray();
    }

    private static DashboardLayoutVariantSnapshot NormalizeLayoutVariant(
        DashboardLayoutVariantSnapshot candidate,
        DashboardLayoutModeSnapshot fallback)
    {
        var normalizedMode = NormalizeLayoutMode(new DashboardLayoutModeSnapshot(
            candidate.Columns,
            candidate.Rows,
            candidate.Panels,
            candidate.Dock), fallback);

        return new DashboardLayoutVariantSnapshot(
            candidate.ViewportKey.Trim(),
            Math.Max(1, candidate.ViewportWidth),
            Math.Max(1, candidate.ViewportHeight),
            normalizedMode.Columns,
            normalizedMode.Rows,
            normalizedMode.Panels,
            normalizedMode.Dock ?? DashboardFloatingDockSnapshot.Default);
    }

    private static DashboardFloatingDockSnapshot MergeDock(
        DashboardFloatingDockSnapshot current,
        DashboardFloatingDockUpdate? update)
    {
        if (update is null)
        {
            return current;
        }

        return NormalizeDock(new DashboardFloatingDockSnapshot(
            update.X ?? current.X,
            update.Y ?? current.Y,
            update.Locked ?? current.Locked,
            update.Orientation?.Trim().ToLowerInvariant() ?? current.Orientation), current);
    }

    private static DashboardFloatingDockSnapshot NormalizeDock(
        DashboardFloatingDockSnapshot? candidate,
        DashboardFloatingDockSnapshot fallback)
    {
        if (candidate is null)
        {
            return fallback;
        }

        return new DashboardFloatingDockSnapshot(
            Math.Max(0, candidate.X),
            Math.Max(0, candidate.Y),
            candidate.Locked,
            candidate.Orientation is "vertical" ? "vertical" : "horizontal");
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

    private static string NormalizeThemePreset(string? presetId, string fallback)
    {
        var normalized = presetId?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized)
            ? fallback
            : normalized;
    }

    private static ThemeBackgroundSnapshot NormalizeThemeBackground(
        ThemeBackgroundSnapshot current,
        ThemeBackgroundUpdate? update)
    {
        if (update is null)
        {
            return current;
        }

        var source = NormalizeThemeSource(update.Source, current.Source);
        var mediaKind = NormalizeThemeMediaKind(update.MediaKind, current.MediaKind);
        if (source == "none")
        {
            return ThemeBackgroundSnapshot.Empty;
        }

        return new ThemeBackgroundSnapshot(
            source,
            mediaKind,
            update.AssetId?.Trim() ?? current.AssetId,
            update.Label?.Trim() ?? current.Label,
            update.RenderUrl?.Trim() ?? current.RenderUrl,
            update.PreviewUrl?.Trim() ?? current.PreviewUrl,
            update.Attribution?.Trim() ?? current.Attribution,
            update.AttributionUrl?.Trim() ?? current.AttributionUrl);
    }

    private static ThemeBackgroundSnapshot NormalizeThemeBackgroundSnapshot(ThemeBackgroundSnapshot? candidate)
    {
        if (candidate is null)
        {
            return ThemeBackgroundSnapshot.Empty;
        }

        var source = NormalizeThemeSource(candidate.Source, "none");
        if (source == "none")
        {
            return ThemeBackgroundSnapshot.Empty;
        }

        return new ThemeBackgroundSnapshot(
            source,
            NormalizeThemeMediaKind(candidate.MediaKind, "none"),
            candidate.AssetId?.Trim() ?? string.Empty,
            candidate.Label?.Trim() ?? string.Empty,
            candidate.RenderUrl?.Trim() ?? string.Empty,
            candidate.PreviewUrl?.Trim() ?? string.Empty,
            candidate.Attribution?.Trim() ?? string.Empty,
            candidate.AttributionUrl?.Trim() ?? string.Empty);
    }

    private static string NormalizeThemeSource(string? source, string fallback)
    {
        var normalized = source?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "none" or "local" or "pexels-photo" or "pexels-video" => normalized,
            _ => fallback
        };
    }

    private static string NormalizeThemeMediaKind(string? mediaKind, string fallback)
    {
        var normalized = mediaKind?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "image" or "video" or "none" => normalized,
            _ => fallback
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
            },
            Spotify = current.Spotify with
            {
                RefreshToken = string.Empty
            },
            Theme = current.Theme with
            {
                PexelsApiKey = string.Empty,
                PexelsApiKeyHint = BuildSecretHint(current.Theme.PexelsApiKey)
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

    private static string ResolvePreferencesPath(IHostEnvironment environment)
    {
        var appDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appDataRoot))
        {
            return Path.Combine(environment.ContentRootPath, PreferencesFileName);
        }

        return Path.Combine(appDataRoot, AppDataFolderName, PreferencesFileName);
    }

    private void TryMigrateLegacyPreferences(string legacyRoot, string targetPath)
    {
        try
        {
            if (File.Exists(targetPath))
            {
                return;
            }

            var legacyPath = Path.Combine(legacyRoot, PreferencesFileName);
            if (!File.Exists(legacyPath))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(legacyPath, targetPath, overwrite: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to migrate legacy dashboard preferences.");
        }
    }
}
