using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DadBoard.App;
using Timer = System.Threading.Timer;

namespace DadBoard.Gate;

sealed class GateEngine : IDisposable
{
    private const int HelloIntervalMs = 5000;
    private const int TalkingIntervalMs = 250;
    private const int DecisionIntervalMs = 100;
    private const int StatusIntervalMs = 1000;
    private const int PeerTimeoutMs = 15000;
    private const int DiscoveryPort = 39555;

    private readonly object _lock = new();
    private readonly string _pcId;
    private readonly string _displayName;

    private readonly SettingsStore _settingsStore;
    private GateSettings _settings;

    private readonly NetworkClient _network;
    private readonly AudioMonitor _audio;
    private readonly MicController _mic;
    private readonly MicLevelsStore _micLevelsStore;
    private readonly StatusWriter _statusWriter;

    private readonly Dictionary<string, PeerState> _peers = new(StringComparer.OrdinalIgnoreCase);

    private ClaimRecord? _leaderClaim;
    private ClaimRecord? _coClaim;
    private Role _effectiveRole;
    private DateTime _lastRoleChange;

    private bool _localTalking;
    private DateTime _localTalkStart;
    private DateTime _localLastTalkActivity;
    private double _localLevel;
    private bool _talkStateDirty;

    private bool _allowed;
    private bool _gated;
    private string? _lastFloorOwner;
    private GateSnapshot _lastSnapshot = new();

    private Timer? _helloTimer;
    private Timer? _talkTimer;
    private Timer? _decisionTimer;
    private Timer? _statusTimer;

    public GateEngine()
    {
        _pcId = Environment.MachineName;
        _displayName = Environment.MachineName;

        var dataRoot = Path.Combine(DataPaths.ResolveBaseDir(), "Gate");
        var logPath = Path.Combine(DataPaths.ResolveBaseDir(), "logs", "gate.log");
        Logger.Initialize(logPath);

        _settingsStore = new SettingsStore(Path.Combine(dataRoot, "settings.json"));
        _settings = _settingsStore.Load();
        _settings.SelectedDeviceId ??= GateAudioDevices.GetDefaultDeviceId();
        if (_settings.GatePort <= 0)
        {
            _settings.GatePort = 39566;
            _settingsStore.Save(_settings);
        }

        _micLevelsStore = new MicLevelsStore(Path.Combine(dataRoot, "mic_levels.json"));

        _audio = new AudioMonitor(_settings);
        _audio.AudioStateUpdated += OnAudioStateUpdated;

        _mic = new MicController();
        _mic.Initialize(_settings.SelectedDeviceId, _micLevelsStore);
        _mic.BaselineChanged += (_, _) => { };

        _statusWriter = new StatusWriter(Path.Combine(dataRoot, "status.json"));

        _network = new NetworkClient(_settings.GatePort);
        _network.MessageReceived += OnMessageReceived;

        AppDomain.CurrentDomain.ProcessExit += (_, _) => SafeRestore();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => SafeRestore();
    }

    public void Start()
    {
        Logger.Info($"GateEngine starting on UDP {_settings.GatePort}.");
        _network.Start();
        _audio.Start(_settings.SelectedDeviceId);

        BroadcastRoleClaim(_settings.DesiredRole, incrementEpoch: false);

        _helloTimer = new Timer(_ => SendHello(), null, 0, HelloIntervalMs);
        _talkTimer = new Timer(_ => SendTalking(), null, 0, TalkingIntervalMs);
        _decisionTimer = new Timer(_ => DecisionTick(), null, 0, DecisionIntervalMs);
        _statusTimer = new Timer(_ => WriteStatus(), null, 0, StatusIntervalMs);
    }

    public void ClaimRole(Role role)
    {
        BroadcastRoleClaim(role, incrementEpoch: true);
    }

    public void UpdateSettings(Action<GateSettings> update)
    {
        lock (_lock)
        {
            update(_settings);
            _settingsStore.Save(_settings);
            _audio.UpdateSettings(_settings);
        }
    }

    public IReadOnlyList<GateAudioDevice> GetInputDevices()
    {
        return GateAudioDevices.GetCaptureDevices();
    }

