using System;

namespace DadBoard.Gate;

sealed class GateMessage
{
    public string Type { get; set; } = "";
    public string PcId { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Role { get; set; }
    public string? ClaimRole { get; set; }
    public int Epoch { get; set; }
    public bool? Talking { get; set; }
    public double? Level { get; set; }
    public string? TalkStartTs { get; set; }
    public string? Ts { get; set; }
    public string? Version { get; set; }
    public string? TargetPcId { get; set; }
    public double? GainScalar { get; set; }
    public bool? AutoGainEnabled { get; set; }
}

enum Role
{
    Normal = 0,
    Leader = 1,
    CoCaptain = 2
}

sealed class ClaimRecord
{
    public string PcId { get; set; } = "";
    public Role Role { get; set; }
    public int Epoch { get; set; }
    public DateTime Timestamp { get; set; }
}

sealed class PeerState
{
    public string PcId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTime LastSeen { get; set; }
    public bool Talking { get; set; }
    public DateTime LastTalkActivity { get; set; }
    public DateTime TalkStart { get; set; }
    public double Level { get; set; }
}

sealed class GateSnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public int GatePort { get; set; }
    public int DiscoveryPort { get; set; }
    public string PcId { get; set; } = "";
    public string? LeaderId { get; set; }
    public string? CoCaptainId { get; set; }
    public Role EffectiveRole { get; set; }
    public bool Talking { get; set; }
    public bool Allowed { get; set; }
    public bool Gated { get; set; }
    public string? BlockedReason { get; set; }
    public float MicScalar { get; set; }
    public float BaselineVolume { get; set; }
    public double GainScalar { get; set; }
    public bool AutoGainEnabled { get; set; }
    public double AutoGainTarget { get; set; }
    public string? SelectedDeviceId { get; set; }
    public string? SelectedDeviceName { get; set; }
    public DateTime TalkStart { get; set; }
    public string? FloorOwner { get; set; }
    public DateTime LastRoleChange { get; set; }
    public DateTime LastUpdate { get; set; }
    public double? LastPeerSeenSeconds { get; set; }
    public int PeerCount { get; set; }
    public GatePeerSnapshot[] Peers { get; set; } = Array.Empty<GatePeerSnapshot>();
}

sealed class GatePeerSnapshot
{
    public string PcId { get; set; } = "";
    public Role EffectiveRole { get; set; }
    public bool Talking { get; set; }
    public DateTime LastSeen { get; set; }
}
