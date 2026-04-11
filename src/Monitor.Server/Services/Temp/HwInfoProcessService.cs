using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using Monitor.Server.Config;

namespace Monitor.Server.Services.Temp;

public sealed class HwInfoProcessService(DashboardSettings settings, ILogger<HwInfoProcessService> logger)
{
    private readonly Lock _lock = new();
    private DateTimeOffset _lastStartAttemptAt = DateTimeOffset.MinValue;

    public string? EnsureSharedMemoryReady(CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.HwInfo.Mode, "SharedMemory", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (HasSharedMemoryMap())
        {
            return null;
        }

        var install = DetectInstall();
        if (!install.Exists)
        {
            return "HWiNFO64.exe not found.";
        }

        if (!install.SharedMemoryConfigured || !install.QuietStartupConfigured)
        {
            TryEnableStartupIniFlags(install);
            install = DetectInstall();
        }

        if (install.ProcessRunning)
        {
            return install.SharedMemoryConfigured
                ? install.QuietStartupConfigured
                    ? "HWiNFO is running but shared memory is not ready yet."
                    : "HWiNFO is running, but its startup flags are incomplete. Set OpenSensors=1, MinimalizeSensors=1, and MinimalizeMainWnd=1 in HWiNFO64.INI."
                : "HWiNFO is running but shared memory is not enabled in HWiNFO64.INI.";
        }

        if (!settings.HwInfo.AutoStart)
        {
            return install.SharedMemoryConfigured
                ? "HWiNFO shared memory map is unavailable."
                : "HWiNFO shared memory is not configured and auto-start is disabled.";
        }

        if (!install.SharedMemoryConfigured)
        {
            return "HWiNFO is installed, but HWiNFO64.INI is missing SensorsOnly=1, ServerRole=1, or SensorsSM=1.";
        }

        if (!TryStartHwInfo(install.ExecutablePath))
        {
            return "Failed to auto-start HWiNFO.";
        }

        var timeout = TimeSpan.FromMilliseconds(Math.Max(settings.HwInfo.AutoStartTimeoutMs, 1000));
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasSharedMemoryMap())
            {
                return null;
            }