    public string? GetSelectedDeviceId()
    {
        lock (_lock)
        {
            return _settings.SelectedDeviceId;
        }
    }

    public void SetInputDevice(string? deviceId)
    {
        lock (_lock)
        {
            _settings.SelectedDeviceId = deviceId;
            _settingsStore.Save(_settings);
        }

        _audio.Restart(deviceId);
        _mic.SwitchDevice(deviceId);
        Logger.Info($"Gate device set to {(deviceId ?? "default")}");
    }

    public async Task CalibrateMicAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Logger.Info("Calibrate mic started.");
        _audio.Stop();
        using var sampler = new AudioSampler();
        var rms = await sampler.SampleRmsAsync(_settings.SelectedDeviceId, TimeSpan.FromSeconds(3), progress, ct)
            .ConfigureAwait(false);
        var current = _mic.BaselineVolume;
        var target = 0.06;
        var scale = rms > 0.001 ? target / rms : 1.0;
        var next = (float)Math.Clamp(current * scale, 0.10, 0.95);
        _mic.SetBaseline(next);
        Logger.Info($"Calibrate mic completed rms={rms:0.000} baseline={next:0.00}");
        _audio.Start(_settings.SelectedDeviceId);
    }

    public async Task QuickTestAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Logger.Info("Quick test started.");
        _mic.SetGated(true, (float)_settings.GateLevel);
        using var sampler = new AudioSampler();
        await sampler.SampleRmsAsync(_settings.SelectedDeviceId, TimeSpan.FromSeconds(3), progress, ct)
            .ConfigureAwait(false);
        _mic.RestoreBaseline();
        Logger.Info("Quick test completed.");
    }

    public GateSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return _lastSnapshot;
        }
    }

    public GateSettings GetSettingsCopy()
    {
        lock (_lock)
        {
            return new GateSettings
            {
                Sensitivity = _settings.Sensitivity,
                AttackMs = _settings.AttackMs,
                ReleaseMs = _settings.ReleaseMs,
                LeaseMs = _settings.LeaseMs,
                GateLevel = _settings.GateLevel,
                DesiredRole = _settings.DesiredRole,
                RoleEpoch = _settings.RoleEpoch,
                SelectedDeviceId = _settings.SelectedDeviceId,
                GatePort = _settings.GatePort
            };
        }
    }

    public double GetCurrentLevel()
    {
        lock (_lock)
        {
            return _localLevel;
        }
    }

    public void CalibrateSensitivity(double ambientLevel)
    {
        var target = Math.Min(1.0, ambientLevel + 0.02);
        UpdateSettings(settings => settings.Sensitivity = target);
    }

    private void OnAudioStateUpdated(AudioState state)
    {
        lock (_lock)
        {
            _localLevel = state.Level;
            if (state.Talking)
            {
                _localLastTalkActivity = state.Timestamp;
            }

            if (state.TalkingChanged)
            {
                _localTalking = state.Talking;
                if (state.Talking)
                {
                    _localTalkStart = state.Timestamp;
                    _localLastTalkActivity = state.Timestamp;
                }
                _talkStateDirty = true;
            }
        }
    }

    private void SendHello()
    {
        var now = DateTime.UtcNow;
        Role role;
        int epoch;
        lock (_lock)
        {
            role = _effectiveRole;
            epoch = _settings.RoleEpoch;
        }

        _network.Send(new GateMessage
        {
            Type = "HELLO",
            PcId = _pcId,
            DisplayName = _displayName,
            Role = role.ToString(),
            Epoch = epoch,
            Ts = now.ToString("O"),
            Version = "1.0"
        });
    }

    private void SendTalking()
    {
        bool shouldSend;
        bool talking;
        double level;
        DateTime talkStart;
        lock (_lock)
        {
            shouldSend = _talkStateDirty || _localTalking;
            talking = _localTalking;
            level = _localLevel;
            talkStart = _localTalkStart == default ? DateTime.UtcNow : _localTalkStart;
            _talkStateDirty = false;
        }

        if (!shouldSend)
        {
            return;
        }

        _network.Send(new GateMessage
        {
            Type = "TALKING",
            PcId = _pcId,
            Talking = talking,
            Level = level,
            TalkStartTs = talkStart.ToString("O"),
            Ts = DateTime.UtcNow.ToString("O")
        });
    }

    private void OnMessageReceived(GateMessage msg)
    {
        if (string.Equals(msg.PcId, _pcId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (!_peers.TryGetValue(msg.PcId, out var peer))
            {
                peer = new PeerState { PcId = msg.PcId, DisplayName = msg.PcId };
                _peers[msg.PcId] = peer;
            }

            peer.LastSeen = now;
            if (!string.IsNullOrWhiteSpace(msg.DisplayName))
            {
                peer.DisplayName = msg.DisplayName;
            }

            switch (msg.Type)
            {
                case "HELLO":
                    break;
                case "TALKING":
                    HandleTalking(msg, peer, now);
                    break;
                case "ROLE_CLAIM":
                    HandleRoleClaim(msg, now);
                    break;
            }
        }
    }

    private void HandleTalking(GateMessage msg, PeerState peer, DateTime now)
    {
        peer.Level = msg.Level ?? peer.Level;
        var talking = msg.Talking ?? false;
        if (talking)
        {
            peer.LastTalkActivity = now;
            var talkStart = ParseTimestamp(msg.TalkStartTs) ?? now;
            if (!peer.Talking)
            {
                peer.TalkStart = talkStart;
            }
        }
        peer.Talking = talking;
    }

    private void HandleRoleClaim(GateMessage msg, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(msg.ClaimRole))
        {
            return;
        }

        var claimRole = ParseRole(msg.ClaimRole);
        var claim = new ClaimRecord
        {
            PcId = msg.PcId,
            Role = claimRole,
            Epoch = msg.Epoch,
            Timestamp = ParseTimestamp(msg.Ts) ?? now
        };

        if (claimRole == Role.Leader)
        {
            if (IsHigherClaim(claim, _leaderClaim))
            {
                _leaderClaim = claim;
                _lastRoleChange = now;
            }
        }
        else if (claimRole == Role.CoCaptain)
        {
            if (IsHigherClaim(claim, _coClaim))
            {
                _coClaim = claim;
                _lastRoleChange = now;
            }
        }
        else
        {
            if (_leaderClaim != null && string.Equals(_leaderClaim.PcId, claim.PcId, StringComparison.OrdinalIgnoreCase))
            {
                if (IsHigherOrEqualClaim(claim, _leaderClaim))
                {
                    _leaderClaim = null;
                    _lastRoleChange = now;
                }
            }
            if (_coClaim != null && string.Equals(_coClaim.PcId, claim.PcId, StringComparison.OrdinalIgnoreCase))
            {
                if (IsHigherOrEqualClaim(claim, _coClaim))
                {
                    _coClaim = null;
                    _lastRoleChange = now;
                }
            }
        }
    }

    private void BroadcastRoleClaim(Role role, bool incrementEpoch)
    {
        ClaimRecord claim;
        lock (_lock)
        {
            if (incrementEpoch)
            {
                _settings.RoleEpoch += 1;
                _settings.DesiredRole = role;
                _settingsStore.Save(_settings);
            }

            claim = new ClaimRecord
            {
                PcId = _pcId,
                Role = role,
                Epoch = _settings.RoleEpoch,
                Timestamp = DateTime.UtcNow
            };

            if (role == Role.Leader)
            {
                _leaderClaim = claim;
            }
            else if (role == Role.CoCaptain)
            {
                _coClaim = claim;
            }
            else
            {
                if (_leaderClaim != null && string.Equals(_leaderClaim.PcId, _pcId, StringComparison.OrdinalIgnoreCase))
                {
                    _leaderClaim = null;
                }
                if (_coClaim != null && string.Equals(_coClaim.PcId, _pcId, StringComparison.OrdinalIgnoreCase))
                {
                    _coClaim = null;
                }
            }
            _lastRoleChange = claim.Timestamp;
        }

        _network.Send(new GateMessage
        {
            Type = "ROLE_CLAIM",
            PcId = claim.PcId,
            ClaimRole = claim.Role.ToString(),
            Epoch = claim.Epoch,
            Ts = claim.Timestamp.ToString("O")
        });

        Logger.Info($"Role claim broadcast: {claim.Role} epoch {claim.Epoch}");
    }

    private void DecisionTick()
    {
        GateSnapshot snapshot;
        bool gated;
        float micScalar;
        float baselineVolume;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var leaderId = _leaderClaim?.PcId;
            var coId = _coClaim?.PcId;

            var leaderTalking = IsTalking(leaderId, now);
            var coTalking = IsTalking(coId, now);

            Role localRole = GetEffectiveRole(leaderId, coId);
            if (localRole != _effectiveRole)
            {
                _effectiveRole = localRole;
                _lastRoleChange = now;
            }

            string? floorOwner = null;
            if (!leaderTalking && !coTalking)
            {
                floorOwner = SelectNormalFloorOwner(leaderId, coId, now);
            }

            bool allowed = localRole == Role.Leader || localRole == Role.CoCaptain;
            if (localRole == Role.Normal)
            {
                if (leaderTalking || coTalking)
                {
                    allowed = false;
                }
                else if (floorOwner == null)
                {
                    allowed = true;
                }
                else
                {
                    allowed = string.Equals(floorOwner, _pcId, StringComparison.OrdinalIgnoreCase);
                }
            }

            gated = localRole == Role.Normal && !allowed;
            if (gated != _gated)
            {
                _mic.SetGated(gated, (float)_settings.GateLevel);
                _gated = gated;
                Logger.Info(gated ? "Mic gated to 5%." : "Mic restored to normal.");
            }

            _allowed = allowed;
            _lastFloorOwner = floorOwner;
            micScalar = _mic.CurrentVolume;
            baselineVolume = _mic.BaselineVolume;

            PrunePeers(now);

            snapshot = BuildSnapshot(now, micScalar, baselineVolume);
            _lastSnapshot = snapshot;
        }
    }

    private GateSnapshot BuildSnapshot(DateTime now, float micScalar, float baselineVolume)
    {
        var leaderId = _leaderClaim?.PcId;
        var coId = _coClaim?.PcId;
        var peers = _peers.Values
            .OrderBy(p => p.PcId, StringComparer.OrdinalIgnoreCase)
            .Select(p => new GatePeerSnapshot
            {
                PcId = p.PcId,
                EffectiveRole = GetEffectiveRole(leaderId, coId, p.PcId),
                Talking = IsTalking(p.PcId, now),
                LastSeen = p.LastSeen
            })
            .ToArray();

        double? lastPeerSeenSeconds = null;
        if (_peers.Count > 0)
        {
            var newest = _peers.Values.Max(p => p.LastSeen);
            lastPeerSeenSeconds = Math.Max(0, (now - newest).TotalSeconds);
        }

        return new GateSnapshot
        {
            GatePort = _settings.GatePort,
            DiscoveryPort = DiscoveryPort,
            PcId = _pcId,
            LeaderId = leaderId,
            CoCaptainId = coId,
            EffectiveRole = _effectiveRole,
            Talking = IsTalking(_pcId, now),
            Allowed = _allowed,
            Gated = _gated,
            MicScalar = micScalar,
            BaselineVolume = baselineVolume,
            SelectedDeviceId = _settings.SelectedDeviceId,
            SelectedDeviceName = GateAudioDevices.GetDeviceName(_settings.SelectedDeviceId),
            TalkStart = _localTalkStart == default ? _localLastTalkActivity : _localTalkStart,
            FloorOwner = _lastFloorOwner,
            LastRoleChange = _lastRoleChange == default ? now : _lastRoleChange,
            LastUpdate = now,
            LastPeerSeenSeconds = lastPeerSeenSeconds,
            PeerCount = _peers.Count,
            Peers = peers
        };
    }

    private void WriteStatus()
    {
        GateSnapshot snapshot;
        lock (_lock)
        {
            snapshot = _lastSnapshot;
        }

        _statusWriter.Write(snapshot);
    }

    private Role GetEffectiveRole(string? leaderId, string? coId, string? pcId = null)
    {
        pcId ??= _pcId;
        if (!string.IsNullOrWhiteSpace(leaderId) && string.Equals(pcId, leaderId, StringComparison.OrdinalIgnoreCase))
        {
            return Role.Leader;
        }
        if (!string.IsNullOrWhiteSpace(coId) && string.Equals(pcId, coId, StringComparison.OrdinalIgnoreCase))
        {
            return Role.CoCaptain;
        }
        return Role.Normal;
    }

    private bool IsTalking(string? pcId, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(pcId))
        {
            return false;
        }

        if (string.Equals(pcId, _pcId, StringComparison.OrdinalIgnoreCase))
        {
            return (now - _localLastTalkActivity).TotalMilliseconds <= _settings.LeaseMs;
        }

        if (_peers.TryGetValue(pcId, out var peer))
        {
            return (now - peer.LastTalkActivity).TotalMilliseconds <= _settings.LeaseMs;
        }

        return false;
    }

    private string? SelectNormalFloorOwner(string? leaderId, string? coId, DateTime now)
    {
        var candidates = new List<(string PcId, DateTime TalkStart)>();

        if (GetEffectiveRole(leaderId, coId, _pcId) == Role.Normal && IsTalking(_pcId, now))
        {
            var start = _localTalkStart == default ? _localLastTalkActivity : _localTalkStart;
            candidates.Add((_pcId, start));
        }

        foreach (var peer in _peers.Values)
        {
            if (GetEffectiveRole(leaderId, coId, peer.PcId) != Role.Normal)
            {
                continue;
            }

            if (!IsTalking(peer.PcId, now))
            {
                continue;
            }

            var start = peer.TalkStart == default ? peer.LastTalkActivity : peer.TalkStart;
            candidates.Add((peer.PcId, start));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates
            .OrderBy(c => c.TalkStart)
            .ThenBy(c => c.PcId, StringComparer.OrdinalIgnoreCase)
            .First()
            .PcId;
    }

    private void PrunePeers(DateTime now)
    {
        var stale = _peers.Values
            .Where(p => (now - p.LastSeen).TotalMilliseconds > PeerTimeoutMs)
            .Select(p => p.PcId)
            .ToList();

        foreach (var pcId in stale)
        {
            _peers.Remove(pcId);
        }
    }

    private static Role ParseRole(string? role)
    {
        if (string.Equals(role, "Leader", StringComparison.OrdinalIgnoreCase))
        {
            return Role.Leader;
        }
        if (string.Equals(role, "CoCaptain", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "Co-Captain", StringComparison.OrdinalIgnoreCase))
        {
            return Role.CoCaptain;
        }
        return Role.Normal;
    }

    private static DateTime? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt))
        {
            return dt;
        }

        return null;
    }

    private static bool IsHigherClaim(ClaimRecord claim, ClaimRecord? current)
    {
        if (current == null)
        {
            return true;
        }
        if (claim.Epoch > current.Epoch)
        {
            return true;
        }
        if (claim.Epoch < current.Epoch)
        {
            return false;
        }
        if (claim.Timestamp > current.Timestamp)
        {
            return true;
        }
        if (claim.Timestamp < current.Timestamp)
        {
            return false;
        }
        return string.CompareOrdinal(claim.PcId, current.PcId) > 0;
    }

    private static bool IsHigherOrEqualClaim(ClaimRecord claim, ClaimRecord current)
    {
        if (claim.Epoch > current.Epoch)
        {
            return true;
        }
        if (claim.Epoch < current.Epoch)
        {
            return false;
        }
        if (claim.Timestamp > current.Timestamp)
        {
            return true;
        }
        if (claim.Timestamp < current.Timestamp)
        {
            return false;
        }
        return string.CompareOrdinal(claim.PcId, current.PcId) >= 0;
    }

    public void Dispose()
    {
        _helloTimer?.Dispose();
        _talkTimer?.Dispose();
        _decisionTimer?.Dispose();
        _statusTimer?.Dispose();

        _network.Dispose();
        _audio.Dispose();

        _mic.RestoreBaseline();
        _mic.Dispose();
    }

    private void SafeRestore()
    {
        try
        {
            _mic.RestoreBaseline();
        }
        catch
        {
        }
    }
}
