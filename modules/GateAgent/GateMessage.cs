using System;

namespace GateAgent;

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
    public string PcId { get; set; } = "";
    public Role EffectiveRole { get; set; }
    public bool Talking { get; set; }
    public bool Allowed { get; set; }
    public bool Gated { get; set; }
    public float MicScalar { get; set; }
    public string? FloorOwner { get; set; }
    public DateTime LastRoleChange { get; set; }
    public DateTime LastUpdate { get; set; }
    public GatePeerSnapshot[] Peers { get; set; } = Array.Empty<GatePeerSnapshot>();
}

sealed class GatePeerSnapshot
{
    public string PcId { get; set; } = "";
    public Role EffectiveRole { get; set; }
    public bool Talking { get; set; }
    public DateTime LastSeen { get; set; }
}
