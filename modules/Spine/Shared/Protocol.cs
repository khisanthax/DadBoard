using System.Text.Json;

namespace DadBoard.Spine.Shared;

public static class ProtocolConstants
{
    public const int DefaultUdpPort = 39555;
    public const int DefaultWsPort = 39601;

    public const string TypeAgentHello = "AgentHello";
    public const string TypeCommandLaunchGame = "Command.LaunchGame";
    public const string TypeCommandLaunchExe = "Command.LaunchExe";
    public const string TypeCommandRestartSteam = "Command.RestartSteam";
    public const string TypeCommandQuitGame = "Command.QuitGame";
    public const string TypeCommandShutdownApp = "Command.ShutdownApp";
    public const string TypeCommandScanSteamGames = "Command.ScanSteamGames";
    public const string TypeCommandUpdateSelf = "Command.UpdateSelf";
    public const string TypeCommandTriggerUpdateNow = "Command.TriggerUpdateNow";
    public const string TypeCommandResetUpdateFailures = "Command.ResetUpdateFailures";
    public const string TypeCommandRunSetupUpdate = "Command.RunSetupUpdate";
    public const string TypeUpdateSource = "Update.Source";
    public const string TypeSteamInventory = "SteamInventory";
    public const string TypeUpdateStatus = "UpdateStatus";
    public const string TypeAck = "Ack";
    public const string TypeStatus = "Status";
}

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
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

public sealed class LaunchExeCommand
{
    public string? ExePath { get; set; }
}

public sealed class RestartSteamCommand
{
    public bool ForceLogin { get; set; } = true;
}

public sealed class QuitGameCommand
{
    public int AppId { get; set; }
    public bool Force { get; set; } = true;
    public int GraceSeconds { get; set; } = 10;
}

public sealed class ShutdownAppCommand
{
    public string? Reason { get; set; }
}

public sealed class ScanSteamGamesCommand
{
}

public sealed class UpdateSelfCommand
{
    public string? UpdateBaseUrl { get; set; }
}

public sealed class TriggerUpdateNowCommand
{
    public string? ManifestUrl { get; set; }
}

public sealed class RunSetupUpdateCommand
{
    public string? ManifestUrl { get; set; }
}

public sealed class ResetUpdateFailuresCommand
{
    public string? Initiator { get; set; }
}

public sealed class UpdateSourcePayload
{
    public string? PrimaryManifestUrl { get; set; }
    public string? FallbackManifestUrl { get; set; }
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
    public string? ErrorClass { get; set; }
}

public sealed class UpdateStatusPayload
{
    public string Status { get; set; } = "";
    public string? Message { get; set; }
    public string? ErrorCode { get; set; }
    public string? VersionBefore { get; set; }
    public string? VersionAfter { get; set; }
    public bool? UpdatesDisabled { get; set; }
    public int? ConsecutiveFailures { get; set; }
    public string? LastError { get; set; }
    public string? LastResult { get; set; }
    public string? DisabledUntilUtc { get; set; }
    public string? LastResetUtc { get; set; }
    public string? LastResetBy { get; set; }
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
    public int UpdatePort { get; set; } = 39602;
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

public sealed class SteamGameEntry
{
    public int AppId { get; set; }
    public string? Name { get; set; }
    public string? InstallDir { get; set; }
}

public sealed class GameInventory
{
    public string PcId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public SteamGameEntry[] Games { get; set; } = Array.Empty<SteamGameEntry>();
    public string Ts { get; set; } = "";
    public string? Error { get; set; }
    public string? SteamPath { get; set; }
    public string[]? LibraryPaths { get; set; }
    public int ManifestCount { get; set; }
}

public sealed class UpdateVersionInfo
{
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
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
