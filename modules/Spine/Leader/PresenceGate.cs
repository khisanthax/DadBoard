using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace DadBoard.Leader;

public sealed class PresenceState
{
    public string PcId { get; set; } = "";
    public bool Online { get; set; }
    public DateTime LastSeen { get; set; }
    public bool Afk { get; set; }
    public bool Available { get; set; } = true;
    public string? BlockedReason { get; set; }
    public bool Eligible { get; set; }
    public int IdleSeconds { get; set; }
}

public sealed class PresenceGate
{
    private readonly ConcurrentDictionary<string, PresenceState> _presence = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _afkThresholdSec;
    private readonly Action<string> _logInfo;

    public PresenceGate(int afkThresholdSec, Action<string> logInfo)
    {
        _afkThresholdSec = Math.Max(30, afkThresholdSec);
        _logInfo = logInfo;
    }

    public PresenceState UpdateHeartbeat(string pcId, bool online, DateTime lastSeen, int idleSeconds)
    {
        var state = _presence.GetOrAdd(pcId, _ => new PresenceState { PcId = pcId });
        var prevOnline = state.Online;
        var prevAfk = state.Afk;

        state.Online = online;
        state.LastSeen = lastSeen;
        state.IdleSeconds = Math.Max(0, idleSeconds);
        state.Afk = state.IdleSeconds >= _afkThresholdSec;

        ApplyEligibility(state);

        if (prevOnline != state.Online)
        {
            _logInfo($"Presence online {pcId}={(state.Online ? "online" : "offline")}");
        }

        if (prevAfk != state.Afk)
        {
            _logInfo($"Presence afk {pcId}={(state.Afk ? "afk" : "active")}");
        }

        return state;
    }

    public PresenceState UpdateOnlineOnly(string pcId, bool online, DateTime lastSeen)
    {
        var state = _presence.GetOrAdd(pcId, _ => new PresenceState { PcId = pcId });
        var prevOnline = state.Online;

        state.Online = online;
        state.LastSeen = lastSeen;

        ApplyEligibility(state);

        if (prevOnline != state.Online)
        {
            _logInfo($"Presence online {pcId}={(state.Online ? "online" : "offline")}");
        }

        return state;
    }

    public PresenceState SetManualAvailable(string pcId, bool available)
    {
        var state = _presence.GetOrAdd(pcId, _ => new PresenceState { PcId = pcId });
        if (state.Available != available)
        {
            state.Available = available;
            _logInfo($"Presence available {pcId}={(available ? "available" : "unavailable")}");
        }

        ApplyEligibility(state);
        return state;
    }

    public PresenceState? Get(string pcId)
    {
        return _presence.TryGetValue(pcId, out var state) ? state : null;
    }

    public List<PresenceState> Snapshot()
    {
        return new List<PresenceState>(_presence.Values);
    }

    private static void ApplyEligibility(PresenceState state)
    {
        state.Eligible = state.Online && state.Available && !state.Afk;
        if (!state.Online)
        {
            state.BlockedReason = "Offline";
            return;
        }

        if (!state.Available)
        {
            state.BlockedReason = "Manually unavailable";
            return;
        }

        if (state.Afk)
        {
            state.BlockedReason = "AFK";
            return;
        }

        state.BlockedReason = null;
    }
}
