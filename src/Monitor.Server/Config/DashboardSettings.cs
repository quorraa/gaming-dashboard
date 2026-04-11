namespace Monitor.Server.Config;

public sealed class DashboardSettings
{
    public int RefreshIntervalMs { get; set; } = 750;
    public NetworkSettings Network { get; set; } = new();
    public ProcessSettings Processes { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public HwInfoSettings HwInfo { get; set; } = new();
    public DiscordSettings Discord { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
}

public sealed class NetworkSettings
{
    public string PingTarget { get; set; } = "1.1.1.1";
    public bool EnableJitter { get; set; } = true;
    public int HistoryPoints { get; set; } = 28;
    public int PingIntervalMs { get; set; } = 1500;
}

public sealed class ProcessSettings
{
    public int TopN { get; set; } = 8;
}

public sealed class AudioSettings
{
    public int MaxSessions { get; set; } = 12;
    public bool IncludeSystemSounds { get; set; } = true;
    public List<string> VisibleSessionMatches { get; set; } = [];
}

public sealed class HwInfoSettings
{
    public bool Enabled { get; set; } = true;
    public string Mode { get; set; } = "SharedMemory";
    public string Endpoint { get; set; } = "http://127.0.0.1:55555/";
    public string SharedMemoryMapName { get; set; } = @"Global\HWiNFO_SENS_SM2";
    public string SharedMemoryMutexName { get; set; } = @"Global\HWiNFO_SM2_MUTEX";
    public List<TemperatureSensorDefinition> Sensors { get; set; } = [];

    public static List<TemperatureSensorDefinition> DefaultSensors() =>
    [
        new() { Key = "cpu", Label = "CPU", MatchAny = ["CPU Package", "CPU (Tctl/Tdie)"], Warning = 70, Danger = 85 },
        new() { Key = "gpu", Label = "GPU", MatchAny = ["GPU Temperature", "GPU Core"], Warning = 72, Danger = 84 },
        new() { Key = "ssd", Label = "SSD", MatchAny = ["Drive Temperature", "Composite"], Warning = 55, Danger = 65 },
        new() { Key = "ram", Label = "RAM", MatchAny = ["DIMM Temperature", "Memory Temperature", "SPD Hub Temperature"], Aggregate = "Average", Warning = 55, Danger = 70 },
        new() { Key = "vrm", Label = "VRM", MatchAny = ["VRM", "MOS"], Warning = 75, Danger = 90 },
        new() { Key = "chipset", Label = "Chipset", MatchAny = ["PCH", "Chipset"], Warning = 68, Danger = 82 }
    ];
}

public sealed class TemperatureSensorDefinition
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public List<string> MatchAny { get; set; } = [];
    public string Aggregate { get; set; } = "First";
    public double Warning { get; set; } = 70;
    public double Danger { get; set; } = 85;
}

public sealed class DiscordSettings
{
    public bool Enabled { get; set; }
    public string Token { get; set; } = string.Empty;
    public ulong GuildId { get; set; }
    public ulong MessagesChannelId { get; set; }
    public ulong VoiceChannelId { get; set; }
    public ulong TrackedUserId { get; set; }
    public int LatestMessagesCount { get; set; } = 6;
    public List<ulong> FavoriteUserIds { get; set; } = [];
}

public sealed class UiSettings
{
    public List<string> VisiblePanels { get; set; } =
    [
        "temps",
        "network",
        "discord",
        "audio",
        "processes",
        "system"
    ];
}
