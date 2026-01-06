using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DadBoard.Spine.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace DadBoard.Agent;

public sealed class AgentService : IDisposable
{
    private readonly string _baseDir;
    private readonly string _agentDir;
    private readonly string _logDir;
    private readonly string _statePath;
    private readonly string _configPath;
    private readonly AgentLogger _logger;
    private readonly AgentStateWriter _stateWriter;

    private AgentConfig _config = new();
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;
    private WebApplication? _webApp;
    private Task? _webAppTask;

    private readonly ConcurrentDictionary<WebSocket, DateTime> _clients = new();
    private Timer? _helloTimer;
    private Timer? _stateTimer;
    private DateTime _lastHello;
    private AgentState _state = new();

    public AgentService()
    {
        _baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DadBoard");
        _agentDir = Path.Combine(_baseDir, "Agent");
        _logDir = Path.Combine(_baseDir, "logs");
        _statePath = Path.Combine(_agentDir, "agent_state.json");
        _configPath = Path.Combine(_agentDir, "agent.config.json");

        Directory.CreateDirectory(_agentDir);
        Directory.CreateDirectory(_logDir);

        _logger = new AgentLogger(Path.Combine(_logDir, "agent.log"));
        _stateWriter = new AgentStateWriter(_statePath);
    }

    public void Start()
    {
        try
        {
            _config = LoadConfig();
            _state = new AgentState
            {
                PcId = _config.PcId,
                DisplayName = _config.DisplayName
            };

            _udp = new UdpClient(AddressFamily.InterNetwork)
            {
                EnableBroadcast = true
            };

            StartWebSocketServer();

            _helloTimer = new Timer(_ => SendHello(), null, 0, _config.HelloIntervalMs);
            _stateTimer = new Timer(_ => PersistState(), null, 0, 1000);

            _logger.Info("Agent started.");
        }
        catch (Exception ex)
        {
            _logger.Error($"Agent failed to start: {ex}");
            throw;
        }
    }

    public void Stop()
    {
        _logger.Info("Agent stopping.");
        Dispose();
    }

    private AgentConfig LoadConfig()
    {
        if (!File.Exists(_configPath))
        {
            var config = new AgentConfig
            {
                PcId = Guid.NewGuid().ToString("N"),
                DisplayName = Environment.MachineName
            };
            SaveConfig(config);
            return config;
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<AgentConfig>(json, JsonUtil.Options);
            if (config == null)
            {
                throw new InvalidOperationException("Invalid agent config.");
            }

            if (string.IsNullOrWhiteSpace(config.PcId))
            {
                config.PcId = Guid.NewGuid().ToString("N");
            }
            if (string.IsNullOrWhiteSpace(config.DisplayName))
            {
                config.DisplayName = Environment.MachineName;
            }

            SaveConfig(config);
            return config;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to load config: {ex.Message}");
            throw;
        }
    }

