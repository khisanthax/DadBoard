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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace DadBoard.Leader;

public sealed class LeaderService : IDisposable
{
    private readonly string _baseDir;
    private readonly string _leaderDir;
    private readonly string _logDir;
    private readonly string _configPath;
    private readonly string _knownAgentsPath;
    private readonly string _statePath;
    private readonly string _leaderDataDir;
    private readonly string _leaderInventoryPath;
    private readonly string _agentInventoriesPath;

    private readonly LeaderLogger _logger;
    private readonly KnownAgentsStore _knownAgentsStore;
    private readonly LeaderStateWriter _stateWriter;
    private readonly string? _localAgentPcId;
    private readonly HashSet<string> _localIps;

    private LeaderConfig _config = new();

    private readonly ConcurrentDictionary<string, AgentInfo> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AgentConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Timer> _commandTimeouts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, GameInventory> _agentInventories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _agentInventoryErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _inventoryLock = new();
    private List<SteamGameEntry> _leaderCatalog = new();
    private readonly Dictionary<int, SteamGameEntry> _leaderCatalogMap = new();
    private DateTime _leaderCatalogTs;
    private readonly ConcurrentDictionary<string, DateTime> _lastInventoryScan = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastInventoryRefresh;

    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;
    private Timer? _offlineTimer;
    private Timer? _persistTimer;
    private Timer? _stateTimer;
    private Timer? _reconnectTimer;
    private readonly ConcurrentDictionary<string, DateTime> _lastReconnectAttempt = new(StringComparer.OrdinalIgnoreCase);
    private WebApplication? _updateHost;
    private Task? _updateHostTask;

    public event Action? InventoriesUpdated;

    public LeaderService(string? baseDir = null)
    {
        _baseDir = baseDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DadBoard");
        _leaderDir = Path.Combine(_baseDir, "Leader");
        _logDir = Path.Combine(_baseDir, "logs");
        _configPath = Path.Combine(_leaderDir, "leader.config.json");
        _knownAgentsPath = Path.Combine(_baseDir, "known_agents.json");
        _statePath = Path.Combine(_leaderDir, "leader_state.json");
        _leaderDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DadBoard", "Leader");
        _leaderInventoryPath = Path.Combine(_leaderDataDir, "leader_inventory.json");
        _agentInventoriesPath = Path.Combine(_leaderDataDir, "agent_inventories.json");

        Directory.CreateDirectory(_leaderDir);
        Directory.CreateDirectory(_logDir);
        Directory.CreateDirectory(_leaderDataDir);

        _logger = new LeaderLogger(Path.Combine(_logDir, "leader.log"));
        _knownAgentsStore = new KnownAgentsStore(_knownAgentsPath);
        _stateWriter = new LeaderStateWriter(_statePath);
        _localAgentPcId = TryLoadLocalAgentPcId();
        _localIps = GetLocalIps();

        _config = LoadConfig();
        LoadInventoryCaches();

        StartDiscovery();
        StartUpdateHost();
        _offlineTimer = new Timer(_ => UpdateOnlineStates(), null, 0, 1000);
        _persistTimer = new Timer(_ => PersistKnownAgents(), null, 0, 5000);
        _stateTimer = new Timer(_ => PersistState(), null, 0, 1000);
        _reconnectTimer = new Timer(_ => TryReconnectOnlineAgents(), null, 0, 2000);

        _ = Task.Run(RefreshSteamInventory);
    }

    public LeaderConfig Config => _config;

    public IReadOnlyList<GameDefinition> GetGames() => _config.Games;

    public IReadOnlyList<SteamGameEntry> GetLeaderCatalog()
    {
        lock (_inventoryLock)
        {
            return _leaderCatalog.ToList();
        }
    }

    public DateTime GetLastInventoryRefreshUtc() => _lastInventoryRefresh;

