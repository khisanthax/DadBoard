using System.Text.Json;

namespace DadBoard.Spine.Shared;

public static class ProtocolConstants
{
    public const int DefaultUdpPort = 39555;
    public const int DefaultWsPort = 39601;

    public const string TypeAgentHello = "AgentHello";
    public const string TypeCommandLaunchGame = "Command.LaunchGame";
    public const string TypeAck = "Ack";
    public const string TypeStatus = "Status";
}

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}

public sealed class AgentHello
{
    public string PcId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public int WsPort { get; set; }
    public string Version { get; set; } = "1.0";
    public string Ts { get; set; } = "";
}

public sealed class MessageEnvelope
{
    public string Type { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public string PcId { get; set; } = "";
    public JsonElement Payload { get; set; }
    public string Ts { get; set; } = "";
}

public sealed class LaunchGameCommand
{
    public string? GameId { get; set; }
    public string? LaunchUrl { get; set; }
    public string? ExePath { get; set; }
    public string[]? ProcessNames { get; set; }
    public int ReadyTimeoutSec { get; set; } = 120;
}

public sealed class AckPayload
{
    public bool Ok { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class StatusPayload
{
    public string State { get; set; } = "";
    public string? Message { get; set; }
    public string? GameId { get; set; }
}

public sealed class AgentConfig
{
    public string PcId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int UdpPort { get; set; } = ProtocolConstants.DefaultUdpPort;
    public int WsPort { get; set; } = ProtocolConstants.DefaultWsPort;
    public int HelloIntervalMs { get; set; } = 1000;
    public string Version { get; set; } = "1.0";
    public bool StartLeaderOnLogin { get; set; } = false;
}

public sealed class LeaderConfig
{
    public int UdpPort { get; set; } = ProtocolConstants.DefaultUdpPort;
    public int WsPortDefault { get; set; } = ProtocolConstants.DefaultWsPort;
    public int OnlineTimeoutSec { get; set; } = 5;
    public int CommandTimeoutSec { get; set; } = 30;
    public GameDefinition[] Games { get; set; } = Array.Empty<GameDefinition>();
}

public sealed class GameDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? LaunchUrl { get; set; }
    public string? ExePath { get; set; }
    public string[]? ProcessNames { get; set; }
    public int ReadyTimeoutSec { get; set; } = 120;
}

public sealed class KnownAgentRecord
{
    public string PcId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public int WsPort { get; set; }
    public string LastSeen { get; set; } = "";
    public bool Online { get; set; }
}
