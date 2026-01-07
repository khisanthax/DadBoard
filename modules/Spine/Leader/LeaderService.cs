using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using Timer = System.Threading.Timer;
using System.Threading.Tasks;
using DadBoard.Spine.Shared;

namespace DadBoard.Leader;

public sealed class LeaderService : IDisposable
{
    private readonly string _baseDir;
    private readonly string _leaderDir;
    private readonly string _logDir;
    private readonly string _configPath;
    private readonly string _knownAgentsPath;
    private readonly string _statePath;

    private readonly LeaderLogger _logger;
    private readonly KnownAgentsStore _knownAgentsStore;
    private readonly LeaderStateWriter _stateWriter;
    private readonly string? _localAgentPcId;
    private readonly HashSet<string> _localIps;

    private LeaderConfig _config = new();

    private readonly ConcurrentDictionary<string, AgentInfo> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AgentConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Timer> _commandTimeouts = new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;
    private Timer? _offlineTimer;
    private Timer? _persistTimer;
    private Timer? _stateTimer;

    public LeaderService(string? baseDir = null)
    {
        _baseDir = baseDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DadBoard");
        _leaderDir = Path.Combine(_baseDir, "Leader");
        _logDir = Path.Combine(_baseDir, "logs");
        _configPath = Path.Combine(_leaderDir, "leader.config.json");
        _knownAgentsPath = Path.Combine(_baseDir, "known_agents.json");
        _statePath = Path.Combine(_leaderDir, "leader_state.json");

        Directory.CreateDirectory(_leaderDir);
        Directory.CreateDirectory(_logDir);

        _logger = new LeaderLogger(Path.Combine(_logDir, "leader.log"));
        _knownAgentsStore = new KnownAgentsStore(_knownAgentsPath);
        _stateWriter = new LeaderStateWriter(_statePath);
        _localAgentPcId = TryLoadLocalAgentPcId();
        _localIps = GetLocalIps();

        _config = LoadConfig();

        StartDiscovery();
        _offlineTimer = new Timer(_ => UpdateOnlineStates(), null, 0, 1000);
        _persistTimer = new Timer(_ => PersistKnownAgents(), null, 0, 5000);
        _stateTimer = new Timer(_ => PersistState(), null, 0, 1000);
    }

    public LeaderConfig Config => _config;

    public IReadOnlyList<GameDefinition> GetGames() => _config.Games;