    public IReadOnlyDictionary<string, GameInventory> GetAgentInventoriesSnapshot()
    {
        return _agentInventories.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public IReadOnlyDictionary<string, string> GetAgentInventoryErrorsSnapshot()
    {
        return _agentInventoryErrors.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public void RefreshSteamInventory()
    {
        var scan = SteamLibraryScanner.ScanInstalledGames();
        lock (_inventoryLock)
        {
            _leaderCatalogMap.Clear();
            foreach (var entry in scan.Games)
            {
                if (entry.AppId <= 0)
                {
                    continue;
                }

                _leaderCatalogMap[entry.AppId] = new SteamGameEntry
                {
                    AppId = entry.AppId,
                    Name = entry.Name
                };
            }

            foreach (var inventory in _agentInventories.Values)
            {
                MergeInventoryIntoCatalog(inventory);
            }

            _leaderCatalog = _leaderCatalogMap.Values
                .OrderBy(g => g.Name ?? $"App {g.AppId}", StringComparer.OrdinalIgnoreCase)
                .ToList();
            _leaderCatalogTs = DateTime.UtcNow;
        }
        _lastInventoryRefresh = DateTime.UtcNow;
        SaveLeaderInventoryCache();
        InventoriesUpdated?.Invoke();

        foreach (var agent in _agents.Values)
        {
            if (!agent.Online)
            {
                continue;
            }

            _ = Task.Run(() => SendInventoryScan(agent));
        }
    }

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
            Version = a.Version,
            LastStatus = a.LastStatus,
            LastStatusMessage = a.LastStatusMessage,
            LastCommandId = a.LastCommandId,
            LastCommandTs = a.LastCommandTs,
            LastAckOk = a.LastAckOk,
            LastAckError = a.LastAckError,
            LastAckTs = a.LastAckTs,
            LastResult = a.LastResult,
            LastError = a.LastError,
            UpdateStatus = a.UpdateStatus,
            UpdateMessage = a.UpdateMessage
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

    public bool LaunchOnAgent(GameDefinition game, string pcId, out string? error)
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

        _ = Task.Run(() => SendLaunchCommand(agent, game));
        return true;
    }

    public async Task<(bool Ok, string? Error)> LaunchAppIdOnAgentAsync(int appId, string pcId)
    {
        if (!_agents.TryGetValue(pcId, out var agent))
        {
            return (false, "Agent not found.");
        }

        if (!agent.Online)
        {
            return (false, "Agent is offline.");
        }

        try
        {
            return await SendLaunchAppId(agent, appId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            agent.LastStatus = "failed";
            agent.LastStatusMessage = ex.Message;
            agent.LastResult = "Failed";
            agent.LastError = ex.Message;
            _logger.Error($"LaunchGame failed for {agent.Name}: {ex}");
            return (false, ex.Message);
        }
    }

    public void LaunchAppIdOnAgents(int appId, IEnumerable<string> pcIds)
    {
        foreach (var pcId in pcIds)
        {
            if (_agents.TryGetValue(pcId, out var agent) && agent.Online)
            {
                _ = Task.Run(async () => await SendLaunchAppId(agent, appId).ConfigureAwait(false));
            }
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

    public bool SendShutdownCommand(string pcId, out string? error)
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

        _ = Task.Run(() => SendShutdown(agent));
        return true;
    }

    public bool SendUpdateCommand(string pcId, out string? error)
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

        _ = Task.Run(() => SendUpdateSelf(agent));
        return true;
    }

    public void SendUpdateAllOnline()
    {
        foreach (var agent in _agents.Values)
        {
            if (!agent.Online)
            {
                continue;
            }

            _ = Task.Run(() => SendUpdateSelf(agent));
        }
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

    private void StartUpdateHost()
    {
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls($"http://0.0.0.0:{_config.UpdatePort}");
            var app = builder.Build();

            app.MapGet("/updates/version.json", () =>
            {
                if (!TryGetUpdateFileInfo(out var runtimePath, out var version, out var sha, out var error))
                {
                    _logger.Warn($"Update host version.json error: {error}");
                    return Results.Problem(error, statusCode: StatusCodes.Status404NotFound);
                }

                return Results.Json(new UpdateVersionInfo
                {
                    Version = version,
                    Sha256 = sha
                });
            });

            app.MapGet("/updates/DadBoard.exe", () =>
            {
                if (!TryGetUpdateFileInfo(out var runtimePath, out _, out _, out var error))
                {
                    _logger.Warn($"Update host DadBoard.exe error: {error}");
                    return Results.Problem(error, statusCode: StatusCodes.Status404NotFound);
                }

                return Results.File(runtimePath, "application/octet-stream", "DadBoard.exe");
            });

            _updateHost = app;
            _updateHostTask = app.RunAsync(_cts.Token);
            _logger.Info($"Update host listening on port {_config.UpdatePort}.");
        }
        catch (Exception ex)
        {
            _logger.Error($"Update host failed to start: {ex.Message}");
        }
    }

    private static bool TryGetUpdateFileInfo(out string runtimePath, out string version, out string sha256, out string error)
    {
        runtimePath = DadBoardPaths.RuntimeExePath;
        version = "0.0.0";
        sha256 = "";
        error = "";

        if (!File.Exists(runtimePath))
        {
            runtimePath = DadBoardPaths.InstalledExePath;
            if (!File.Exists(runtimePath))
            {
                error = "DadBoard.exe not found for update.";
                return false;
            }
        }

        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(runtimePath);
            version = VersionUtil.Normalize(info.FileVersion ?? "0.0.0");
            sha256 = HashUtil.ComputeSha256(runtimePath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
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
        agent.Version = VersionUtil.Normalize(hello.Version ?? "");
        agent.LastSeen = DateTime.UtcNow;
        agent.Online = true;

        if (!_agentInventories.ContainsKey(agent.PcId))
        {
            var lastScan = _lastInventoryScan.GetOrAdd(agent.PcId, _ => DateTime.MinValue);
            if ((DateTime.UtcNow - lastScan).TotalSeconds > 30)
            {
                _lastInventoryScan[agent.PcId] = DateTime.UtcNow;
                _ = Task.Run(() => SendInventoryScan(agent));
            }
        }
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

    private void TryReconnectOnlineAgents()
    {
        foreach (var agent in _agents.Values)
        {
            if (!agent.Online)
            {
                continue;
            }

            if (_connections.TryGetValue(agent.PcId, out var connection) &&
                connection.Socket.State == WebSocketState.Open)
            {
                continue;
            }

            var lastAttempt = _lastReconnectAttempt.GetOrAdd(agent.PcId, _ => DateTime.MinValue);
            if ((DateTime.UtcNow - lastAttempt).TotalSeconds < 2)
            {
                continue;
            }

            _lastReconnectAttempt[agent.PcId] = DateTime.UtcNow;
            _ = Task.Run(() => EnsureConnection(agent));
        }
    }

    private async Task SendLaunchCommand(AgentInfo agent, GameDefinition game)
    {
        var connection = await EnsureConnection(agent).ConfigureAwait(false);
        if (connection == null || connection.Socket.State != WebSocketState.Open)
        {
            agent.LastStatus = "ws_error";
            agent.LastStatusMessage = connection?.LastError ?? "Unable to connect";
            agent.LastResult = "Failed";
            agent.LastError = agent.LastStatusMessage;
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
        agent.LastStatus = "launching";
        agent.LastStatusMessage = $"Launching {game.Name}";
        agent.LastResult = "Pending";
        agent.LastError = "";
        StartTimeout(agent.PcId, correlationId);
        _logger.Info($"Launch requested gameId={game.Id} name={game.Name} pcId={agent.PcId} ip={agent.Ip} ws={agent.WsPort} corr={correlationId}");

        try
        {
            await connection.Socket.SendAsync(buffer, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            agent.LastStatus = "failed";
            agent.LastStatusMessage = ex.Message;
            agent.LastResult = "Failed";
            agent.LastError = ex.Message;
            _logger.Error($"LaunchGame send failed for {agent.Name}: {ex}");
        }
    }

    private async Task<(bool Ok, string? Error)> SendLaunchAppId(AgentInfo agent, int appId)
    {
        var connection = await EnsureConnection(agent).ConfigureAwait(false);
        if (connection == null || connection.Socket.State != WebSocketState.Open)
        {
            agent.LastStatus = "ws_error";
            agent.LastStatusMessage = connection?.LastError ?? "Unable to connect";
            agent.LastResult = "Failed";
            agent.LastError = agent.LastStatusMessage;
            _logger.Warn($"LaunchGame failed for {agent.Name}: {agent.LastStatusMessage}");
            return (false, agent.LastStatusMessage);
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var payload = new LaunchGameCommand
        {
            GameId = appId.ToString(),
            LaunchUrl = $"steam://run/{appId}",
            ReadyTimeoutSec = 120
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
        agent.LastStatus = "launching";
        agent.LastStatusMessage = $"Launching app {appId}";
        agent.LastResult = "Pending";
        agent.LastError = "";
        StartTimeout(agent.PcId, correlationId);
        _logger.Info($"Launch requested appId={appId} pcId={agent.PcId} ip={agent.Ip} ws={agent.WsPort} corr={correlationId}");

        try
        {
            await connection.Socket.SendAsync(buffer, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
            return (true, null);
        }
        catch (Exception ex)
        {
            agent.LastStatus = "failed";
            agent.LastStatusMessage = ex.Message;
            agent.LastResult = "Failed";
            agent.LastError = ex.Message;
            _logger.Error($"LaunchGame send failed for {agent.Name}: {ex}");
            return (false, ex.Message);
        }
    }

    public void LogLaunchRequest(int appId, string gameName, IEnumerable<string> pcIds)
    {
        var targets = string.Join(",", pcIds);
        var gameId = appId.ToString();
        _logger.Info($"Launch requested gameId={gameId} name={gameName} appId={appId} targets=[{targets}]");
    }

    private async Task SendLaunchExe(AgentInfo agent, string exePath)
    {
        var connection = await EnsureConnection(agent).ConfigureAwait(false);
        if (connection == null || connection.Socket.State != WebSocketState.Open)
        {
            agent.LastStatus = "ws_error";
            agent.LastStatusMessage = connection?.LastError ?? "Unable to connect";
            agent.LastResult = "Failed";
            agent.LastError = agent.LastStatusMessage;
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
        agent.LastResult = "Pending";
        agent.LastError = "";
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
            agent.LastResult = "Failed";
            agent.LastError = ex.Message;
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

    private string GetUpdateBaseUrl(AgentInfo agent)
    {
        var port = _config.UpdatePort;
        var agentIp = agent.Ip ?? "";
        var match = _localIps.FirstOrDefault(ip => IsSameSubnet(ip, agentIp));
        if (string.IsNullOrWhiteSpace(match))
        {
            match = _localIps.FirstOrDefault(ip => !string.Equals(ip, "127.0.0.1", StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrWhiteSpace(match))
        {
            match = "127.0.0.1";
        }

        return $"http://{match}:{port}";
    }

    private static bool IsSameSubnet(string ipA, string ipB)
    {
        if (!IPAddress.TryParse(ipA, out var a) || !IPAddress.TryParse(ipB, out var b))
        {
            return false;
        }

        if (a.AddressFamily != AddressFamily.InterNetwork || b.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytesA = a.GetAddressBytes();
        var bytesB = b.GetAddressBytes();
        return bytesA[0] == bytesB[0] && bytesA[1] == bytesB[1] && bytesA[2] == bytesB[2];
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
                    var noAck = agent.LastAckTs == default || agent.LastAckTs < agent.LastCommandTs;
                    agent.LastStatusMessage = noAck ? "No response from agent." : "Command timeout.";
                    agent.LastResult = "Timed out";
                    agent.LastError = agent.LastStatusMessage;
                    _logger.Warn($"Command timeout pcId={pcId} corr={correlationId} noAck={noAck}");
                }
            }
        }, null, TimeSpan.FromSeconds(_config.CommandTimeoutSec), Timeout.InfiniteTimeSpan);

        _commandTimeouts[key] = timer;
    }

    private async Task SendShutdown(AgentInfo agent)
    {
        var connection = await EnsureConnection(agent).ConfigureAwait(false);
        if (connection == null || connection.Socket.State != WebSocketState.Open)
        {
            agent.LastStatus = "ws_error";
            agent.LastStatusMessage = connection?.LastError ?? "Unable to connect";
            agent.LastResult = "Failed";
            agent.LastError = agent.LastStatusMessage;
            return;
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var payload = new ShutdownAppCommand
        {
            Reason = "Leader requested shutdown."
        };

        var envelope = new
        {
            type = ProtocolConstants.TypeCommandShutdownApp,
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
        agent.LastStatusMessage = "Sent shutdown command";
        agent.LastResult = "Pending";
        agent.LastError = "";
        StartTimeout(agent.PcId, correlationId);
        _logger.Info($"Sending ShutdownApp to pcId={agent.PcId} ip={agent.Ip} ws={agent.WsPort} corr={correlationId}");

        try
        {
            await connection.Socket.SendAsync(buffer, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            agent.LastStatus = "failed";
            agent.LastStatusMessage = ex.Message;
            agent.LastResult = "Failed";
            agent.LastError = ex.Message;
        }
    }

    private async Task SendInventoryScan(AgentInfo agent)
    {
        var connection = await EnsureConnection(agent).ConfigureAwait(false);
        if (connection == null || connection.Socket.State != WebSocketState.Open)
        {
            _logger.Warn($"Inventory scan skipped for {agent.Name}: {connection?.LastError ?? "Unable to connect"}");
            return;
        }

        var correlationId = Guid.NewGuid().ToString("N");
        _lastInventoryScan[agent.PcId] = DateTime.UtcNow;
        var envelope = new
        {
            type = ProtocolConstants.TypeCommandScanSteamGames,
            correlationId,
            pcId = agent.PcId,
            payload = new ScanSteamGamesCommand(),
            ts = DateTime.UtcNow.ToString("O")
        };

        var json = JsonSerializer.Serialize(envelope, JsonUtil.Options);
        var buffer = Encoding.UTF8.GetBytes(json);

        try
        {
            await connection.Socket.SendAsync(buffer, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
            _logger.Info($"Sent ScanSteamGames to pcId={agent.PcId} ip={agent.Ip} ws={agent.WsPort} corr={correlationId}");
        }
        catch (Exception ex)
        {
            _logger.Warn($"ScanSteamGames send failed for {agent.Name}: {ex.Message}");
        }
    }

    private async Task SendUpdateSelf(AgentInfo agent)
    {
        var connection = await EnsureConnection(agent).ConfigureAwait(false);
        if (connection == null || connection.Socket.State != WebSocketState.Open)
        {
            agent.UpdateStatus = "failed";
            agent.UpdateMessage = connection?.LastError ?? "Unable to connect";
            _logger.Warn($"Update command skipped for {agent.Name}: {agent.UpdateMessage}");
            return;
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var baseUrl = GetUpdateBaseUrl(agent);
        var payload = new UpdateSelfCommand
        {
            UpdateBaseUrl = baseUrl
        };

        var envelope = new
        {
            type = ProtocolConstants.TypeCommandUpdateSelf,
            correlationId,
            pcId = agent.PcId,
            payload,
            ts = DateTime.UtcNow.ToString("O")
        };

        var json = JsonSerializer.Serialize(envelope, JsonUtil.Options);
        var buffer = Encoding.UTF8.GetBytes(json);

        agent.UpdateStatus = "sent";
        agent.UpdateMessage = $"Update requested via {baseUrl}";
        _logger.Info($"Sending UpdateSelf to pcId={agent.PcId} ip={agent.Ip} ws={agent.WsPort} base={baseUrl} corr={correlationId}");

        try
        {
            await connection.Socket.SendAsync(buffer, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            agent.UpdateStatus = "failed";
            agent.UpdateMessage = ex.Message;
            _logger.Warn($"UpdateSelf send failed for {agent.Name}: {ex.Message}");
        }
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

        if (envelope.Type == ProtocolConstants.TypeSteamInventory)
        {
            var inventory = envelope.Payload.Deserialize<GameInventory>(JsonUtil.Options);
            if (inventory != null)
            {
                if (string.IsNullOrWhiteSpace(inventory.PcId))
                {
                    inventory.PcId = envelope.PcId;
                }

                if (string.IsNullOrWhiteSpace(inventory.Ts))
                {
                    inventory.Ts = DateTime.UtcNow.ToString("O");
                }

                _agentInventories[inventory.PcId] = inventory;
                if (!string.IsNullOrWhiteSpace(inventory.Error))
                {
                    _agentInventoryErrors[inventory.PcId] = inventory.Error!;
                    _logger.Warn($"Inventory scan error for {inventory.PcId}: {inventory.Error}");
                }
                else
                {
                    _agentInventoryErrors.TryRemove(inventory.PcId, out _);
                }

                lock (_inventoryLock)
                {
                    MergeInventoryIntoCatalog(inventory);
                    _leaderCatalog = _leaderCatalogMap.Values
                        .OrderBy(g => g.Name ?? $"App {g.AppId}", StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                SaveAgentInventoriesCache();
                InventoriesUpdated?.Invoke();
            }

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
                agent.LastResult = "Failed";
                agent.LastError = agent.LastStatusMessage;
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
                ApplyResult(agent, status.State, status.Message);
            }
            return;
        }

        if (envelope.Type == ProtocolConstants.TypeUpdateStatus)
        {
            var status = envelope.Payload.Deserialize<UpdateStatusPayload>(JsonUtil.Options);
            if (status != null)
            {
                agent.UpdateStatus = status.Status ?? "";
                agent.UpdateMessage = status.Message ?? "";
            }
        }
    }

    private static void ApplyResult(AgentInfo agent, string state, string? message)
    {
        if (string.Equals(state, "running", StringComparison.OrdinalIgnoreCase))
        {
            agent.LastResult = "Success";
            agent.LastError = "";
            return;
        }

        if (string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase))
        {
            agent.LastResult = "Failed";
            agent.LastError = message ?? "";
            return;
        }

        if (string.Equals(state, "timeout", StringComparison.OrdinalIgnoreCase))
        {
            agent.LastResult = "Timed out";
            agent.LastError = message ?? "Command timeout.";
        }
    }

    private void MergeInventoryIntoCatalog(GameInventory inventory)
    {
        foreach (var game in inventory.Games)
        {
            if (game.AppId <= 0)
            {
                continue;
            }

            if (_leaderCatalogMap.TryGetValue(game.AppId, out var existing))
            {
                if (string.IsNullOrWhiteSpace(existing.Name) && !string.IsNullOrWhiteSpace(game.Name))
                {
                    existing.Name = game.Name;
                }
            }
        }
    }

    private void LoadInventoryCaches()
    {
        try
        {
            if (File.Exists(_leaderInventoryPath))
            {
                var json = File.ReadAllText(_leaderInventoryPath);
                var cache = JsonSerializer.Deserialize<LeaderInventoryCacheFile>(json, JsonUtil.Options);
                if (cache != null)
                {
                    _leaderCatalog = cache.Games.ToList();
                    _leaderCatalogMap.Clear();
                    foreach (var entry in _leaderCatalog)
                    {
                        if (entry.AppId > 0)
                        {
                            _leaderCatalogMap[entry.AppId] = entry;
                        }
                    }
                    _leaderCatalogTs = DateTime.TryParse(cache.Ts, out var ts) ? ts : DateTime.MinValue;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to load leader inventory cache: {ex.Message}");
        }

        try
        {
            if (File.Exists(_agentInventoriesPath))
            {
                var json = File.ReadAllText(_agentInventoriesPath);
                var cache = JsonSerializer.Deserialize<AgentInventoriesCacheFile>(json, JsonUtil.Options);
                if (cache != null)
                {
                    foreach (var inventory in cache.Inventories)
                    {
                        if (!string.IsNullOrWhiteSpace(inventory.PcId))
                        {
                            _agentInventories[inventory.PcId] = inventory;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to load agent inventories cache: {ex.Message}");
        }
    }

    private void SaveLeaderInventoryCache()
    {
        try
        {
            var cache = new LeaderInventoryCacheFile
            {
                Ts = _leaderCatalogTs == default ? DateTime.UtcNow.ToString("O") : _leaderCatalogTs.ToString("O"),
                Games = _leaderCatalog.ToArray()
            };
            var json = JsonSerializer.Serialize(cache, JsonUtil.Options);
            File.WriteAllText(_leaderInventoryPath, json);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to save leader inventory cache: {ex.Message}");
        }
    }

    private void SaveAgentInventoriesCache()
    {
        try
        {
            var cache = new AgentInventoriesCacheFile
            {
                Inventories = _agentInventories.Values.ToArray()
            };
            var json = JsonSerializer.Serialize(cache, JsonUtil.Options);
            File.WriteAllText(_agentInventoriesPath, json);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to save agent inventories cache: {ex.Message}");
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
        _reconnectTimer?.Dispose();
        if (_updateHost != null)
        {
            try
            {
                _updateHost.StopAsync().GetAwaiter().GetResult();
            }
            catch
            {
            }
        }
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