    private void SaveConfig(AgentConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonUtil.Options);
        File.WriteAllText(_configPath, json);
    }

    private void StartWebSocketServer()
    {
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls($"http://0.0.0.0:{_config.WsPort}");

            var app = builder.Build();
            app.UseWebSockets();

            app.Map("/ws", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                _clients[socket] = DateTime.UtcNow;
                _logger.Info("Leader connected via WebSocket.");
                await ReceiveLoop(socket).ConfigureAwait(false);
            });

            _webApp = app;
            _webAppTask = app.RunAsync(_cts.Token);
            _logger.Info($"Kestrel WebSocket server listening on port {_config.WsPort}.");
        }
        catch (Exception ex)
        {
            _logger.Error($"WebSocket listener failed: {ex.Message}");
            throw;
        }
    }

    private async Task ReceiveLoop(WebSocket socket)
    {
        var buffer = new byte[8192];
        while (socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                var payload = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var envelope = JsonSerializer.Deserialize<MessageEnvelope>(payload, JsonUtil.Options);
                if (envelope != null)
                {
                    _state.LastWsMessageTs = DateTime.UtcNow.ToString("O");
                    HandleEnvelope(envelope, socket);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warn($"WebSocket receive error: {ex.Message}");
                break;
            }
        }

        _clients.TryRemove(socket, out _);
        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void HandleEnvelope(MessageEnvelope envelope, WebSocket socket)
    {
        if (envelope.Type == ProtocolConstants.TypeCommandLaunchGame)
        {
            var command = envelope.Payload.Deserialize<LaunchGameCommand>(JsonUtil.Options);
            if (command == null)
            {
                SendAck(socket, envelope.CorrelationId, ok: false, "Invalid payload");
                return;
            }

            _state.LastCommandId = envelope.CorrelationId;
            _state.LastCommandType = envelope.Type;
            _state.LastCommandTs = DateTime.UtcNow.ToString("O");

            _ = Task.Run(() => ExecuteLaunchCommand(envelope.CorrelationId, command, socket));
            return;
        }

        SendAck(socket, envelope.CorrelationId, ok: false, "Unknown command");
    }

    private async Task ExecuteLaunchCommand(string correlationId, LaunchGameCommand command, WebSocket socket)
    {
        SendStatus(correlationId, "received", command.GameId, "Command received.");
        SendAck(socket, correlationId, ok: true, null);

        try
        {
            SendStatus(correlationId, "launching", command.GameId, "Launching game.");

            if (!string.IsNullOrWhiteSpace(command.LaunchUrl))
            {
                Process.Start(new ProcessStartInfo(command.LaunchUrl) { UseShellExecute = true });
            }
            else if (!string.IsNullOrWhiteSpace(command.ExePath))
            {
                Process.Start(new ProcessStartInfo(command.ExePath) { UseShellExecute = true });
            }
            else
            {
                SendStatus(correlationId, "failed", command.GameId, "No launch target provided.");
                return;
            }

            bool ready = await WaitForProcess(command.ProcessNames, command.ReadyTimeoutSec).ConfigureAwait(false);
            if (ready)
            {
                SendStatus(correlationId, "running", command.GameId, "Game running.");
            }
            else
            {
                SendStatus(correlationId, "failed", command.GameId, "TIMEOUT waiting for game process.");
            }
        }
        catch (Exception ex)
        {
            SendStatus(correlationId, "failed", command.GameId, ex.Message);
        }
    }

    private async Task<bool> WaitForProcess(string[]? processNames, int timeoutSec)
    {
        if (processNames == null || processNames.Length == 0)
        {
            await Task.Delay(2000, _cts.Token).ConfigureAwait(false);
            return true;
        }

        var timeout = TimeSpan.FromSeconds(timeoutSec <= 0 ? 120 : timeoutSec);
        var start = DateTime.UtcNow;

        while (DateTime.UtcNow - start < timeout && !_cts.IsCancellationRequested)
        {
            if (IsAnyProcessRunning(processNames))
            {
                return true;
            }

            await Task.Delay(500, _cts.Token).ConfigureAwait(false);
        }

        return false;
    }

    private static bool IsAnyProcessRunning(string[] processNames)
    {
        foreach (var name in processNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var shortName = Path.GetFileNameWithoutExtension(name);
            if (Process.GetProcessesByName(shortName).Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    private void SendAck(WebSocket socket, string correlationId, bool ok, string? error)
    {
        var ack = new AckPayload { Ok = ok, ErrorMessage = error };
        SendEnvelope(socket, ProtocolConstants.TypeAck, correlationId, ack);
    }

    private void SendStatus(string correlationId, string state, string? gameId, string? message)
    {
        var status = new StatusPayload
        {
            State = state,
            GameId = gameId,
            Message = message
        };
        _state.LastCommandState = state;
        BroadcastEnvelope(ProtocolConstants.TypeStatus, correlationId, status);
    }

    private void SendEnvelope(WebSocket socket, string type, string correlationId, object payload)
    {
        var envelope = new
        {
            type,
            correlationId,
            pcId = _config.PcId,
            payload,
            ts = DateTime.UtcNow.ToString("O")
        };

        var json = JsonSerializer.Serialize(envelope, JsonUtil.Options);
        var buffer = Encoding.UTF8.GetBytes(json);
        _ = socket.SendAsync(buffer, WebSocketMessageType.Text, true, _cts.Token);
    }

    private void BroadcastEnvelope(string type, string correlationId, object payload)
    {
        foreach (var socket in _clients.Keys)
        {
            if (socket.State != WebSocketState.Open)
            {
                continue;
            }

            SendEnvelope(socket, type, correlationId, payload);
        }
    }

    private void SendHello()
    {
        if (_udp == null)
        {
            return;
        }

        var hello = new AgentHello
        {
            PcId = _config.PcId,
            Name = _config.DisplayName,
            Ip = GetLocalIp(),
            WsPort = _config.WsPort,
            Version = _config.Version,
            Ts = DateTime.UtcNow.ToString("O")
        };

        var json = JsonSerializer.Serialize(hello, JsonUtil.Options);
        var data = Encoding.UTF8.GetBytes(json);

        _udp.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, _config.UdpPort));
        _lastHello = DateTime.UtcNow;
        _state.LastHelloTs = _lastHello.ToString("O");
    }

    private static string GetLocalIp()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                {
                    return ip.ToString();
                }
            }
        }
        catch
        {
        }

        return "127.0.0.1";
    }

    private void PersistState()
    {
        _state.WsClientCount = _clients.Count;
        _stateWriter.Write(_state);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _helloTimer?.Dispose();
        _stateTimer?.Dispose();
        if (_webApp != null)
        {
            try
            {
                _webApp.StopAsync().GetAwaiter().GetResult();
            }
            catch
            {
            }
        }
        _udp?.Dispose();
    }
}
