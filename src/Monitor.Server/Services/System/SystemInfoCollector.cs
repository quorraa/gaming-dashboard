using System.Management;
using System.Text;
using Microsoft.Win32;
using Monitor.Server.Models;

namespace Monitor.Server.Services.System;

public sealed class SystemInfoCollector
{
    private SystemInfoSnapshot? _cached;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

    public SystemInfoSnapshot Read()
    {
        if (_cached is null || DateTimeOffset.UtcNow - _lastRefresh > TimeSpan.FromMinutes(5))
        {
            _cached = new SystemInfoSnapshot(
                Environment.MachineName,
                QuerySingle("SELECT Name FROM Win32_Processor", "Name"),
                QuerySingle("SELECT Name FROM Win32_VideoController", "Name"),
                ReadRamLabel(),
                QuerySingle("SELECT Product FROM Win32_BaseBoard", "Product"),
                QuerySingle("SELECT Caption FROM Win32_OperatingSystem", "Caption"),
                ReadMonitorLabel(),
                FormatUptime());
            _lastRefresh = DateTimeOffset.UtcNow;
        }

        return _cached with { Uptime = FormatUptime() };
    }

    private static string QuerySingle(string query, string propertyName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            foreach (var item in searcher.Get().Cast<ManagementObject>())
            {
                var value = item[propertyName]?.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }
        catch
        {
        }

        return "Unavailable";
    }

    private static string ReadRamLabel()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            var bytes = searcher.Get().Cast<ManagementObject>().Select(item => Convert.ToDouble(item["TotalPhysicalMemory"] ?? 0)).FirstOrDefault();
            if (bytes <= 0)
            {
                return "Unavailable";
            }

            var gigabytes = bytes / 1024d / 1024d / 1024d;
            return $"{gigabytes:F0} GB";
        }
        catch
        {
            return "Unavailable";
        }
    }

    private static string ReadMonitorLabel()
    {
        try
        {
            var scope = new ManagementScope(@"root\wmi");
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT UserFriendlyName FROM WmiMonitorID"));
            foreach (var item in searcher.Get().Cast<ManagementObject>())
            {
                if (item["UserFriendlyName"] is ushort[] chars)
                {
                    var value = DecodeMonitorString(chars);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
        }
        catch
        {
        }

        try
        {
            var names = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY")?.GetSubKeyNames();
            var first = names?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
            {
                return first;
            }
        }
        catch
        {
        }

        return "Unavailable";
    }

    private static string DecodeMonitorString(IEnumerable<ushort> data)
    {
        var builder = new StringBuilder();
        foreach (var value in data)
        {
            if (value == 0)
            {
                continue;
            }

            builder.Append(Convert.ToChar(value));
        }

        return builder.ToString();
    }

    private static string FormatUptime()
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
    }
}
