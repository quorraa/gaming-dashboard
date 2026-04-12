using NAudio.CoreAudioApi;
using Monitor.Server.Config;
using Monitor.Server.Models;

namespace Monitor.Server.Services.Audio;

public sealed class AudioMixerService(DashboardPreferencesStore preferencesStore)
{
    private const string MasterOutputSessionPrefix = "master-output|";
    private readonly Lock _orderLock = new();
    private readonly Dictionary<string, int> _sessionOrder = [];
    private int _nextOrder;

    public AudioMixerSnapshot Read()
    {
        if (!TryReadSessions(out var cards, out var endpoints, out var selectedEndpointId, out var error))
        {
            return new AudioMixerSnapshot([], [], string.Empty, error);
        }

        ApplyStableOrder(cards);
        var preferences = preferencesStore.Current;
        var visibleCards = FilterVisibleSessions(cards, preferences, selectedEndpointId);
        var ordered = visibleCards
            .OrderBy(card => card.IsSystemSound ? -1 : 0)
            .ThenBy(card => GetOrder(card.Id))
            .ThenBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
            .Take(preferences.Audio.MaxSessions)
            .ToArray();

        return new AudioMixerSnapshot(ordered, endpoints, selectedEndpointId, null);
    }

    public IReadOnlyList<string> GetAvailableSessionNames()
    {
        if (!TryReadSessions(out var cards, out _, out _, out _))
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

    public AudioEditorInventorySnapshot GetEditorInventory()
    {
        if (!TryReadEditorInventory(out var inventory))
        {
            return new AudioEditorInventorySnapshot([], []);
        }

        return inventory;
    }

    public bool TrySetVolume(string sessionId, double value)
    {
        if (TryResolveMasterOutputDevice(sessionId, out var endpointId))
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDevice(endpointId);
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
        if (TryResolveMasterOutputDevice(sessionId, out var endpointId))
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDevice(endpointId);
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

    public bool TrySetAllInputMute(bool isMuted)
    {
        var changed = false;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in EnumerateCaptureDevices(enumerator))
            {
                using (device)
                {
                    device.AudioEndpointVolume.Mute = isMuted;
                    changed = true;
                }
            }
        }
        catch
        {
        }

        return changed;
    }

    public bool TrySetDiscordOutputsMute(bool isMuted)
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
                        if (processId == 0)
                        {
                            continue;
                        }

