using System;

namespace DadBoard.Leader;

public sealed class AgentInfo
{
    public string PcId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public int WsPort { get; set; }
    public DateTime LastSeen { get; set; }
    public bool Online { get; set; }
    public string Version { get; set; } = "";

    public string LastStatus { get; set; } = "";
    public string LastStatusMessage { get; set; } = "";
    public string LastCommandId { get; set; } = "";
    public DateTime LastCommandTs { get; set; }
    public bool LastAckOk { get; set; }
    public string LastAckError { get; set; } = "";
    public DateTime LastAckTs { get; set; }
    public string LastResult { get; set; } = "";
    public string LastError { get; set; } = "";
    public string UpdateStatus { get; set; } = "";
    public string UpdateMessage { get; set; } = "";
}

public sealed class ConnectionInfo
{
    public string PcId { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string State { get; set; } = "";
    public string LastMessage { get; set; } = "";
    public string LastError { get; set; } = "";
}

public sealed class LeaderStateSnapshot
{
    public string LastUpdated { get; set; } = "";
    public AgentInfo[] Agents { get; set; } = Array.Empty<AgentInfo>();
    public ConnectionInfo[] Connections { get; set; } = Array.Empty<ConnectionInfo>();
}

public sealed class UpdateMirrorSnapshot
{
    public bool Enabled { get; set; }
    public string ManifestUrl { get; set; } = "";
    public string LocalHostUrl { get; set; } = "";
    public string LastManifestFetchUtc { get; set; } = "";
    public string LastManifestResult { get; set; } = "";
    public string LastDownloadUtc { get; set; } = "";
    public string LastDownloadResult { get; set; } = "";
    public string CachedVersions { get; set; } = "";
    public string LastError { get; set; } = "";
}