    public bool IsLocalAgent(string pcId, string ip)
    {
        if (!string.IsNullOrWhiteSpace(_localAgentPcId) &&
            string.Equals(pcId, _localAgentPcId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(ip) && _localIps.Contains(ip);
    }

    public IReadOnlyList<AgentInfo> GetAgentsSnapshot()
    {
        return _agents.Values.Select(a => new AgentInfo
        {
            PcId = a.PcId,
            Name = a.Name,
            Ip = a.Ip,
            WsPort = a.WsPort,
            LastSeen = a.LastSeen,
            Online = a.Online,
            LastStatus = a.LastStatus,
            LastStatusMessage = a.LastStatusMessage,
            LastCommandId = a.LastCommandId,
            LastCommandTs = a.LastCommandTs,
            LastAckOk = a.LastAckOk,
            LastAckError = a.LastAckError,
            LastAckTs = a.LastAckTs
        }).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<ConnectionInfo> GetConnectionsSnapshot()
    {
        return _connections.Values.Select(c => new ConnectionInfo
        {
            PcId = c.PcId,
            Endpoint = c.Endpoint.ToString(),
            State = c.State,
            LastMessage = c.LastMessage == default ? "" : c.LastMessage.ToString("O"),
            LastError = c.LastError ?? ""
        }).OrderBy(c => c.PcId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void LaunchOnAll(GameDefinition game)
    {
        foreach (var agent in _agents.Values)
        {
            if (!agent.Online)
            {
                continue;
            }

            _ = Task.Run(() => SendLaunchCommand(agent, game));
        }
    }

    public bool SendTestCommand(string pcId, out string? error)
    {
        return SendTestCommand(pcId, "notepad.exe", out error);
    }

    public bool SendTestCommand(string pcId, string exePath, out string? error)
    {
        error = null;
        if (!_agents.TryGetValue(pcId, out var agent))
        {
            error = "Agent not found.";
            return false;
        }

        if (!agent.Online)
        {
            error = "Agent is offline.";
            return false;
        }

        _ = Task.Run(() => SendLaunchExe(agent, exePath));
        return true;
    }

    private LeaderConfig LoadConfig()
    {
        if (!File.Exists(_configPath))
        {
            var config = new LeaderConfig();
            SaveConfig(config);
            return config;
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<LeaderConfig>(json, JsonUtil.Options);
            return config ?? new LeaderConfig();
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to load config: {ex.Message}");
            return new LeaderConfig();
        }
    }

    private void SaveConfig(LeaderConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonUtil.Options);
        File.WriteAllText(_configPath, json);
    }

    private void StartDiscovery()
    {
        _udp = new UdpClient(_config.UdpPort);
        _ = Task.Run(ReceiveHelloLoop);
        _logger.Info($"Listening for AgentHello on UDP {_config.UdpPort}.");
    }

    private async Task ReceiveHelloLoop()
    {
        if (_udp == null)
        {
            return;
        }

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                var json = Encoding.UTF8.GetString(result.Buffer);
                var hello = JsonSerializer.Deserialize<AgentHello>(json, JsonUtil.Options);
                if (hello != null)
                {
                    var ip = result.RemoteEndPoint.Address.ToString();
                    HandleHello(hello, ip);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warn($"UDP receive error: {ex.Message}");
            }
        }
    }

    private void HandleHello(AgentHello hello, string ip)
    {
        var agent = _agents.GetOrAdd(hello.PcId, _ => new AgentInfo { PcId = hello.PcId });
        agent.Name = string.IsNullOrWhiteSpace(hello.Name) ? hello.PcId : hello.Name;
        agent.Ip = string.IsNullOrWhiteSpace(ip) ? hello.Ip : ip;
        agent.WsPort = hello.WsPort > 0 ? hello.WsPort : _config.WsPortDefault;
        agent.LastSeen = DateTime.UtcNow;
        agent.Online = true;
    }

    private void UpdateOnlineStates()
    {
        var now = DateTime.UtcNow;
        foreach (var agent in _agents.Values)
        {
            if ((now - agent.LastSeen).TotalSeconds > _config.OnlineTimeoutSec)
            {
                agent.Online = false;
            }
        }
    }

    private async Task SendLaunchCommand(AgentInfo agent, GameDefinition game)
    {
        var connection = await EnsureConnection(agent).ConfigureAwait(false);
        if (connection == null || connection.Socket.State != WebSocketState.Open)
        {
            agent.LastStatus = "ws_error";
            agent.LastStatusMessage = connection?.LastError ?? "Unable to connect";
            return;
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var payload = new LaunchGameCommand
        {
            GameId = game.Id,
            LaunchUrl = game.LaunchUrl,
            ExePath = game.ExePath,
            ProcessNames = game.ProcessNames,
            ReadyTimeoutSec = game.ReadyTimeoutSec
        };

        var envelope = new
        {
            type = ProtocolConstants.TypeCommandLaunchGame,
            correlationId,
            pcId = agent.PcId,
            payload,
            ts = DateTime.UtcNow.ToString("O")
        };

        var json = JsonSerializer.Serialize(envelope, JsonUtil.Options);
        var buffer = Encoding.UTF8.GetBytes(json);

        agent.LastCommandId = correlationId;
        agent.LastCommandTs = DateTime.UtcNow;
        agent.LastStatus = "sent";
        agent.LastStatusMessage = $"Sent {game.Name}";
        StartTimeout(agent.PcId, correlationId);

        try
        {
            await connection.Socket.SendAsync(buffer, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            agent.LastStatus = "failed";
            agent.LastStatusMessage = ex.Message;
        }
    }

    private async Task SendLaunchExe(AgentInfo agent, string exePath)
    {
        var connection = await EnsureConnection(agent).ConfigureAwait(false);
        if (connection == null || connection.Socket.State != WebSocketState.Open)
        {
            agent.LastStatus = "ws_error";
            agent.LastStatusMessage = connection?.LastError ?? "Unable to connect";
            return;
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var payload = new LaunchExeCommand
        {
            ExePath = exePath
        };

        var envelope = new
        {
            type = ProtocolConstants.TypeCommandLaunchExe,
            correlationId,
            pcId = agent.PcId,
            payload,
            ts = DateTime.UtcNow.ToString("O")
        };

        var json = JsonSerializer.Serialize(envelope, JsonUtil.Options);
        var buffer = Encoding.UTF8.GetBytes(json);

        agent.LastCommandId = correlationId;
        agent.LastCommandTs = DateTime.UtcNow;
        agent.LastStatus = "sent";
        agent.LastStatusMessage = $"Sent {exePath}";
        StartTimeout(agent.PcId, correlationId);
        _logger.Info($"Sending LaunchExe to pcId={agent.PcId} ip={agent.Ip} ws={agent.WsPort} exe={exePath} corr={correlationId}");

        try
        {
            await connection.Socket.SendAsync(buffer, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            agent.LastStatus = "failed";
            agent.LastStatusMessage = ex.Message;
        }
    }

    private static string? TryLoadLocalAgentPcId()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DadBoard",
                "Agent",
                "agent.config.json");
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<AgentConfig>(json, JsonUtil.Options);
            return config?.PcId;
        }
        catch
        {
            return null;
        }
    }

    private static HashSet<string> GetLocalIps()
    {
        var ips = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "127.0.0.1"
        };

        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                {
                    ips.Add(ip.ToString());
                }
            }
        }
        catch
        {
        }

        return ips;
    }

    private void StartTimeout(string pcId, string correlationId)
    {
        var key = $"{pcId}:{correlationId}";
        var timer = new Timer(_ =>
        {
            if (_agents.TryGetValue(pcId, out var agent))
            {
                if (!string.Equals(agent.LastCommandId, correlationId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (agent.LastStatus != "running" && agent.LastStatus != "failed")
                {
                    agent.LastStatus = "timeout";
                    agent.LastStatusMessage = "Command timeout.";
                }
            }
        }, null, TimeSpan.FromSeconds(_config.CommandTimeoutSec), Timeout.InfiniteTimeSpan);

        _commandTimeouts[key] = timer;
    }

    private async Task<AgentConnection?> EnsureConnection(AgentInfo agent)
    {
        if (_connections.TryGetValue(agent.PcId, out var existing) && existing.Socket.State == WebSocketState.Open)
        {
            return existing;
        }

        var uri = new Uri($"ws://{agent.Ip}:{agent.WsPort}/ws/");
        var socket = new ClientWebSocket();
        var connection = new AgentConnection(agent.PcId, uri, socket);
        _connections[agent.PcId] = connection;

        try
        {
            await socket.ConnectAsync(uri, _cts.Token).ConfigureAwait(false);
            connection.State = "Open";
            connection.LastConnected = DateTime.UtcNow;
            _ = Task.Run(() => ReceiveLoop(connection));
            _logger.Info($"Connected to {agent.Name} at {uri}.");
            return connection;
        }
        catch (Exception ex)
        {
            connection.State = "Error";
            connection.LastError = ex.Message;
            _logger.Warn($"WebSocket connect failed for {agent.Name}: {ex.Message}");
            return connection;
        }
    }

    private async Task ReceiveLoop(AgentConnection connection)
    {
        var buffer = new byte[8192];
        while (connection.Socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
        {
            try
            {
                var result = await connection.Socket.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var envelope = JsonSerializer.Deserialize<MessageEnvelope>(json, JsonUtil.Options);
                if (envelope != null)
                {
                    connection.LastMessage = DateTime.UtcNow;
                    HandleEnvelope(envelope);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                connection.LastError = ex.Message;
                break;
            }
        }

        connection.State = "Closed";
    }

    private void HandleEnvelope(MessageEnvelope envelope)
    {
        if (!_agents.TryGetValue(envelope.PcId, out var agent))
        {
            return;
        }

        if (envelope.Type == ProtocolConstants.TypeAck)
        {
            var ack = envelope.Payload.Deserialize<AckPayload>(JsonUtil.Options);
            agent.LastAckOk = ack?.Ok ?? false;
            agent.LastAckError = ack?.ErrorMessage ?? "";
            agent.LastAckTs = DateTime.UtcNow;
            if (!agent.LastAckOk)
            {
                agent.LastStatus = "failed";
                agent.LastStatusMessage = ack?.ErrorMessage ?? "Ack failed";
            }
            return;
        }

        if (envelope.Type == ProtocolConstants.TypeStatus)
        {
            var status = envelope.Payload.Deserialize<StatusPayload>(JsonUtil.Options);
            if (status != null)
            {
                agent.LastStatus = status.State;
                agent.LastStatusMessage = status.Message ?? "";
            }
        }
    }

    private void PersistKnownAgents()
    {
        var records = _agents.Values.Select(a => new KnownAgentRecord
        {
            PcId = a.PcId,
            Name = a.Name,
            Ip = a.Ip,
            WsPort = a.WsPort,
            LastSeen = a.LastSeen.ToString("O"),
            Online = a.Online
        }).ToArray();

        _knownAgentsStore.Save(records);
    }

    private void PersistState()
    {
        var snapshot = new LeaderStateSnapshot
        {
            LastUpdated = DateTime.UtcNow.ToString("O"),
            Agents = GetAgentsSnapshot().ToArray(),
            Connections = _connections.Values.Select(c => new ConnectionInfo
            {
                PcId = c.PcId,
                Endpoint = c.Endpoint.ToString(),
                State = c.State,
                LastMessage = c.LastMessage == default ? "" : c.LastMessage.ToString("O"),
                LastError = c.LastError ?? ""
            }).ToArray()
        };

        _stateWriter.Write(snapshot);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
        _offlineTimer?.Dispose();
        _persistTimer?.Dispose();
        _stateTimer?.Dispose();
        foreach (var connection in _connections.Values)
        {
            connection.Dispose();
        }
    }
}

sealed class AgentConnection : IDisposable
{
    public string PcId { get; }
    public Uri Endpoint { get; }
    public ClientWebSocket Socket { get; }
    public string State { get; set; } = "Connecting";
    public DateTime LastConnected { get; set; }
    public DateTime LastMessage { get; set; }
    public string? LastError { get; set; }

    public AgentConnection(string pcId, Uri endpoint, ClientWebSocket socket)
    {
        PcId = pcId;
        Endpoint = endpoint;
        Socket = socket;
    }

    public void Dispose()
    {
        Socket.Dispose();
    }
}
