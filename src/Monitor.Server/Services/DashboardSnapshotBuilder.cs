using Monitor.Server.Config;
using Monitor.Server.Models;
using Monitor.Server.Services.Audio;
using Monitor.Server.Services.Discord;
using Monitor.Server.Services.Spotify;
using Monitor.Server.Services.System;
using Monitor.Server.Services.Temp;

namespace Monitor.Server.Services;

public sealed class DashboardSnapshotBuilder(
    DashboardPreferencesStore preferencesStore,
    HwInfoClient hwInfoClient,
    DiscordCollector discordCollector,
    SpotifyService spotifyService,
    NetworkCollector networkCollector,
    SystemInfoCollector systemInfoCollector,
    AudioMixerService audioMixerService,
    ProcessCollector processCollector,
    DashboardStateStore stateStore)
{
    private readonly SemaphoreSlim _sync = new(1, 1);

    public async Task<DashboardSnapshot> BuildAsync(CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            var tempsTask = hwInfoClient.ReadAsync(cancellationToken);
            var discordTask = discordCollector.ReadAsync(cancellationToken);
            var spotifyTask = spotifyService.ReadAsync(cancellationToken);
            var networkTask = networkCollector.ReadAsync(cancellationToken);

            var system = systemInfoCollector.Read();
            var audio = audioMixerService.Read();
            var processes = processCollector.Read();

            await Task.WhenAll(tempsTask, discordTask, spotifyTask, networkTask);

            var snapshot = new DashboardSnapshot(
                DateTimeOffset.UtcNow,
                tempsTask.Result,
                discordTask.Result,
                spotifyTask.Result,
                networkTask.Result,
                system,
                audio,
                processes,
                new UiSnapshot(
                    preferencesStore.Current.VisiblePanels,
                    preferencesStore.Current.Layout,
                    preferencesStore.Current.Theme));

            stateStore.Update(snapshot);
            return snapshot;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<DashboardSnapshot> RefreshAudioAsync(CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            var current = stateStore.Current;
            var updated = current with
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                Audio = audioMixerService.Read()
            };

            stateStore.Update(updated);
            return updated;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<DashboardSnapshot> RefreshSpotifyAsync(CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            var current = stateStore.Current;
            var updated = current with
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                Spotify = await spotifyService.ReadAsync(cancellationToken)
            };

            stateStore.Update(updated);
            return updated;
        }
        finally
        {
            _sync.Release();
        }
    }
}