                        if (!string.Equals(ResolveStableAppKey(processId), "discord", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        session.SimpleAudioVolume.Mute = isMuted;
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
                        if (!string.Equals(ResolveLogicalSessionId(device, session, processId), sessionId, StringComparison.Ordinal))
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

    private bool TryReadSessions(
        out List<AudioSessionCard> cards,
        out AudioEndpointOption[] endpoints,
        out string selectedEndpointId,
        out string? error)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var devices = EnumerateRenderDevices(enumerator).ToArray();
            try
            {
                endpoints = devices
                    .Select(device => new AudioEndpointOption(device.ID, ResolveEndpointName(device), string.Equals(device.ID, defaultDevice.ID, StringComparison.Ordinal)))
                    .ToArray();

                selectedEndpointId = ResolveSelectedEndpointId(preferencesStore.Current.Audio.SelectedEndpointId, endpoints, defaultDevice.ID);
                cards = devices
                    .SelectMany(device => ReadEndpointSessions(device, includeMasterOutput: true))
                    .ToList();

                error = null;
                return true;
            }
            finally
            {
                foreach (var device in devices)
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            cards = [];
            endpoints = [];
            selectedEndpointId = string.Empty;
            error = $"Audio mixer unavailable: {ex.Message}";
            return false;
        }
    }

    private bool TryReadEditorInventory(out AudioEditorInventorySnapshot inventory)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var defaultRenderDevice = TryGetDefaultAudioEndpoint(enumerator, DataFlow.Render, Role.Multimedia);
            using var defaultCaptureDevice = TryGetDefaultAudioEndpoint(enumerator, DataFlow.Capture, Role.Communications);
            var selectedEndpointId = preferencesStore.Current.Audio.SelectedEndpointId;

            var renderDevices = EnumerateRenderDevices(enumerator).ToArray();
            var captureDevices = EnumerateCaptureDevices(enumerator).ToArray();

            try
            {
                var outputDevices = renderDevices
                    .Select(device =>
                    {
                        var sessions = ReadEndpointSessions(device, includeMasterOutput: false);
                        var master = BuildMasterCard(device);
                        return new AudioOutputDeviceSnapshot(
                            device.ID,
                            ResolveEndpointName(device),
                            string.Equals(device.ID, defaultRenderDevice?.ID, StringComparison.Ordinal),
                            string.Equals(device.ID, selectedEndpointId, StringComparison.Ordinal)
                                || (string.IsNullOrWhiteSpace(selectedEndpointId) && string.Equals(device.ID, defaultRenderDevice?.ID, StringComparison.Ordinal)),
                            master,
                            sessions);
                    })
                    .ToArray();

                var inputDevices = captureDevices
                    .Select(device => new AudioInputDeviceSnapshot(
                        device.ID,
                        ResolveEndpointName(device),
                        string.Equals(device.ID, defaultCaptureDevice?.ID, StringComparison.Ordinal),
                        (int)Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100),
                        device.AudioEndpointVolume.Mute))
                    .ToArray();

                inventory = new AudioEditorInventorySnapshot(outputDevices, inputDevices);
                return true;
            }
            finally
            {
                foreach (var device in renderDevices)
                {
                    device.Dispose();
                }

                foreach (var device in captureDevices)
                {
                    device.Dispose();
                }
            }
        }
        catch
        {
            inventory = new AudioEditorInventorySnapshot([], []);
            return false;
        }
    }

    private static IEnumerable<AudioSessionCard> FilterVisibleSessions(
        IEnumerable<AudioSessionCard> cards,
        DashboardPreferencesSnapshot preferences,
        string fallbackEndpointId)
    {
        var visibleTargets = preferences.Audio.VisibleDeviceSessions ?? [];
        if (visibleTargets.Count > 0)
        {
            return cards.Where(card => visibleTargets.Any(target =>
                string.Equals(target.EndpointId, ResolveEndpointId(card.Id), StringComparison.Ordinal)
                && string.Equals(target.SessionName, card.Name, StringComparison.OrdinalIgnoreCase)));
        }

        var matches = preferences.Audio.VisibleSessionMatches
            .Where(match => !string.IsNullOrWhiteSpace(match))
            .ToArray();

        var selectedEndpointId = string.IsNullOrWhiteSpace(preferences.Audio.SelectedEndpointId)
            ? fallbackEndpointId
            : preferences.Audio.SelectedEndpointId;
        var filtered = cards
            .Where(card => string.IsNullOrWhiteSpace(selectedEndpointId)
                || string.Equals(ResolveEndpointId(card.Id), selectedEndpointId, StringComparison.Ordinal))
            .Where(card => preferences.Audio.IncludeSystemSounds || !card.IsSystemSound);
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

    private static string ResolveEndpointId(string sessionId)
    {
        if (TryResolveMasterOutputDevice(sessionId, out var endpointId))
        {
            return endpointId;
        }

        var separatorIndex = sessionId.IndexOf('|');
        return separatorIndex > 0 ? sessionId[..separatorIndex] : sessionId;
    }

    private static List<AudioSessionCard> ReadEndpointSessions(MMDevice device, bool includeMasterOutput)
    {
        var rawSessions = new List<RawAudioSession>();
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
            var logicalSessionId = ResolveLogicalSessionId(device, session, processId);
            rawSessions.Add(new RawAudioSession(
                logicalSessionId,
                name,
                processId,
                ResolveEndpointName(device),
                (int)Math.Round(session.SimpleAudioVolume.Volume * 100),
                session.SimpleAudioVolume.Mute,
                false));
        }

        var cards = rawSessions
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
            .OrderBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (includeMasterOutput)
        {
            cards.Insert(0, BuildMasterCard(device));
        }

        return cards;
    }

    private static AudioSessionCard BuildMasterCard(MMDevice device)
    {
        return new AudioSessionCard(
            BuildMasterOutputSessionId(device.ID),
            "System Audio",
            0,
            ResolveEndpointName(device),
            (int)Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100),
            device.AudioEndpointVolume.Mute,
            true);
    }

    private static string ResolveSessionName(AudioSessionControl session, int processId)
    {
        if (processId == 0)
        {
            return "System Sounds";
        }

        return ResolveStableAppName(processId) ?? session.DisplayName ?? $"PID {processId}";
    }

    private static string ResolveSessionId(MMDevice device, AudioSessionControl session, int processId)
    {
        var identifier = session.GetSessionIdentifier;
        var sessionKey = string.IsNullOrWhiteSpace(identifier) ? $"pid:{processId}" : identifier;
        return $"{device.ID}|{sessionKey}";
    }

    private static string ResolveLogicalSessionId(MMDevice device, AudioSessionControl session, int processId)
    {
        if (processId == 0)
        {
            return BuildMasterOutputSessionId(device.ID);
        }

        var appKey = ResolveStableAppKey(processId);
        return string.IsNullOrWhiteSpace(appKey)
            ? ResolveSessionId(device, session, processId)
            : $"{device.ID}|app:{appKey}";
    }

    private static string BuildDetailLabel(bool isSystemSound, IReadOnlyList<int> distinctPids, int sessionCount, string endpointName)
    {
        return endpointName;
    }

    private static IEnumerable<MMDevice> EnumerateRenderDevices(MMDeviceEnumerator enumerator)
    {
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Cast<MMDevice>();
    }

    private static IEnumerable<MMDevice> EnumerateCaptureDevices(MMDeviceEnumerator enumerator)
    {
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Cast<MMDevice>();
    }

    private static MMDevice? TryGetDefaultAudioEndpoint(MMDeviceEnumerator enumerator, DataFlow dataFlow, Role role)
    {
        try
        {
            return enumerator.GetDefaultAudioEndpoint(dataFlow, role);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveSelectedEndpointId(string? preferredEndpointId, IReadOnlyList<AudioEndpointOption> endpoints, string defaultEndpointId)
    {
        if (!string.IsNullOrWhiteSpace(preferredEndpointId)
            && endpoints.Any(endpoint => string.Equals(endpoint.Id, preferredEndpointId, StringComparison.Ordinal)))
        {
            return preferredEndpointId.Trim();
        }

        return defaultEndpointId;
    }

    private static string BuildMasterOutputSessionId(string endpointId) => $"{MasterOutputSessionPrefix}{endpointId}";

    private static bool TryResolveMasterOutputDevice(string sessionId, out string endpointId)
    {
        if (sessionId.StartsWith(MasterOutputSessionPrefix, StringComparison.Ordinal))
        {
            endpointId = sessionId[MasterOutputSessionPrefix.Length..];
            return !string.IsNullOrWhiteSpace(endpointId);
        }

        endpointId = string.Empty;
        return false;
    }

    private static string ResolveEndpointName(MMDevice device)
    {
        return string.IsNullOrWhiteSpace(device.FriendlyName) ? device.ID : device.FriendlyName;
    }

    private static string? ResolveStableAppName(int processId)
    {
        try
        {
            using var process = global::System.Diagnostics.Process.GetProcessById(processId);
            var processName = process.ProcessName;
            if (string.IsNullOrWhiteSpace(processName))
            {
                return null;
            }

            return processName.ToLowerInvariant() switch
            {
                "discord" => "Discord",
                "spotify" => "Spotify",
                "firefox" => "Mozilla Firefox",
                "chrome" => "Chrome",
                "msedge" => "Microsoft Edge",
                "msedgewebview2" => "WebView",
                "epicgameslauncher" => "Epic Games Launcher",
                _ => processName
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveStableAppKey(int processId)
    {
        var appName = ResolveStableAppName(processId);
        return string.IsNullOrWhiteSpace(appName)
            ? null
            : new string(appName.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
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
