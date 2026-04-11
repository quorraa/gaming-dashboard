using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using Monitor.Server.Config;
using Monitor.Server.Models;

namespace Monitor.Server.Services.Temp;

public sealed class HwInfoClient(HttpClient httpClient, DashboardSettings settings)
{
    private const int SensorStringLength = 128;
    private const int UnitStringLength = 16;

    public async Task<TempsSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        if (!settings.HwInfo.Enabled)
        {
            return new TempsSnapshot(BuildEmptyCards("HWiNFO polling disabled."), "HWiNFO polling disabled.");
        }

        if (string.Equals(settings.HwInfo.Mode, "SharedMemory", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadFromSharedMemoryAsync(cancellationToken);
        }

        return await ReadFromHttpAsync(cancellationToken);
    }

    private async Task<TempsSnapshot> ReadFromHttpAsync(CancellationToken cancellationToken)
    {
        try
        {
            var sensors = await httpClient.GetFromJsonAsync<List<HwInfoSensor>>(settings.HwInfo.Endpoint, cancellationToken);
            if (sensors is null || sensors.Count == 0)
            {
                return new TempsSnapshot(BuildEmptyCards("No HWiNFO sensor rows returned."), "No HWiNFO sensor rows returned.");
            }

            var cards = settings.HwInfo.Sensors
                .Select(definition => BuildCard(definition, sensors))
                .ToArray();

            return new TempsSnapshot(cards, null);
        }
        catch (Exception ex)
        {
            return new TempsSnapshot(BuildEmptyCards(ex.Message), $"HWiNFO unavailable: {ex.Message}");
        }
    }

    private Task<TempsSnapshot> ReadFromSharedMemoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var mutex = Mutex.OpenExisting(settings.HwInfo.SharedMemoryMutexName);
            if (!mutex.WaitOne(TimeSpan.FromMilliseconds(250)))
            {
                return Task.FromResult(new TempsSnapshot(BuildEmptyCards("Shared memory busy."), "HWiNFO shared memory mutex timed out."));
            }

            try
            {
                using var mmf = MemoryMappedFile.OpenExisting(settings.HwInfo.SharedMemoryMapName, MemoryMappedFileRights.Read);
                using var accessor = mmf.CreateViewAccessor(0, Marshal.SizeOf<HwInfoSharedMemHeader>(), MemoryMappedFileAccess.Read);
                accessor.Read(0, out HwInfoSharedMemHeader header);

                var sensorNames = ReadSensorNames(mmf, header);
                var cards = ReadCards(mmf, header, sensorNames);
                return Task.FromResult(new TempsSnapshot(cards, null));
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return Task.FromResult(new TempsSnapshot(BuildEmptyCards("Shared memory not found."), "HWiNFO shared memory support is not enabled."));
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult(new TempsSnapshot(BuildEmptyCards("Shared memory map not found."), "HWiNFO shared memory map is unavailable."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new TempsSnapshot(BuildEmptyCards(ex.Message), $"HWiNFO shared memory unavailable: {ex.Message}"));
        }
    }

    private string[] ReadSensorNames(MemoryMappedFile mmf, HwInfoSharedMemHeader header)
    {
        var names = new string[header.dwNumSensorElements];
        using var stream = mmf.CreateViewStream(header.dwOffsetOfSensorSection, header.dwNumSensorElements * header.dwSizeOfSensorElement, MemoryMappedFileAccess.Read);
        var buffer = new byte[header.dwSizeOfSensorElement];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            for (var index = 0; index < header.dwNumSensorElements; index++)
            {
                stream.ReadExactly(buffer, 0, (int)header.dwSizeOfSensorElement);
                var sensor = Marshal.PtrToStructure<HwInfoSensorElement>(handle.AddrOfPinnedObject());
                names[index] = string.IsNullOrWhiteSpace(sensor.szSensorNameUser) ? sensor.szSensorNameOrig : sensor.szSensorNameUser;
            }
        }
        finally
        {
            handle.Free();
        }

