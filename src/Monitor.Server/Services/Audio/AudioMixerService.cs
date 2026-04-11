using NAudio.CoreAudioApi;
using Monitor.Server.Config;
using Monitor.Server.Models;

namespace Monitor.Server.Services.Audio;

public sealed class AudioMixerService(DashboardPreferencesStore preferencesStore)
{
    private const string MasterOutputSessionId = "master-output";
    private readonly Lock _orderLock = new();
    private readonly Dictionary<string, int> _sessionOrder = [];
    private int _nextOrder;

    public AudioMixerSnapshot Read()
    {
        if (!TryReadSessions(out var cards, out var error))
        {
            return new AudioMixerSnapshot([], error);
        }

        ApplyStableOrder(cards);
        var preferences = preferencesStore.Current;
        var visibleCards = FilterVisibleSessions(cards, preferences);
        var ordered = visibleCards
            .OrderBy(card => card.IsSystemSound ? -1 : 0)
            .ThenBy(card => GetOrder(card.Id))
            .ThenBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
            .Take(preferences.Audio.MaxSessions)
            .ToArray();

        return new AudioMixerSnapshot(ordered, null);
    }

    public IReadOnlyList<string> GetAvailableSessionNames()
    {
        if (!TryReadSessions(out var cards, out _))
        {
            return [];
        }

        return cards
            .Select(card => card.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool TrySetVolume(string sessionId, double value)
    {
        if (string.Equals(sessionId, MasterOutputSessionId, StringComparison.Ordinal))
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(value, 0d, 1d);
                return true;
            }
            catch
            {
                return false;
            }
        }

        return ModifySession(sessionId, session => session.SimpleAudioVolume.Volume = (float)Math.Clamp(value, 0d, 1d));
    }

