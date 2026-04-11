using System.Diagnostics;
using Monitor.Server.Config;
using Monitor.Server.Models;

namespace Monitor.Server.Services.System;

public sealed class ProcessCollector(DashboardSettings settings)
{
    private readonly Dictionary<int, (TimeSpan Cpu, DateTimeOffset At)> _previous = [];

    public ProcessesSnapshot Read()
    {
        var now = DateTimeOffset.UtcNow;
        var results = new List<ProcessCard>();

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
                results.Add(new ProcessCard(process.Id, process.ProcessName, Math.Max(cpuPercent, 0), process.WorkingSet64 / 1024d / 1024d));
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        var activeIds = results.Select(result => result.ProcessId).ToHashSet();
        foreach (var staleId in _previous.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            _previous.Remove(staleId);
        }

        var top = results
            .OrderByDescending(item => item.CpuPercent)
            .ThenByDescending(item => item.MemoryMb)
            .Take(settings.Processes.TopN)
            .ToArray();

        return new ProcessesSnapshot(top);
    }
}