        return names;
    }

    private TemperatureCard[] ReadCards(MemoryMappedFile mmf, HwInfoSharedMemHeader header, string[] sensorNames)
    {
        var readings = new List<SharedMemoryReading>((int)header.dwNumReadingElements);
        using var stream = mmf.CreateViewStream(header.dwOffsetOfReadingSection, header.dwNumReadingElements * header.dwSizeOfReadingElement, MemoryMappedFileAccess.Read);
        var buffer = new byte[header.dwSizeOfReadingElement];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            for (var index = 0; index < header.dwNumReadingElements; index++)
            {
                stream.ReadExactly(buffer, 0, (int)header.dwSizeOfReadingElement);
                var reading = Marshal.PtrToStructure<HwInfoReadingElement>(handle.AddrOfPinnedObject());
                var label = string.IsNullOrWhiteSpace(reading.szLabelUser) ? reading.szLabelOrig : reading.szLabelUser;
                var sourceName = reading.dwSensorIndex < sensorNames.Length ? sensorNames[reading.dwSensorIndex] : "Unknown Sensor";
                readings.Add(new SharedMemoryReading(sourceName, label, reading.szUnit, reading.Value));
            }
        }
        finally
        {
            handle.Free();
        }

        return settings.HwInfo.Sensors
            .Select(definition => BuildCard(definition, readings))
            .ToArray();
    }

    private TemperatureCard BuildCard(TemperatureSensorDefinition definition, IReadOnlyList<HwInfoSensor> sensors)
    {
        var matches = sensors
            .Where(sensor =>
                definition.MatchAny.Any(matchTerm =>
                    sensor.SensorName.Contains(matchTerm, StringComparison.OrdinalIgnoreCase) ||
                    sensor.SensorClass.Contains(matchTerm, StringComparison.OrdinalIgnoreCase)))
            .Select(sensor => new MatchedTemperatureReading(
                sensor.SensorName,
                sensor.SensorUnit,
                TryParseTemperature(sensor.SensorValue, out var value) ? value : null))
            .Where(reading => reading.Value.HasValue)
            .ToArray();

        if (matches.Length == 0)
        {
            return new TemperatureCard(definition.Key, definition.Label, null, "°C", definition.Warning, definition.Danger, "offline", 0, "No matching sensor");
        }

        var aggregate = AggregateMatches(definition, matches);
        var value = aggregate.Value;
        var severity = value >= definition.Danger ? "danger" :
            value >= definition.Warning ? "warning" : "good";

        var fillPercent = Math.Clamp(value / Math.Max(definition.Danger, 1) * 100, 0, 100);
        return new TemperatureCard(definition.Key, definition.Label, value, aggregate.Unit, definition.Warning, definition.Danger, severity, fillPercent, aggregate.SourceName);
    }

    private IReadOnlyList<TemperatureCard> BuildEmptyCards(string sourceName) =>
        settings.HwInfo.Sensors
            .Select(sensor => new TemperatureCard(sensor.Key, sensor.Label, null, "°C", sensor.Warning, sensor.Danger, "offline", 0, sourceName))
            .ToArray();

    private static bool TryParseTemperature(string raw, out double value)
    {
        var normalized = new string(raw.Where(ch => char.IsDigit(ch) || ch is '.' or '-' or ',').ToArray());
        normalized = normalized.Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private TemperatureCard BuildCard(TemperatureSensorDefinition definition, IReadOnlyList<SharedMemoryReading> readings)
    {
        var matches = readings
            .Where(reading =>
                definition.MatchAny.Any(matchTerm =>
                    reading.Label.Contains(matchTerm, StringComparison.OrdinalIgnoreCase) ||
                    reading.SourceName.Contains(matchTerm, StringComparison.OrdinalIgnoreCase)))
            .Select(reading => new MatchedTemperatureReading(
                $"{reading.SourceName}: {reading.Label}",
                string.IsNullOrWhiteSpace(reading.Unit) ? "°C" : reading.Unit,
                reading.Value))
            .ToArray();

        if (matches.Length == 0)
        {
            return new TemperatureCard(definition.Key, definition.Label, null, "°C", definition.Warning, definition.Danger, "offline", 0, "No matching sensor");
        }

        var aggregate = AggregateMatches(definition, matches);
        var value = aggregate.Value;
        var severity = value >= definition.Danger ? "danger" :
            value >= definition.Warning ? "warning" : "good";
        var fillPercent = Math.Clamp(value / Math.Max(definition.Danger, 1) * 100, 0, 100);
        return new TemperatureCard(definition.Key, definition.Label, value, aggregate.Unit, definition.Warning, definition.Danger, severity, fillPercent, aggregate.SourceName);
    }

    private static AggregatedTemperature AggregateMatches(
        TemperatureSensorDefinition definition,
        IReadOnlyList<MatchedTemperatureReading> matches)
    {
        if (string.Equals(definition.Aggregate, "Average", StringComparison.OrdinalIgnoreCase))
        {
            var average = matches.Average(match => match.Value!.Value);
            var unit = matches.FirstOrDefault(match => !string.IsNullOrWhiteSpace(match.Unit))?.Unit ?? "°C";
            var sources = string.Join(" + ", matches.Select(match => match.SourceName));
            var sourceName = matches.Count == 1 ? sources : $"Avg of {matches.Count} sensors: {sources}";
            return new AggregatedTemperature(average, unit, sourceName);
        }

        var first = matches[0];
        return new AggregatedTemperature(first.Value!.Value, first.Unit, first.SourceName);
    }

    private sealed record HwInfoSensor(
        string SensorApp,
        string SensorClass,
        string SensorName,
        string SensorValue,
        string SensorUnit,
        long SensorUpdateTime);

    private sealed record SharedMemoryReading(string SourceName, string Label, string Unit, double Value);
    private sealed record MatchedTemperatureReading(string SourceName, string Unit, double? Value);
    private sealed record AggregatedTemperature(double Value, string Unit, string SourceName);

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    private struct HwInfoSharedMemHeader
    {
        public uint dwSignature;
        public uint dwVersion;
        public uint dwRevision;
        public long poll_time;
        public uint dwOffsetOfSensorSection;
        public uint dwSizeOfSensorElement;
        public uint dwNumSensorElements;
        public uint dwOffsetOfReadingSection;
        public uint dwSizeOfReadingElement;
        public uint dwNumReadingElements;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    private struct HwInfoSensorElement
    {
        public uint dwSensorID;
        public uint dwSensorInst;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SensorStringLength)]
        public string szSensorNameOrig;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SensorStringLength)]
        public string szSensorNameUser;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    private struct HwInfoReadingElement
    {
        public uint tReading;
        public uint dwSensorIndex;
        public uint dwReadingID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SensorStringLength)]
        public string szLabelOrig;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SensorStringLength)]
        public string szLabelUser;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = UnitStringLength)]
        public string szUnit;

        public double Value;
        public double ValueMin;
        public double ValueMax;
        public double ValueAvg;
    }
}
