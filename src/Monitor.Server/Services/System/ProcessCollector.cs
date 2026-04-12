using System.Diagnostics;
using System.Management;
using Monitor.Server.Config;
using Monitor.Server.Models;

namespace Monitor.Server.Services.System;

public sealed class ProcessCollector(DashboardSettings settings)
{
    private readonly Dictionary<int, (TimeSpan Cpu, DateTimeOffset At)> _previous = [];

    public ProcessesSnapshot Read()
    {
        var now = DateTimeOffset.UtcNow;
        var results = new List<ProcessSample>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.HasExited)
                {
                    continue;
                }

                var totalCpu = process.TotalProcessorTime;
                var cpuPercent = 0d;
                if (_previous.TryGetValue(process.Id, out var previous))
                {
                    var elapsedSeconds = Math.Max((now - previous.At).TotalSeconds, 0.001d);
                    cpuPercent = ((totalCpu - previous.Cpu).TotalMilliseconds / (elapsedSeconds * 1000d * Environment.ProcessorCount)) * 100d;
                }

                _previous[process.Id] = (totalCpu, now);
                results.Add(new ProcessSample(process.Id, process.ProcessName, Math.Max(cpuPercent, 0), process.WorkingSet64 / 1024d / 1024d));
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        var parentMap = ReadParentMap();
        var activeById = results.ToDictionary(result => result.ProcessId);
        var aggregated = new Dictionary<int, ProcessCard>();

        foreach (var sample in results)
        {
            var rootId = ResolveRootProcessId(sample.ProcessId, activeById, parentMap);
            if (!activeById.TryGetValue(rootId, out var root))
            {
                root = sample;
                rootId = sample.ProcessId;
            }

            if (aggregated.TryGetValue(rootId, out var current))
            {
                aggregated[rootId] = current with
                {
                    CpuPercent = current.CpuPercent + sample.CpuPercent,
                    MemoryMb = current.MemoryMb + sample.MemoryMb
                };
            }
            else
            {
                aggregated[rootId] = new ProcessCard(rootId, root.Name, sample.CpuPercent, sample.MemoryMb);
            }
        }

        var activeIds = results.Select(result => result.ProcessId).ToHashSet();
        foreach (var staleId in _previous.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            _previous.Remove(staleId);
        }

        var top = aggregated.Values
            .OrderByDescending(item => item.CpuPercent)
            .ThenByDescending(item => item.MemoryMb)
            .Take(settings.Processes.TopN)
            .ToArray();

        return new ProcessesSnapshot(top);
    }

    private static Dictionary<int, int> ReadParentMap()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process");
            return searcher.Get()
                .Cast<ManagementObject>()
                .Select(item => new
                {
                    ProcessId = Convert.ToInt32(item["ProcessId"] ?? 0),
                    ParentProcessId = Convert.ToInt32(item["ParentProcessId"] ?? 0)
                })
                .Where(item => item.ProcessId > 0)
                .ToDictionary(item => item.ProcessId, item => item.ParentProcessId);
        }
        catch
        {
            return [];
        }
    }

    private static int ResolveRootProcessId(
        int processId,
        IReadOnlyDictionary<int, ProcessSample> activeById,
        IReadOnlyDictionary<int, int> parentMap)
    {
        var currentId = processId;
        var visited = new HashSet<int>();

        while (visited.Add(currentId)
               && parentMap.TryGetValue(currentId, out var parentId)
               && parentId > 0
               && activeById.TryGetValue(parentId, out var parent))
        {
            if (IsTreeBoundary(parent.Name))
            {
                break;
            }

            currentId = parentId;
        }

        return currentId;
    }

    private static bool IsTreeBoundary(string processName) =>
        processName.Equals("explorer", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("svchost", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("services", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("wininit", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("winlogon", StringComparison.OrdinalIgnoreCase);

    private sealed record ProcessSample(
        int ProcessId,
        string Name,
        double CpuPercent,
        double MemoryMb);
}