            Thread.Sleep(250);
        }

        return install.QuietStartupConfigured
            ? "HWiNFO was started, but the shared memory map did not appear."
            : "HWiNFO launched, but its startup flags are incomplete. Set OpenSensors=1, MinimalizeSensors=1, and MinimalizeMainWnd=1 in HWiNFO64.INI.";
    }

    private bool TryStartHwInfo(string executablePath)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastStartAttemptAt < TimeSpan.FromSeconds(5))
            {
                return false;
            }

            _lastStartAttemptAt = now;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized
            };

            Process.Start(startInfo);
            SafeLogInformation("Started HWiNFO from {Path}", executablePath);
            return true;
        }
        catch (Exception ex)
        {
            SafeLogWarning(ex, "Failed to start HWiNFO from {Path}", executablePath);
            return false;
        }
    }

    private bool HasSharedMemoryMap()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(settings.HwInfo.SharedMemoryMapName, MemoryMappedFileRights.Read);
            return mmf is not null;
        }
        catch
        {
            return false;
        }
    }

    private void TryEnableStartupIniFlags(HwInfoInstallInfo install)
    {
        if (string.IsNullOrWhiteSpace(install.IniPath) || !File.Exists(install.IniPath))
        {
            return;
        }

        try
        {
            var lines = File.ReadAllLines(install.IniPath).ToList();
            var settingsIndex = lines.FindIndex(line => string.Equals(line.Trim(), "[Settings]", StringComparison.OrdinalIgnoreCase));
            if (settingsIndex < 0)
            {
                lines.Insert(0, "[Settings]");
                settingsIndex = 0;
            }

            EnsureIniValue(lines, settingsIndex, "SensorsOnly", "1");
            EnsureIniValue(lines, settingsIndex, "OpenSensors", "1");
            EnsureIniValue(lines, settingsIndex, "ServerRole", "1");
            EnsureIniValue(lines, settingsIndex, "SensorsSM", "1");
            EnsureIniValue(lines, settingsIndex, "MinimalizeSensors", "1");
            EnsureIniValue(lines, settingsIndex, "MinimalizeMainWnd", "1");
            EnsureIniValue(lines, settingsIndex, "ShowWelcomeAndProgress", "0");
            EnsureIniValue(lines, settingsIndex, "ShowRegDialog", "0");
            File.WriteAllLines(install.IniPath, lines);
            SafeLogInformation("Updated HWiNFO startup flags in {Path}", install.IniPath);
        }
        catch (Exception ex)
        {
            SafeLogWarning(ex, "Failed to update HWiNFO INI at {Path}", install.IniPath);
        }
    }

    private void SafeLogInformation(string message, params object?[] args)
    {
        try
        {
            logger.LogInformation(message, args);
        }
        catch
        {
        }
    }

    private void SafeLogWarning(Exception exception, string message, params object?[] args)
    {
        try
        {
            logger.LogWarning(exception, message, args);
        }
        catch
        {
        }
    }

    private static void EnsureIniValue(List<string> lines, int settingsIndex, string key, string value)
    {
        var nextSectionIndex = lines.FindIndex(settingsIndex + 1, line => line.StartsWith('[') && line.EndsWith(']'));
        var searchEnd = nextSectionIndex >= 0 ? nextSectionIndex : lines.Count;
        for (var index = settingsIndex + 1; index < searchEnd; index++)
        {
            if (!lines[index].StartsWith($"{key}=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            lines[index] = $"{key}={value}";
            return;
        }

        lines.Insert(searchEnd, $"{key}={value}");
    }

    private HwInfoInstallInfo DetectInstall()
    {
        var executablePath = ResolveExecutablePath();
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return HwInfoInstallInfo.Missing;
        }

        var iniPath = Path.ChangeExtension(executablePath, ".INI");
        var values = ReadIniValues(iniPath);
        return new HwInfoInstallInfo(
            true,
            executablePath,
            iniPath,
            IsHwInfoRunning(),
            values.GetValueOrDefault("SensorsOnly") == "1"
                && values.GetValueOrDefault("OpenSensors") == "1"
                && values.GetValueOrDefault("ServerRole") == "1"
                && values.GetValueOrDefault("SensorsSM") == "1",
            values.GetValueOrDefault("MinimalizeSensors") == "1"
                && values.GetValueOrDefault("MinimalizeMainWnd") == "1"
                && values.GetValueOrDefault("ShowWelcomeAndProgress", "1") == "0");
    }

    private string ResolveExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(settings.HwInfo.ExecutablePath))
        {
            return settings.HwInfo.ExecutablePath;
        }

        var candidates = new[]
        {
            @"C:\Program Files\HWiNFO64\HWiNFO64.EXE",
            @"C:\Program Files (x86)\HWiNFO64\HWiNFO64.EXE"
        };

        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private static Dictionary<string, string> ReadIniValues(string iniPath)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(iniPath))
        {
            return values;
        }

        var inSettings = false;
        foreach (var line in File.ReadAllLines(iniPath))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                inSettings = string.Equals(trimmed, "[Settings]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSettings || string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith(';'))
            {
                continue;
            }

            var separatorIndex = trimmed.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            values[trimmed[..separatorIndex].Trim()] = trimmed[(separatorIndex + 1)..].Trim();
        }

        return values;
    }

    private static bool IsHwInfoRunning()
    {
        return Process.GetProcessesByName("HWiNFO64").Length > 0
            || Process.GetProcessesByName("HWiNFO32").Length > 0;
    }

    private sealed record HwInfoInstallInfo(
        bool Exists,
        string ExecutablePath,
        string IniPath,
        bool ProcessRunning,
        bool SharedMemoryConfigured,
        bool QuietStartupConfigured)
    {
        public static HwInfoInstallInfo Missing { get; } = new(false, string.Empty, string.Empty, false, false, false);
    }
}
