using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Principal;
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
    private readonly HttpClient _httpClient = new();
    private Timer? _updateTimer;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private UpdateState _updateState = new();

    private readonly ConcurrentDictionary<WebSocket, DateTime> _clients = new();
    private Timer? _helloTimer;
    private Timer? _stateTimer;
    private DateTime _lastHello;
    private AgentState _state = new();

    public event Action? ShutdownRequested;

    public AgentService(string? baseDir = null)
    {
        _baseDir = baseDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DadBoard");
        _agentDir = Path.Combine(_baseDir, "Agent");
        _logDir = Path.Combine(_baseDir, "logs");
        _statePath = Path.Combine(_agentDir, "agent_state.json");
        _configPath = ResolveConfigPath();

        Directory.CreateDirectory(_agentDir);
        Directory.CreateDirectory(_logDir);
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath) ?? _agentDir);

        _logger = new AgentLogger(Path.Combine(_logDir, "agent.log"));
        _stateWriter = new AgentStateWriter(_statePath);
    }

    public void Start()
    {
        try
        {
            _logger.Info($"Agent config path: {_configPath}");
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
            _updateState = UpdateStateStore.Load();
            if (string.IsNullOrWhiteSpace(_updateState.ManifestUrl))
            {
                _updateState.ManifestUrl = "http://127.0.0.1:39602/updates/latest.json";
            }
            _updateTimer = new Timer(_ => _ = CheckForUpdatesAsync(triggered: false), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));

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

    private static string ResolveConfigPath()
    {
        var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DadBoard", "Agent");
        return Path.Combine(baseDir, "agent.config.json");
    }

    private void SaveConfig(AgentConfig config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, JsonUtil.Options);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            var identity = WindowsIdentity.GetCurrent().Name;
            _logger.Error($"FATAL: Failed to write config at {_configPath} as {identity}: {ex.Message}");
            throw;
        }
    }

    private void StartWebSocketServer()
    {
        try
        {
            var builder = WebApplication.CreateBuilder();
            _logger.Info("WS backend: Kestrel");
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
                SendUpdateStatusToSocket(socket);
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

            _logger.Info($"Received LaunchGame corr={envelope.CorrelationId} gameId={command.GameId} url={command.LaunchUrl}");
            _state.LastCommandId = envelope.CorrelationId;
            _state.LastCommandType = envelope.Type;
            _state.LastCommandTs = DateTime.UtcNow.ToString("O");

            _ = Task.Run(() => ExecuteLaunchCommand(envelope.CorrelationId, command, socket));
            return;
        }

        if (envelope.Type == ProtocolConstants.TypeCommandLaunchExe)
        {
            var command = envelope.Payload.Deserialize<LaunchExeCommand>(JsonUtil.Options);
            if (command == null)
            {
                SendAck(socket, envelope.CorrelationId, ok: false, "Invalid payload");
                return;
            }

            _logger.Info($"Received LaunchExe corr={envelope.CorrelationId} exe={command.ExePath}");
            _state.LastCommandId = envelope.CorrelationId;
            _state.LastCommandType = envelope.Type;
            _state.LastCommandTs = DateTime.UtcNow.ToString("O");

            _ = Task.Run(() => ExecuteLaunchExe(envelope.CorrelationId, command, socket));
            return;
        }

        if (envelope.Type == ProtocolConstants.TypeCommandScanSteamGames)
        {
            _state.LastCommandId = envelope.CorrelationId;
            _state.LastCommandType = envelope.Type;
            _state.LastCommandTs = DateTime.UtcNow.ToString("O");

            _ = Task.Run(() => ExecuteScanSteamGames(envelope.CorrelationId, socket));
            return;
        }

        if (envelope.Type == ProtocolConstants.TypeCommandShutdownApp)
        {
            var command = envelope.Payload.Deserialize<ShutdownAppCommand>(JsonUtil.Options);
            if (command == null)
            {
                SendAck(socket, envelope.CorrelationId, ok: false, "Invalid payload");
                return;
            }

            _state.LastCommandId = envelope.CorrelationId;
            _state.LastCommandType = envelope.Type;
            _state.LastCommandTs = DateTime.UtcNow.ToString("O");

            _ = Task.Run(() => ExecuteShutdown(envelope.CorrelationId, command, socket));
            return;
        }

        if (envelope.Type == ProtocolConstants.TypeCommandUpdateSelf)
        {
            var command = envelope.Payload.Deserialize<UpdateSelfCommand>(JsonUtil.Options);
            if (command == null)
            {
                SendAck(socket, envelope.CorrelationId, ok: false, "Invalid payload");
                return;
            }

            _state.LastCommandId = envelope.CorrelationId;
            _state.LastCommandType = envelope.Type;
            _state.LastCommandTs = DateTime.UtcNow.ToString("O");

            _ = Task.Run(() => ExecuteUpdateSelf(envelope.CorrelationId, command, socket));
            return;
        }

        if (envelope.Type == ProtocolConstants.TypeCommandTriggerUpdateNow)
        {
            var command = envelope.Payload.Deserialize<TriggerUpdateNowCommand>(JsonUtil.Options);
            if (command == null)
            {
                SendAck(socket, envelope.CorrelationId, ok: false, "Invalid payload");
                return;
            }

            _state.LastCommandId = envelope.CorrelationId;
            _state.LastCommandType = envelope.Type;
            _state.LastCommandTs = DateTime.UtcNow.ToString("O");

            _ = Task.Run(() => ExecuteTriggerUpdateNow(envelope.CorrelationId, command, socket));
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
                _logger.Info($"LaunchGame running corr={correlationId} gameId={command.GameId}.");
            }
            else
            {
                SendStatus(correlationId, "failed", command.GameId, "TIMEOUT waiting for game process.");
                _logger.Warn($"LaunchGame timeout corr={correlationId} gameId={command.GameId}.");
            }
        }
        catch (Exception ex)
        {
            SendStatus(correlationId, "failed", command.GameId, ex.Message);
            _logger.Error($"LaunchGame failed corr={correlationId}: {ex.Message}");
        }
    }

    private Task ExecuteScanSteamGames(string correlationId, WebSocket socket)
    {
        try
        {
            var scan = SteamLibraryScanner.ScanInstalledGames();
            var inventory = new GameInventory
            {
                PcId = _config.PcId,
                MachineName = _config.DisplayName,
                Games = scan.Games,
                Ts = DateTime.UtcNow.ToString("O"),
                Error = scan.Error,
                SteamPath = scan.SteamPath,
                LibraryPaths = scan.LibraryPaths,
                ManifestCount = scan.ManifestCount
            };

            var libs = inventory.LibraryPaths?.Length ?? 0;
            _logger.Info($"ScanSteamGames steamPath={inventory.SteamPath ?? "none"} libs={libs} manifests={inventory.ManifestCount} games={inventory.Games.Length}");

            SendEnvelope(socket, ProtocolConstants.TypeSteamInventory, correlationId, inventory);
            _logger.Info($"ScanSteamGames sent {inventory.Games.Length} games corr={correlationId}.");

            if (!string.IsNullOrWhiteSpace(inventory.Error))
            {
                _logger.Warn($"ScanSteamGames error corr={correlationId}: {inventory.Error}");
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"ScanSteamGames failed corr={correlationId}: {ex.Message}");
            SendAck(socket, correlationId, ok: false, ex.Message);
            return Task.CompletedTask;
        }

        SendAck(socket, correlationId, ok: true, null);
        _logger.Info($"ScanSteamGames ack sent corr={correlationId}.");

        return Task.CompletedTask;
    }

    private async Task ExecuteLaunchExe(string correlationId, LaunchExeCommand command, WebSocket socket)
    {
        SendStatus(correlationId, "received", null, "Command received.");
        SendAck(socket, correlationId, ok: true, null);
        _logger.Info($"LaunchExe ack sent corr={correlationId}.");

        try
        {
            if (string.IsNullOrWhiteSpace(command.ExePath))
            {
                SendStatus(correlationId, "failed", null, "No exePath provided.");
                _logger.Warn($"LaunchExe failed corr={correlationId}: no exePath provided.");
                return;
            }

            SendStatus(correlationId, "launching", null, $"Launching {command.ExePath}.");
            var startInfo = new ProcessStartInfo(command.ExePath) { UseShellExecute = true };
            _logger.Info($"LaunchExe starting corr={correlationId} exe={startInfo.FileName} shell={startInfo.UseShellExecute}.");
            var process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException("Process start returned null.");
            }
            await Task.Delay(500, _cts.Token).ConfigureAwait(false);
            SendStatus(correlationId, "running", null, "Process started.");
            _logger.Info($"LaunchExe running corr={correlationId}.");
        }
        catch (Exception ex)
        {
            var message = $"LaunchExe failed: {ex.Message}";
            SendStatus(correlationId, "failed", null, message);
            _logger.Error($"LaunchExe failed corr={correlationId}: {ex.Message}");
        }
    }

    private async Task ExecuteUpdateSelf(string correlationId, UpdateSelfCommand command, WebSocket socket)
    {
        SendAck(socket, correlationId, ok: true, null);

        if (string.IsNullOrWhiteSpace(command.UpdateBaseUrl))
        {
            SendUpdateStatus("failed", "Missing update base URL.");
            _logger.Warn($"UpdateSelf failed corr={correlationId}: missing update base URL.");
            return;
        }

        var manifestUrl = $"{command.UpdateBaseUrl.TrimEnd('/')}/updates/latest.json";
        _updateState.ManifestUrl = manifestUrl;
        UpdateStateStore.Save(_updateState);
        await RunSetupUpdateAsync(manifestUrl).ConfigureAwait(false);
    }

    private async Task ExecuteTriggerUpdateNow(string correlationId, TriggerUpdateNowCommand command, WebSocket socket)
    {
        SendAck(socket, correlationId, ok: true, null);

        if (string.IsNullOrWhiteSpace(command.ManifestUrl))
        {
            SendUpdateStatus("failed", "Missing manifest URL.");
            _logger.Warn($"TriggerUpdateNow failed corr={correlationId}: missing manifest URL.");
            return;
        }

        _updateState.ManifestUrl = command.ManifestUrl;
        UpdateStateStore.Save(_updateState);

        await CheckForUpdatesAsync(triggered: true).ConfigureAwait(false);
    }

    private async Task CheckForUpdatesAsync(bool triggered)
    {
        if (!await _updateLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var manifestUrl = _updateState.ManifestUrl;
            if (string.IsNullOrWhiteSpace(manifestUrl))
            {
                if (triggered)
                {
                    SendUpdateStatus("failed", "No manifest URL configured.");
                }
                return;
            }

            _updateState.LastCheckedUtc = DateTime.UtcNow.ToString("O");
            UpdateStateStore.Save(_updateState);

            UpdateManifest? manifest = null;
            try
            {
                var json = await _httpClient.GetStringAsync(manifestUrl).ConfigureAwait(false);
                manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonUtil.Options);
            }
            catch (Exception ex)
            {
                _updateState.LastResult = "Manifest fetch failed";
                _updateState.LastError = ex.Message;
                UpdateStateStore.Save(_updateState);
                if (triggered)
                {
                    SendUpdateStatus("failed", $"Manifest fetch failed: {ex.Message}");
                }
                _logger.Warn($"Update check failed: {ex.Message}");
                return;
            }

            if (manifest == null || string.IsNullOrWhiteSpace(manifest.LatestVersion) || string.IsNullOrWhiteSpace(manifest.PackageUrl))
            {
                _updateState.LastResult = "Manifest invalid";
                _updateState.LastError = "Missing latest_version or package_url.";
                UpdateStateStore.Save(_updateState);
                if (triggered)
                {
                    SendUpdateStatus("failed", "Manifest missing required fields.");
                }
                _logger.Warn("Update manifest missing required fields.");
                return;
            }

            manifest.LatestVersion = VersionUtil.Normalize(manifest.LatestVersion);
            _updateState.LatestVersion = manifest.LatestVersion;
            _updateState.ForceCheckToken = manifest.ForceCheckToken;
            UpdateStateStore.Save(_updateState);

            var installed = VersionUtil.GetCurrentVersion();
            var minSupported = string.IsNullOrWhiteSpace(manifest.MinSupportedVersion)
                ? null
                : VersionUtil.Normalize(manifest.MinSupportedVersion);

            var updateAvailable = VersionUtil.Compare(manifest.LatestVersion, installed) > 0;
            var minRequired = minSupported != null && VersionUtil.Compare(installed, minSupported) < 0;
            if (!updateAvailable && !minRequired)
            {
                _updateState.LastResult = "Up to date";
                _updateState.LastError = "";
                UpdateStateStore.Save(_updateState);
                if (triggered)
                {
                    SendUpdateStatus("idle", "Up to date.");
                }
                return;
            }

            _logger.Info($"Update required: installed={installed} latest={manifest.LatestVersion} min={minSupported ?? "n/a"}");
            await RunSetupUpdateAsync(manifestUrl).ConfigureAwait(false);
        }
        finally
        {
            _updateLock.Release();
        }
    }

    private async Task RunSetupUpdateAsync(string manifestUrl)
    {
        var setupPath = Path.Combine(DadBoardPaths.InstallDir, "DadBoardSetup.exe");
        if (!File.Exists(setupPath))
        {
            var message = $"DadBoardSetup.exe not found at {setupPath}.";
            SendUpdateStatus("failed", message);
            _logger.Error(message);
            return;
        }

        try
        {
            SendUpdateStatus("downloading", "Downloading update.");
            var args = $"/update --silent --manifest \"{manifestUrl}\"";
            _logger.Info($"Starting updater: \"{setupPath}\" {args}");
            var process = Process.Start(new ProcessStartInfo(setupPath, args)
            {
                UseShellExecute = true,
                WorkingDirectory = DadBoardPaths.InstallDir
            });
            if (process == null)
            {
                SendUpdateStatus("failed", "Failed to start updater.");
                return;
            }

            SendUpdateStatus("installing", "Applying update.");
            await process.WaitForExitAsync(_cts.Token).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                SendUpdateStatus("failed", $"Updater exit code {process.ExitCode}");
                _logger.Error($"Updater failed with exit code {process.ExitCode}");
                return;
            }

            SendUpdateStatus("restarting", "Restarting.");
            ShutdownRequested?.Invoke();
        }
        catch (Exception ex)
        {
            SendUpdateStatus("failed", ex.Message);
            _logger.Error($"Updater failed: {ex}");
        }
    }

    private async Task DownloadFileAsync(string url, string path)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(file).ConfigureAwait(false);
    }

    private bool LaunchBootstrapperForUpdate(string newExePath)
    {
        var bootstrapper = DadBoardPaths.InstalledExePath;
        if (!File.Exists(bootstrapper))
        {
            _logger.Error($"UpdateSelf bootstrapper not found at {bootstrapper}.");
            return false;
        }

        var args = $"--apply-update \"{newExePath}\" --wait-pid {Process.GetCurrentProcess().Id} --mode agent --minimized";
        try
        {
            var startInfo = new ProcessStartInfo(bootstrapper, args)
            {
                UseShellExecute = true
            };
            var process = Process.Start(startInfo);
            if (process == null)
            {
                _logger.Error("UpdateSelf failed to start bootstrapper process.");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"UpdateSelf failed to launch bootstrapper: {ex.Message}");
            return false;
        }

        return true;
    }

    private void SendUpdateStatus(string status, string? message)
    {
        _state.UpdateStatus = status;
        _state.UpdateMessage = message ?? "";
        var payload = new UpdateStatusPayload
        {
            Status = status,
            Message = message
        };

        foreach (var socket in _clients.Keys)
        {
            if (socket.State != WebSocketState.Open)
            {
                continue;
            }

            SendEnvelope(socket, ProtocolConstants.TypeUpdateStatus, "", payload);
        }
    }

    private void SendUpdateStatusToSocket(WebSocket socket)
    {
        var payload = new UpdateStatusPayload
        {
            Status = _state.UpdateStatus,
            Message = _state.UpdateMessage
        };
        SendEnvelope(socket, ProtocolConstants.TypeUpdateStatus, "", payload);
    }

    private Task ExecuteShutdown(string correlationId, ShutdownAppCommand command, WebSocket socket)
    {
        var reason = string.IsNullOrWhiteSpace(command.Reason) ? "Leader requested shutdown." : command.Reason;
        _logger.Info($"Received ShutdownApp corr={correlationId} reason={reason}");
        SendStatus(correlationId, "received", null, reason);
        SendAck(socket, correlationId, ok: true, null);
        SendStatus(correlationId, "stopping", null, "Shutting down.");
        _logger.Info($"ShutdownApp ack sent corr={correlationId}.");

        try
        {
            ShutdownRequested?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error($"ShutdownApp handler failed corr={correlationId}: {ex.Message}");
        }

        return Task.CompletedTask;
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
            Version = VersionUtil.GetCurrentVersion(),
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
        _updateTimer?.Dispose();
        _httpClient.Dispose();
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

    private static string GetAppVersion()
    {
        return VersionUtil.GetCurrentVersion();
    }
}
