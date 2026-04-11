using System.Net.NetworkInformation;
using Monitor.Server.Config;
using Monitor.Server.Models;

namespace Monitor.Server.Services.System;

public sealed class NetworkCollector(DashboardSettings settings)
{
    private DateTimeOffset _lastSampleAt = DateTimeOffset.MinValue;
    private long _lastBytesReceived;
    private long _lastBytesSent;
    private DateTimeOffset _lastPingAt = DateTimeOffset.MinValue;
    private readonly Queue<double> _downloadHistory = new();
    private readonly Queue<double> _uploadHistory = new();
    private readonly Queue<double> _pingHistory = new();
    private double? _lastPingMs;
    private string _interfaceLabel = "No active interface";

    public async Task<NetworkSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var (bytesReceived, bytesSent, label) = SampleInterfaces();
        var downloadMbps = 0d;
        var uploadMbps = 0d;

        if (_lastSampleAt != DateTimeOffset.MinValue)
        {
            var elapsedSeconds = Math.Max((now - _lastSampleAt).TotalSeconds, 0.001d);
            downloadMbps = Math.Max(0, ((bytesReceived - _lastBytesReceived) * 8d) / 1_000_000d / elapsedSeconds);
            uploadMbps = Math.Max(0, ((bytesSent - _lastBytesSent) * 8d) / 1_000_000d / elapsedSeconds);
        }

        _lastSampleAt = now;
        _lastBytesReceived = bytesReceived;
        _lastBytesSent = bytesSent;
        _interfaceLabel = label;

        Push(_downloadHistory, downloadMbps, settings.Network.HistoryPoints);
        Push(_uploadHistory, uploadMbps, settings.Network.HistoryPoints);

        if (now - _lastPingAt >= TimeSpan.FromMilliseconds(settings.Network.PingIntervalMs))
        {
            _lastPingMs = await PingAsync(settings.Network.PingTarget);
            if (_lastPingMs is double pingValue)
            {
                Push(_pingHistory, pingValue, settings.Network.HistoryPoints);
            }

            _lastPingAt = now;
        }

        var jitter = settings.Network.EnableJitter ? CalculateJitter() : null;
        return new NetworkSnapshot(downloadMbps, uploadMbps, _lastPingMs, jitter, _downloadHistory.ToArray(), _uploadHistory.ToArray(), _interfaceLabel);
    }

    private static void Push(Queue<double> queue, double value, int max)
    {
        queue.Enqueue(value);
        while (queue.Count > max)
        {
            queue.Dequeue();
        }
    }

    private static async Task<double?> PingAsync(string target)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(target, 1200);
            if (reply.Status == IPStatus.Success)
            {
                return reply.RoundtripTime;
            }
        }
        catch
        {
        }

        return null;
    }

    private (long BytesReceived, long BytesSent, string Label) SampleInterfaces()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
            .Where(nic => nic.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .Where(nic => !IsVirtualAdapter(nic))
            .ToArray();

        if (interfaces.Length == 0)
        {
            return (0, 0, "No active interface");
        }

        var preferred = interfaces
            .OrderByDescending(HasGateway)
            .ThenByDescending(nic => nic.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)
            .ThenBy(nic => nic.Name, StringComparer.OrdinalIgnoreCase)
            .First();

        long received = 0;
        long sent = 0;
        foreach (var nic in interfaces.Where(nic => string.Equals(nic.Id, preferred.Id, StringComparison.Ordinal)))
        {
            var stats = nic.GetIPStatistics();
            received += stats.BytesReceived;
            sent += stats.BytesSent;
        }

        return (received, sent, preferred.Name);
    }

    private double? CalculateJitter()
    {
        if (_pingHistory.Count < 2)
        {
            return null;
        }

        var samples = _pingHistory.ToArray();
        var average = samples.Average();
        var variance = samples.Select(sample => Math.Pow(sample - average, 2)).Average();
        return Math.Sqrt(variance);
    }

    private static bool HasGateway(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GatewayAddresses.Any(address => address.Address is not null && !address.Address.Equals(global::System.Net.IPAddress.Any));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsVirtualAdapter(NetworkInterface nic)
    {
        var text = $"{nic.Name} {nic.Description}";
        return text.Contains("virtual", StringComparison.OrdinalIgnoreCase)
            || text.Contains("vmware", StringComparison.OrdinalIgnoreCase)
            || text.Contains("hyper-v", StringComparison.OrdinalIgnoreCase)
            || text.Contains("vethernet", StringComparison.OrdinalIgnoreCase)
            || text.Contains("npcap", StringComparison.OrdinalIgnoreCase)
            || text.Contains("loopback", StringComparison.OrdinalIgnoreCase)
            || text.Contains("wfp", StringComparison.OrdinalIgnoreCase);
    }
}