    public bool TrySetMute(string sessionId, bool isMuted)
    {
        if (string.Equals(sessionId, MasterOutputSessionId, StringComparison.Ordinal))
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                device.AudioEndpointVolume.Mute = isMuted;
                return true;
            }
            catch
            {
                return false;
            }
        }

        return ModifySession(sessionId, session => session.SimpleAudioVolume.Mute = isMuted);
    }

    private bool ModifySession(string sessionId, Action<AudioSessionControl> apply)
    {
        var changed = false;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in EnumerateRenderDevices(enumerator))
            {
                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var index = 0; index < sessions.Count; index++)
                    {
                        using var session = sessions[index];
                        if (session is null)
                        {
                            continue;
                        }

                        var processId = (int)session.GetProcessID;
                        if (!string.Equals(ResolveSessionId(device, session, processId), sessionId, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        apply(session);
                        changed = true;
                    }
                }
            }
        }
        catch
        {
        }

        return changed;
    }

    private void ApplyStableOrder(IEnumerable<AudioSessionCard> cards)
    {
        lock (_orderLock)
        {
            foreach (var card in cards)
            {
                if (_sessionOrder.ContainsKey(card.Id))
                {
                    continue;
                }

                _sessionOrder[card.Id] = _nextOrder++;
            }
        }
    }

    private int GetOrder(string sessionId)
    {
        lock (_orderLock)
        {
            return _sessionOrder.GetValueOrDefault(sessionId, int.MaxValue);
        }
    }

    private bool TryReadSessions(out List<AudioSessionCard> cards, out string? error)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            var rawSessions = new List<RawAudioSession>();
            foreach (var device in EnumerateRenderDevices(enumerator))
            {
                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var index = 0; index < sessions.Count; index++)
                    {
                        using var session = sessions[index];
                        if (session is null)
                        {
                            continue;
                        }

                        var processId = (int)session.GetProcessID;
                        if (processId == 0)
                        {
                            continue;
                        }

                        var name = ResolveSessionName(session, processId);
                        rawSessions.Add(new RawAudioSession(
                            ResolveSessionId(device, session, processId),
                            name,
                            processId,
                            ResolveEndpointName(device),
                            (int)Math.Round(session.SimpleAudioVolume.Volume * 100),
                            session.SimpleAudioVolume.Mute,
                            false));
                    }
                }
            }

            cards =
            [
                new AudioSessionCard(
                    MasterOutputSessionId,
                    "System Audio",
                    0,
                    ResolveEndpointName(defaultDevice),
                    (int)Math.Round(defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100),
                    defaultDevice.AudioEndpointVolume.Mute,
                    true),
                .. rawSessions
                .GroupBy(session => session.Id, StringComparer.Ordinal)
                .Select(group =>
                {
                    var orderedGroup = group.OrderByDescending(item => item.VolumePercent).ToArray();
                    var primary = orderedGroup[0];
                    var distinctPids = orderedGroup
                        .Where(item => item.ProcessId > 0)
                        .Select(item => item.ProcessId)
                        .Distinct()
                        .ToArray();

                    return new AudioSessionCard(
                        primary.Id,
                        primary.Name,
                        distinctPids.FirstOrDefault(),
                        BuildDetailLabel(primary.IsSystemSound, distinctPids, orderedGroup.Length, primary.EndpointName),
                        orderedGroup.Max(item => item.VolumePercent),
                        orderedGroup.All(item => item.IsMuted),
                        primary.IsSystemSound);
                })
            ];

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            cards = [];
            error = $"Audio mixer unavailable: {ex.Message}";
            return false;
        }
    }

    private static IEnumerable<AudioSessionCard> FilterVisibleSessions(
        IEnumerable<AudioSessionCard> cards,
        DashboardPreferencesSnapshot preferences)
    {
        var matches = preferences.Audio.VisibleSessionMatches
            .Where(match => !string.IsNullOrWhiteSpace(match))
            .ToArray();

        var filtered = cards.Where(card => preferences.Audio.IncludeSystemSounds || !card.IsSystemSound);
        if (matches.Length == 0)
        {
            return filtered;
        }

        return filtered.Where(card =>
        {
            if (card.IsSystemSound)
            {
                return true;
            }

            var haystack = BuildSessionSearchText(card);
            return matches.Any(match => haystack.Contains(match, StringComparison.OrdinalIgnoreCase));
        });
    }

    private static string BuildSessionSearchText(AudioSessionCard card)
    {
        return $"{card.Name} {card.Detail} {card.ProcessId} {(card.IsSystemSound ? "system sounds" : string.Empty)}";
    }

    private static string ResolveSessionName(AudioSessionControl session, int processId)
    {
        if (processId == 0)
        {
            return "System Sounds";
        }

        if (!string.IsNullOrWhiteSpace(session.DisplayName))
        {
            return session.DisplayName;
        }

        try
        {
            using var process = global::System.Diagnostics.Process.GetProcessById(processId);
            if (IsSpotifyProcess(process))
            {
                return "Spotify";
            }

            if (!string.IsNullOrWhiteSpace(process.MainWindowTitle))
            {
                return process.MainWindowTitle;
            }

            return process.ProcessName;
        }
        catch
        {
            return $"PID {processId}";
        }
    }

    private static string ResolveSessionId(MMDevice device, AudioSessionControl session, int processId)
    {
        var identifier = session.GetSessionIdentifier;
        var sessionKey = string.IsNullOrWhiteSpace(identifier) ? $"pid:{processId}" : identifier;
        return $"{device.ID}|{sessionKey}";
    }

    private static string BuildDetailLabel(bool isSystemSound, IReadOnlyList<int> distinctPids, int sessionCount, string endpointName)
    {
        if (isSystemSound)
        {
            return endpointName;
        }

        if (distinctPids.Count == 1)
        {
            return $"{endpointName} · pid {distinctPids[0]}";
        }

        if (distinctPids.Count > 1)
        {
            return $"{endpointName} · {distinctPids.Count} pids";
        }

        return sessionCount > 1 ? $"{endpointName} · {sessionCount} sessions" : endpointName;
    }

    private static IEnumerable<MMDevice> EnumerateRenderDevices(MMDeviceEnumerator enumerator)
    {
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Cast<MMDevice>();
    }

    private static string ResolveEndpointName(MMDevice device)
    {
        return string.IsNullOrWhiteSpace(device.FriendlyName) ? device.ID : device.FriendlyName;
    }

    private static bool IsSpotifyProcess(global::System.Diagnostics.Process process)
    {
        if (process.ProcessName.Equals("Spotify", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var fileName = Path.GetFileNameWithoutExtension(process.MainModule?.FileName);
            return string.Equals(fileName, "Spotify", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private sealed record RawAudioSession(
        string Id,
        string Name,
        int ProcessId,
        string EndpointName,
        int VolumePercent,
        bool IsMuted,
        bool IsSystemSound);
}
