using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
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
    private readonly string _launchSessionsPath;
    private readonly string _configPath;
    private readonly AgentLogger _logger;
    private readonly AgentStateWriter _stateWriter;

    private AgentConfig _config = new();
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;
    private WebApplication? _webApp;
    private Task? _webAppTask;
    private readonly HttpClient _httpClient = new();

    private readonly ConcurrentDictionary<WebSocket, DateTime> _clients = new();
    private Timer? _helloTimer;
    private Timer? _stateTimer;
    private DateTime _lastHello;
    private AgentState _state = new();
    private Timer? _updateTimer;
    private readonly object _updateLock = new();
    private UpdateState _updateState = new();
    private UpdateConfig _updateConfig = new();
    private bool _updateConfigLoaded;
    private bool _lastUpdateCanceled;
    private string _updateConfigError = "";
    private readonly TimeSpan _updateConfigTimeout = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _manifestTimeout = TimeSpan.FromSeconds(5);
    private Dictionary<int, LaunchSession> _launchSessions = new();
    private const int QuitGraceSecondsDefault = 10;

    public event Action? ShutdownRequested;

    private const int WM_CLOSE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public AgentService(string? baseDir = null)
    {
        _baseDir = baseDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DadBoard");
        _agentDir = Path.Combine(_baseDir, "Agent");
        _logDir = Path.Combine(_baseDir, "logs");
        _statePath = Path.Combine(_agentDir, "agent_state.json");
        _launchSessionsPath = Path.Combine(_agentDir, "launch_sessions.json");
        _configPath = ResolveConfigPath();

        Directory.CreateDirectory(_agentDir);
        Directory.CreateDirectory(_logDir);
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath) ?? _agentDir);

        _logger = new AgentLogger(Path.Combine(_logDir, "agent.log"));
        _stateWriter = new AgentStateWriter(_statePath);
        _httpClient.Timeout = _manifestTimeout;
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
            _updateState = UpdateStateStore.Load();
            _launchSessions = LaunchSessionStore.Load(_launchSessionsPath, message => _logger.Warn(message));
            ApplySetupResultIfPresent("startup");
            _ = Task.Run(InitializeUpdateConfigAsync);

            _udp = new UdpClient(AddressFamily.InterNetwork)
            {
                EnableBroadcast = true
            };

            StartWebSocketServer();

            _helloTimer = new Timer(_ => SendHello(), null, 0, _config.HelloIntervalMs);
            _stateTimer = new Timer(_ => PersistState(), null, 0, 1000);
            StartUpdatePolling();

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

    private void StartUpdatePolling()
    {
        _updateTimer = new Timer(_ =>
        {
            _ = Task.Run(() => CheckForUpdatesAsync(force: false, manifestOverride: null));
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));
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

        if (envelope.Type == ProtocolConstants.TypeCommandRestartSteam)
        {
            var command = envelope.Payload.Deserialize<RestartSteamCommand>(JsonUtil.Options);
            if (command == null)
            {
                SendAck(socket, envelope.CorrelationId, ok: false, "Invalid payload");
                return;
            }

            _logger.Info($"Received RestartSteam corr={envelope.CorrelationId} forceLogin={command.ForceLogin}");
            _state.LastCommandId = envelope.CorrelationId;
            _state.LastCommandType = envelope.Type;
            _state.LastCommandTs = DateTime.UtcNow.ToString("O");

            _ = Task.Run(() => ExecuteRestartSteam(envelope.CorrelationId, command, socket));
            return;
        }

        if (envelope.Type == ProtocolConstants.TypeCommandQuitGame)
        {
            var command = envelope.Payload.Deserialize<QuitGameCommand>(JsonUtil.Options);
            if (command == null)
            {
                SendAck(socket, envelope.CorrelationId, ok: false, "Invalid payload");
                return;
            }

            _logger.Info($"Received QuitGame corr={envelope.CorrelationId} appId={command.AppId}");
            _state.LastCommandId = envelope.CorrelationId;
            _state.LastCommandType = envelope.Type;
            _state.LastCommandTs = DateTime.UtcNow.ToString("O");

            _ = Task.Run(() => ExecuteQuitGame(envelope.CorrelationId, command, socket));
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

            _ = Task.Run(async () =>
            {
                SendAck(socket, envelope.CorrelationId, ok: true, null);
                SendUpdateStatus("starting_update", "Update requested.");
                _logger.Info("Update requested via TriggerUpdateNow command.");
                await CheckForUpdatesAsync(force: true, manifestOverride: command.ManifestUrl).ConfigureAwait(false);
            });
            return;
        }

        if (envelope.Type == ProtocolConstants.TypeCommandRunSetupUpdate)
        {
            var command = envelope.Payload.Deserialize<RunSetupUpdateCommand>(JsonUtil.Options);
            if (command == null)
            {
                SendAck(socket, envelope.CorrelationId, ok: false, "Invalid payload");
                return;
            }

            _state.LastCommandId = envelope.CorrelationId;
            _state.LastCommandType = envelope.Type;
            _state.LastCommandTs = DateTime.UtcNow.ToString("O");

            _ = Task.Run(async () =>
            {
                SendAck(socket, envelope.CorrelationId, ok: true, null);
                SendUpdateStatus("starting_update", "Update requested.");
                _logger.Info("Update requested via RunSetupUpdate command.");
                await CheckForUpdatesAsync(force: true, manifestOverride: command.ManifestUrl).ConfigureAwait(false);
            });
            return;
        }

        if (envelope.Type == ProtocolConstants.TypeCommandResetUpdateFailures)
        {
            var command = envelope.Payload.Deserialize<ResetUpdateFailuresCommand>(JsonUtil.Options);
            _state.LastCommandId = envelope.CorrelationId;
            _state.LastCommandType = envelope.Type;
            _state.LastCommandTs = DateTime.UtcNow.ToString("O");

            _ = Task.Run(() =>
            {
                SendAck(socket, envelope.CorrelationId, ok: true, null);
                ResetUpdateFailures(command?.Initiator ?? "leader");
            });
            return;
        }

        if (envelope.Type == ProtocolConstants.TypeUpdateSource)
        {
            var payload = envelope.Payload.Deserialize<UpdateSourcePayload>(JsonUtil.Options);
            if (payload == null)
            {
                SendAck(socket, envelope.CorrelationId, ok: false, "Invalid payload");
                return;
            }

            _updateState.ManifestUrl = payload.PrimaryManifestUrl ?? "";
            _updateState.FallbackManifestUrl = payload.FallbackManifestUrl ?? "";
            UpdateStateStore.Save(_updateState);
            _logger.Info($"Update source set. primary={_updateState.ManifestUrl} fallback={_updateState.FallbackManifestUrl}");
            SendAck(socket, envelope.CorrelationId, ok: true, null);
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
            var hasAppId = TryParseAppId(command.GameId, out var appId);
            var installDir = hasAppId && SteamLibraryScanner.TryGetInstallDir(appId, out var foundDir)
                ? foundDir
                : null;
            var beforeSnapshot = hasAppId ? SnapshotProcessStarts() : new Dictionary<int, DateTime>();

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
                SendStatus(correlationId, "failed", command.GameId, "No launch target provided.", "config_invalid");
                return;
            }

            if (hasAppId)
            {
                var resolvedPid = await ResolveGamePidAsync(appId, installDir, command.ProcessNames, beforeSnapshot, _cts.Token)
                    .ConfigureAwait(false);
                RecordLaunchSession(appId, command.GameId, installDir, resolvedPid, command.LaunchUrl, command.ExePath);
            }

            bool ready = await WaitForProcess(command.ProcessNames, command.ReadyTimeoutSec).ConfigureAwait(false);
            if (ready)
            {
                SendStatus(correlationId, "running", command.GameId, "Game running.");
                _logger.Info($"LaunchGame running corr={correlationId} gameId={command.GameId}.");
            }
            else
            {
                SendStatus(correlationId, "timeout", command.GameId, "Process not detected before timeout.", "process_not_detected");
                _logger.Warn($"LaunchGame timeout corr={correlationId} gameId={command.GameId}.");
            }
        }
        catch (Win32Exception ex)
        {
            var errorClass = !string.IsNullOrWhiteSpace(command.LaunchUrl) && string.IsNullOrWhiteSpace(command.ExePath)
                ? "steam_uri_failed"
                : MapLaunchErrorClass(ex);
            SendStatus(correlationId, "failed", command.GameId, ex.Message, errorClass);
            _logger.Error($"LaunchGame failed corr={correlationId}: {ex.Message}");
        }
        catch (Exception ex)
        {
            SendStatus(correlationId, "failed", command.GameId, ex.Message, "launch_failed");
            _logger.Error($"LaunchGame failed corr={correlationId}: {ex.Message}");
        }
    }

    private async Task ExecuteQuitGame(string correlationId, QuitGameCommand command, WebSocket socket)
    {
        SendStatus(correlationId, "quit_starting", command.AppId.ToString(), "Stopping game.");
        SendAck(socket, correlationId, ok: true, null);

        try
        {
            if (!_launchSessions.TryGetValue(command.AppId, out var session) || session.RootPid is not int pid)
            {
                SendStatus(correlationId, "quit_failed", command.AppId.ToString(),
                    "No tracked launch session for this game on this PC.", "no_session");
                return;
            }

            Process? process = null;
            try
            {
                process = Process.GetProcessById(pid);
            }
            catch (Exception)
            {
                _launchSessions.Remove(command.AppId);
                SaveLaunchSessions();
                SendStatus(correlationId, "quit_failed", command.AppId.ToString(),
                    "Tracked process is no longer running.", "not_running");
                return;
            }

            SendStatus(correlationId, "quit_graceful_sent", command.AppId.ToString(), "Requesting graceful close.");
            TryCloseProcessWindow(process);

            var graceSeconds = command.GraceSeconds > 0 ? command.GraceSeconds : QuitGraceSecondsDefault;
            var stopped = await WaitForExitAsync(process, TimeSpan.FromSeconds(graceSeconds)).ConfigureAwait(false);

            if (!stopped)
            {
                SendStatus(correlationId, "quit_force_killing", command.AppId.ToString(), "Force killing process.");
                var killed = ForceKillProcessTree(process.Id);
                _logger.Info($"QuitGame force kill pid={pid} killed={killed}");
                stopped = await WaitForExitAsync(process, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }

            if (stopped)
            {
                _launchSessions.Remove(command.AppId);
                SaveLaunchSessions();
                SendStatus(correlationId, "quit_completed", command.AppId.ToString(), "Game stopped.");
                return;
            }

            SendStatus(correlationId, "quit_failed", command.AppId.ToString(), "Game process did not exit.", "quit_failed");
        }
        catch (Exception ex)
        {
            SendStatus(correlationId, "quit_failed", command.AppId.ToString(), ex.Message, "quit_failed");
            _logger.Error($"QuitGame failed corr={correlationId}: {ex.Message}");
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
                SendStatus(correlationId, "failed", null, "No exePath provided.", "config_invalid");
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
        catch (Win32Exception ex)
        {
            var errorClass = MapLaunchErrorClass(ex);
            var message = $"LaunchExe failed: {ex.Message}";
            SendStatus(correlationId, "failed", null, message, errorClass);
            _logger.Error($"LaunchExe failed corr={correlationId}: {ex.Message}");
        }
        catch (Exception ex)
        {
            var message = $"LaunchExe failed: {ex.Message}";
            SendStatus(correlationId, "failed", null, message, "launch_failed");
            _logger.Error($"LaunchExe failed corr={correlationId}: {ex.Message}");
        }
    }

    private async Task ExecuteRestartSteam(string correlationId, RestartSteamCommand command, WebSocket socket)
    {
        SendStatus(correlationId, "steam_restart_starting", null, "Restarting Steam.");
        SendAck(socket, correlationId, ok: true, null);

        try
        {
            TryLaunchSteamExit();
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            var killed = KillSteamProcesses();
            _logger.Info($"RestartSteam killedProcesses={killed}");

            var steamExe = ResolveSteamExe();
            if (string.IsNullOrWhiteSpace(steamExe) || !File.Exists(steamExe))
            {
                SendStatus(correlationId, "steam_restart_failed", null, "Steam.exe not found.", "file_not_found");
                return;
            }

            Process.Start(new ProcessStartInfo(steamExe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(steamExe) ?? ""
            });

            SendStatus(correlationId, "steam_restart_completed", null, "Steam restarted.");
            _logger.Info($"RestartSteam completed corr={correlationId} exe={steamExe}");
        }
        catch (Exception ex)
        {
            SendStatus(correlationId, "steam_restart_failed", null, ex.Message, "restart_failed");
            _logger.Error($"RestartSteam failed corr={correlationId}: {ex.Message}");
        }
    }

    private static bool TryParseAppId(string? gameId, out int appId)
    {
        appId = 0;
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return false;
        }

        return int.TryParse(gameId, out appId);
    }

    private static Dictionary<int, DateTime> SnapshotProcessStarts()
    {
        var snapshot = new Dictionary<int, DateTime>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                snapshot[process.Id] = process.StartTime;
            }
            catch
            {
                snapshot[process.Id] = DateTime.MinValue;
            }
            finally
            {
                process.Dispose();
            }
        }

        return snapshot;
    }

    private async Task<int?> ResolveGamePidAsync(
        int appId,
        string? installDir,
        string[]? processNames,
        Dictionary<int, DateTime> before,
        CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var normalizedNames = NormalizeProcessNames(processNames);

        while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (before.ContainsKey(process.Id))
                    {
                        continue;
                    }

                    var path = TryGetProcessPath(process);
                    if (!string.IsNullOrWhiteSpace(installDir) && !string.IsNullOrWhiteSpace(path) &&
                        path.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.Info($"LaunchGame resolved pid={process.Id} appId={appId} path={path}");
                        return process.Id;
                    }

                    if (normalizedNames.Count > 0 &&
                        normalizedNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                    {
                        _logger.Info($"LaunchGame resolved pid={process.Id} appId={appId} name={process.ProcessName}");
                        return process.Id;
                    }
                }
                catch
                {
                    // ignore access issues
                }
                finally
                {
                    process.Dispose();
                }
            }

            await Task.Delay(1000, token).ConfigureAwait(false);
        }

        _logger.Warn($"LaunchGame PID unresolved appId={appId}");
        return null;
    }

    private static HashSet<string> NormalizeProcessNames(string[]? processNames)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (processNames == null)
        {
            return names;
        }

        foreach (var name in processNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var trimmed = name.Trim();
            var normalized = Path.GetFileNameWithoutExtension(trimmed);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                names.Add(normalized);
            }
        }

        return names;
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private void RecordLaunchSession(
        int appId,
        string? gameId,
        string? installDir,
        int? pid,
        string? launchUrl,
        string? exePath)
    {
        var session = new LaunchSession
        {
            AppId = appId,
            RootPid = pid,
            GameName = gameId,
            InstallDir = installDir,
            ExePath = exePath,
            LaunchMethod = string.IsNullOrWhiteSpace(launchUrl) ? "exe" : "steam",
            StartedAtUtc = DateTime.UtcNow.ToString("O"),
            LastSeenRunningUtc = pid.HasValue ? DateTime.UtcNow.ToString("O") : null,
            ResolvedPid = pid.HasValue
        };

        _launchSessions[appId] = session;
        SaveLaunchSessions();
        _logger.Info($"LaunchGame session recorded appId={appId} pid={(pid?.ToString() ?? "none")} installDir={installDir ?? "none"}");
    }

    private void SaveLaunchSessions()
    {
        LaunchSessionStore.Save(_launchSessionsPath, _launchSessions, message => _logger.Warn(message));
    }

    private static void TryCloseProcessWindow(Process process)
    {
        try
        {
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                process.CloseMainWindow();
                SendMessage(process.MainWindowHandle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        try
        {
            return await Task.Run(() => process.WaitForExit((int)timeout.TotalMilliseconds)).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private bool ForceKillProcessTree(int pid)
    {
        try
        {
            var startInfo = new ProcessStartInfo("taskkill", $"/PID {pid} /T /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var taskkill = Process.Start(startInfo);
            if (taskkill != null)
            {
                taskkill.WaitForExit(5000);
                return taskkill.ExitCode == 0;
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.Kill(true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryLaunchSteamExit()
    {
        try
        {
            Process.Start(new ProcessStartInfo("steam://exit") { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private int KillSteamProcesses()
    {
        var names = new[] { "steam", "steamwebhelper", "steamservice" };
        var killed = 0;

        foreach (var name in names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.CloseMainWindow();
                    }
                }
                catch
                {
                }
            }
        }

        Thread.Sleep(1000);

        foreach (var name in names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                        killed++;
                    }
                }
                catch
                {
                }
            }
        }

        return killed;
    }

    private static string? ResolveSteamExe()
    {
        var steamPath = SteamLibraryScanner.GetSteamPath();
        if (string.IsNullOrWhiteSpace(steamPath))
        {
            return null;
        }

        return Path.Combine(steamPath, "steam.exe");
    }

    private async Task CheckForUpdatesAsync(bool force, string? manifestOverride)
    {
        _lastUpdateCanceled = false;
        if (_updateState.UpdatesDisabled && !force)
        {
            _logger.Warn("Updates disabled due to repeated failures.");
            return;
        }
        if (_updateState.UpdatesDisabled && force)
        {
            var message = "Updates disabled due to repeated failures.";
            EnsureUpdateVersionsForFailure();
            _updateState.LastErrorCode = "updates_disabled";
            _updateState.LastError = message;
            UpdateStateStore.Save(_updateState);
            SendUpdateStatus("failed", message);
            _logger.Warn(message);
            return;
        }

        lock (_updateLock)
        {
            if (_state.UpdateStatus is "downloading" or "installing" or "restarting")
            {
                return;
            }
        }

        var primaryUrl = ResolveManifestUrl(manifestOverride);
        var fallbackUrl = GetFallbackManifestUrl(primaryUrl);
        if (string.IsNullOrWhiteSpace(primaryUrl) && string.IsNullOrWhiteSpace(fallbackUrl))
        {
            var message = "Update source not configured.";
            EnsureUpdateVersionsForFailure();
            _updateState.LastErrorCode = "update_source_missing";
            _updateState.LastError = message;
            UpdateStateStore.Save(_updateState);
            SendUpdateStatus("failed", message);
            _logger.Warn(message);
            return;
        }

        try
        {
            _logger.Info($"Update init: resolving manifest primary={primaryUrl} fallback={fallbackUrl}");
            var (manifest, error, usedUrl) = await TryLoadManifestWithFallback(primaryUrl, fallbackUrl).ConfigureAwait(false);
            if (manifest == null)
            {
                var message = error ?? "Manifest unavailable.";
                EnsureUpdateVersionsForFailure();
                _updateState.LastErrorCode = "manifest_unavailable";
                _updateState.LastError = message;
                RegisterUpdateFailure(message);
                SendUpdateStatus("failed", message);
                return;
            }

            var latest = VersionUtil.Normalize(manifest.LatestVersion);
            var current = VersionUtil.GetCurrentVersion();
            _updateState.LastVersionBefore = current;
            _updateState.LastVersionAfter = "";
            _updateState.LastErrorCode = "";
            var tokenChanged = manifest.ForceCheckToken != _updateState.ForceCheckToken;
            var versionNewer = VersionUtil.Compare(latest, current) > 0;

            if (!string.IsNullOrWhiteSpace(primaryUrl))
            {
                _updateState.ManifestUrl = primaryUrl;
            }
            _updateState.ForceCheckToken = manifest.ForceCheckToken;
            _updateState.LastCheckedUtc = DateTime.UtcNow.ToString("O");
            _updateState.LatestVersion = latest;
            _updateState.LastError = "";
            _updateState.ConsecutiveFailures = 0;
            _updateState.UpdatesDisabled = false;
            UpdateStateStore.Save(_updateState);

            if (!force && !tokenChanged && !versionNewer)
            {
                _updateState.LastResult = "up-to-date";
                UpdateStateStore.Save(_updateState);
                return;
            }

            SendUpdateStatus("starting", "Starting update.");
            _logger.Info($"Update check starting. current={current} latest={latest} tokenChanged={tokenChanged}");

            var updateUrl = usedUrl ?? primaryUrl;
            var ok = await RunUpdaterAsync(updateUrl, fallbackUrl).ConfigureAwait(false);
            if (!ok && _lastUpdateCanceled)
            {
                _logger.Warn("Update canceled by user; not retrying or tripping breaker.");
                return;
            }

            if (!ok && !string.IsNullOrWhiteSpace(fallbackUrl) &&
                !string.Equals(updateUrl, fallbackUrl, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warn($"Update failed via primary manifest; retrying fallback {fallbackUrl}");
                SendUpdateStatus("downloading", "Primary update failed; retrying fallback.");
                ok = await RunUpdaterAsync(fallbackUrl, "").ConfigureAwait(false);
                if (ok)
                {
                    _updateState.ManifestUrl = fallbackUrl;
                    UpdateStateStore.Save(_updateState);
                }
            }

            if (!ok)
            {
                if (_lastUpdateCanceled)
                {
                    _logger.Warn("Update canceled by user; not tripping breaker.");
                    return;
                }

                RegisterUpdateFailure("Updater failed.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_updateState.LastResult))
            {
                _updateState.LastResult = "updated";
            }
            UpdateStateStore.Save(_updateState);
        }
        catch (Exception ex)
        {
            RegisterUpdateFailure(ex.ToString());
            SendUpdateStatus("failed", ex.Message);
            _logger.Error($"Update check failed: {ex}");
        }
    }

    private async Task<(UpdateManifest? Manifest, string? Error, string? UsedUrl)> TryLoadManifestWithFallback(string primaryUrl, string fallbackUrl)
    {
        if (!string.IsNullOrWhiteSpace(primaryUrl))
        {
            var (manifest, error) = await TryLoadManifestAsync(primaryUrl).ConfigureAwait(false);
            if (manifest != null)
            {
                return (manifest, null, primaryUrl);
            }

            _logger.Warn($"Update init: primary manifest failed: {error}");
        }

        if (!string.IsNullOrWhiteSpace(fallbackUrl) &&
            !string.Equals(primaryUrl, fallbackUrl, StringComparison.OrdinalIgnoreCase))
        {
            var (manifest, error) = await TryLoadManifestAsync(fallbackUrl).ConfigureAwait(false);
            if (manifest != null)
            {
                _logger.Info("Update init: fallback manifest succeeded.");
                _updateState.ManifestUrl = fallbackUrl;
                return (manifest, null, fallbackUrl);
            }

            return (null, error, null);
        }

        return (null, "Manifest unavailable.", null);
    }

    private async Task<(UpdateManifest? Manifest, string? Error)> TryLoadManifestAsync(string manifestUrl)
    {
        if (File.Exists(manifestUrl))
        {
            _logger.Info($"Update init: reading manifest from file {manifestUrl}");
            var json = await ReadFileWithTimeoutAsync(manifestUrl, _manifestTimeout).ConfigureAwait(false);
            if (json == null)
            {
                _logger.Warn("Update init: manifest read timed out.");
                return (null, "Manifest read timed out.");
            }
            return (JsonSerializer.Deserialize<UpdateManifest>(json, JsonUtil.Options), null);
        }

        if (Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            _logger.Info($"Update init: reading manifest from file {uri.LocalPath}");
            var json = await ReadFileWithTimeoutAsync(uri.LocalPath, _manifestTimeout).ConfigureAwait(false);
            if (json == null)
            {
                _logger.Warn("Update init: manifest read timed out.");
                return (null, "Manifest read timed out.");
            }
            return (JsonSerializer.Deserialize<UpdateManifest>(json, JsonUtil.Options), null);
        }

        _logger.Info($"Update init: reading manifest via http {manifestUrl}");
        try
        {
            var response = await _httpClient.GetAsync(manifestUrl, _cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return (JsonSerializer.Deserialize<UpdateManifest>(content, JsonUtil.Options), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private string ResolveManifestUrl(string? overrideUrl)
    {
        if (!string.IsNullOrWhiteSpace(overrideUrl))
        {
            _updateState.ManifestUrl = overrideUrl;
            UpdateStateStore.Save(_updateState);
            return overrideUrl;
        }

        if (!string.IsNullOrWhiteSpace(_updateState.ManifestUrl))
        {
            return _updateState.ManifestUrl;
        }

        if (_updateConfigLoaded)
        {
            return UpdateConfigStore.ResolveManifestUrl(_updateConfig);
        }

        return UpdateConfigStore.GetDefaultManifestUrl(UpdateChannel.Nightly);
    }

    private string GetFallbackManifestUrl(string primaryUrl)
    {
        if (!string.IsNullOrWhiteSpace(_updateState.FallbackManifestUrl) &&
            !string.Equals(_updateState.FallbackManifestUrl, primaryUrl, StringComparison.OrdinalIgnoreCase))
        {
            return _updateState.FallbackManifestUrl;
        }

        if (_updateConfigLoaded)
        {
            var fallback = UpdateConfigStore.ResolveManifestUrl(_updateConfig);
            if (!string.Equals(fallback, primaryUrl, StringComparison.OrdinalIgnoreCase))
            {
                return fallback;
            }
        }

        return "";
    }

    private async Task<bool> RunUpdaterAsync(string manifestUrl, string fallbackManifestUrl)
    {
        var updaterExe = DadBoardPaths.UpdaterExePath;
        if (!File.Exists(updaterExe))
        {
            _logger.Warn($"Updater missing: {updaterExe}. Attempting download.");
            SendUpdateStatus("downloading", "Downloading updater.");
            var downloaded = await EnsureUpdaterPresentAsync(manifestUrl, fallbackManifestUrl).ConfigureAwait(false);
            if (!downloaded || !File.Exists(updaterExe))
            {
                EnsureUpdateVersionsForFailure();
                _updateState.LastErrorCode = "updater_missing";
                _updateState.LastError = $"Updater not found: {updaterExe}";
                UpdateStateStore.Save(_updateState);
                SendUpdateStatus("failed", $"Updater not found: {updaterExe}");
                _logger.Warn($"Update skipped: {updaterExe} missing after download attempt.");
                return false;
            }
        }

        SendUpdateStatus("downloading", "Starting updater.");
        _logger.Info($"Launching updater: {updaterExe} --silent --manifest \"{manifestUrl}\"");

        var updaterDir = Path.GetDirectoryName(updaterExe) ?? DadBoardPaths.InstallDir;
        Directory.CreateDirectory(updaterDir);
        var startInfo = new ProcessStartInfo
        {
            FileName = updaterExe,
            Arguments = $"--silent --manifest \"{manifestUrl}\"",
            UseShellExecute = true,
            WorkingDirectory = updaterDir
        };

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _lastUpdateCanceled = true;
            var message = "Update canceled by user (UAC/consent).";
            EnsureUpdateVersionsForFailure();
            _updateState.LastResult = "canceled";
            _updateState.LastError = message;
            _updateState.LastErrorCode = "updater_canceled";
            UpdateStateStore.Save(_updateState);
            SendUpdateStatus("failed", message);
            _logger.Warn($"Updater launch canceled by user: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            EnsureUpdateVersionsForFailure();
            _updateState.LastErrorCode = "updater_launch_failed";
            _updateState.LastError = ex.Message;
            UpdateStateStore.Save(_updateState);
            SendUpdateStatus("failed", $"Failed to launch updater: {ex.Message}");
            _logger.Error($"Updater start failed: {ex}");
            return false;
        }
        if (process == null)
        {
            EnsureUpdateVersionsForFailure();
            _updateState.LastErrorCode = "updater_launch_failed";
            _updateState.LastError = "Failed to launch updater.";
            UpdateStateStore.Save(_updateState);
            SendUpdateStatus("failed", "Failed to launch updater.");
            _logger.Warn("Updater process failed to start.");
            return false;
        }

        SendUpdateStatus("installing", "Updater running.");
        await process.WaitForExitAsync(_cts.Token).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            EnsureUpdateVersionsForFailure();
            _updateState.LastResult = "failed";
            _updateState.LastError = $"Updater exited with code {process.ExitCode}.";
            _updateState.LastErrorCode = $"updater_exit_{process.ExitCode}";
            UpdateStateStore.Save(_updateState);
            SendUpdateStatus("failed", _updateState.LastError);
            _logger.Warn($"Updater exited with {process.ExitCode}.");
            return false;
        }

        var updaterStatus = UpdaterStatusStore.Load();
        if (updaterStatus != null && !updaterStatus.Success)
        {
            EnsureUpdateVersionsForFailure();
            _updateState.LastResult = "failed";
            _updateState.LastError = updaterStatus.ErrorMessage;
            _updateState.LastErrorCode = string.IsNullOrWhiteSpace(updaterStatus.ErrorCode) ? "update_failed" : updaterStatus.ErrorCode;
            UpdateStateStore.Save(_updateState);
            SendUpdateStatus("failed", string.IsNullOrWhiteSpace(_updateState.LastError) ? "Update failed." : _updateState.LastError);
            _logger.Warn($"Updater status failure: {_updateState.LastErrorCode} {_updateState.LastError}");
            return false;
        }

        if (updaterStatus != null)
        {
            var invoked = (updaterStatus.Action ?? "").Trim().Equals("invoked_setup", StringComparison.OrdinalIgnoreCase) ||
                          (updaterStatus.Result ?? "").Trim().Equals("installing", StringComparison.OrdinalIgnoreCase);
            if (invoked)
            {
                _updateState.LastResult = "installing";
                _updateState.LastError = "";
                _updateState.LastErrorCode = "";
                UpdateStateStore.Save(_updateState);
                SendUpdateStatus("restarting", "Setup launched; waiting for completion.");
                _logger.Info("Updater launched setup; waiting for setup_result.json.");
                _ = Task.Run(() => WaitForSetupResultAsync(TimeSpan.FromMinutes(2)));
                return true;
            }
        }

        var applied = false;
        if (updaterStatus != null)
        {
            var action = (updaterStatus.Action ?? "").Trim().ToLowerInvariant();
            var result = (updaterStatus.Result ?? "").Trim().ToLowerInvariant();
            applied = action is "updated" or "repair" || result == "updated";
        }

        if (!applied)
        {
            var message = "Up to date.";
            _updateState.LastResult = "up-to-date";
            _updateState.LastError = "";
            _updateState.LastErrorCode = "";
            _updateState.LastVersionAfter = _updateState.LastVersionBefore;
            UpdateStateStore.Save(_updateState);
            SendUpdateStatus("idle", message);
            _logger.Info("Updater finished; no restart required.");
            return true;
        }

        _updateState.LastResult = "updated";
        _updateState.LastError = "";
        _updateState.LastErrorCode = "";
        _updateState.LastVersionAfter = VersionUtil.Normalize(VersionUtil.GetVersionFromFile(DadBoardPaths.InstalledExePath));
        UpdateStateStore.Save(_updateState);
        SendUpdateStatus("updated", $"Update applied ({_updateState.LastVersionAfter}). Restarting.");
        _logger.Info("Updater finished; requesting shutdown.");
        ShutdownRequested?.Invoke();
        return true;
    }

    private void ApplySetupResultIfPresent(string source)
    {
        var result = SetupResultStore.Load();
        if (result == null)
        {
            return;
        }

        _updateState.LastResult = result.Success ? "updated" : "failed";
        _updateState.LastError = result.ErrorMessage ?? "";
        _updateState.LastErrorCode = result.ErrorCode ?? "";
        _updateState.LastVersionAfter = string.IsNullOrWhiteSpace(result.VersionAfter)
            ? VersionUtil.Normalize(VersionUtil.GetCurrentVersion())
            : result.VersionAfter;
        UpdateStateStore.Save(_updateState);
        SetupResultStore.TryClear();
        _logger.Info($"Applied setup result from {source}: success={result.Success} version={_updateState.LastVersionAfter} error={_updateState.LastError}");
    }

    private async Task WaitForSetupResultAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !_cts.IsCancellationRequested)
        {
            var result = SetupResultStore.Load();
            if (result != null)
            {
                ApplySetupResultIfPresent("setup_result");
                var status = result.Success ? "updated" : "failed";
                var message = result.Success
                    ? $"Update applied ({_updateState.LastVersionAfter}). Restarting."
                    : (string.IsNullOrWhiteSpace(result.ErrorMessage) ? "Update failed." : result.ErrorMessage);
                SendUpdateStatus(status, message);
                return;
            }

            await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
        }
    }

    private async Task<bool> EnsureUpdaterPresentAsync(string primaryManifestUrl, string fallbackManifestUrl)
    {
        var updaterExe = DadBoardPaths.UpdaterExePath;
        var candidates = new[]
        {
            primaryManifestUrl,
            fallbackManifestUrl
        }.Where(url => !string.IsNullOrWhiteSpace(url))
         .Distinct(StringComparer.OrdinalIgnoreCase)
         .ToArray();

        foreach (var manifestUrl in candidates)
        {
            if (TryResolveLocalUpdaterPath(manifestUrl, out var localPath))
            {
                try
                {
                    File.Copy(localPath, updaterExe, true);
                    FileUnblocker.TryUnblock(updaterExe, msg => _logger.Info(msg));
                    _logger.Info($"Updater copied from {localPath}");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Updater copy failed: {ex.Message}");
                }
            }

            if (TryBuildUpdaterUrl(manifestUrl, out var setupUrl))
            {
                try
                {
                    _logger.Info($"Downloading updater from {setupUrl}");
                    var bytes = await _httpClient.GetByteArrayAsync(setupUrl, _cts.Token).ConfigureAwait(false);
                    Directory.CreateDirectory(Path.GetDirectoryName(updaterExe)!);
                    await File.WriteAllBytesAsync(updaterExe, bytes, _cts.Token).ConfigureAwait(false);
                    FileUnblocker.TryUnblock(updaterExe, msg => _logger.Info(msg));
                    _logger.Info($"Updater downloaded to {updaterExe}");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Updater download failed: {ex.Message}");
                }
            }
        }

        return false;
    }

    private static bool TryResolveLocalUpdaterPath(string manifestUrl, out string localPath)
    {
        localPath = "";
        if (File.Exists(manifestUrl))
        {
            var dir = Path.GetDirectoryName(manifestUrl);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                var candidate = Path.Combine(dir, "DadBoardUpdater.exe");
                if (File.Exists(candidate))
                {
                    localPath = candidate;
                    return true;
                }
            }
        }

        if (Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            var dir = Path.GetDirectoryName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                var candidate = Path.Combine(dir, "DadBoardUpdater.exe");
                if (File.Exists(candidate))
                {
                    localPath = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryBuildUpdaterUrl(string manifestUrl, out string setupUrl)
    {
        setupUrl = "";
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!uri.AbsolutePath.EndsWith("/latest.json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var baseUrl = manifestUrl.Substring(0, manifestUrl.Length - "latest.json".Length);
        setupUrl = baseUrl + "DadBoardUpdater.exe";
        return true;
    }

    private async Task InitializeUpdateConfigAsync()
    {
        var path = UpdateConfigStore.GetConfigPath();
        try
        {
            _logger.Info($"Update init: reading config path={path}");
            if (!File.Exists(path))
            {
                _logger.Info("Update init: update.config.json not found.");
                _updateConfigLoaded = true;
                return;
            }

            var info = new FileInfo(path);
            _logger.Info($"Update init: config size={info.Length} bytes");
            var json = await ReadFileWithTimeoutAsync(path, _updateConfigTimeout).ConfigureAwait(false);
            if (json == null)
            {
                _updateConfigError = "update.config.json read timed out.";
                _logger.Warn($"Update init: {_updateConfigError}");
                RegisterUpdateFailure(_updateConfigError);
                _updateConfigLoaded = true;
                return;
            }

            try
            {
                var config = JsonSerializer.Deserialize<UpdateConfig>(json, JsonUtil.Options);
                _updateConfig = config ?? new UpdateConfig();
                if (!string.IsNullOrWhiteSpace(_updateConfig.ManifestUrl))
                {
                    var resolved = UpdateConfigStore.ResolveManifestUrl(_updateConfig);
                    _updateState.ManifestUrl = resolved;
                    UpdateStateStore.Save(_updateState);
                    var sourceLabel = UpdateConfigStore.IsDefaultManifestUrl(_updateConfig) ? "default" : "override";
                    _logger.Info($"Update init: manifest source set to {resolved} ({sourceLabel})");
                }
                _updateConfigLoaded = true;
                _logger.Info("Update init: config parsed successfully.");
            }
            catch (Exception ex)
            {
                _updateConfigError = $"update.config.json parse failed: {ex.Message}";
                _logger.Warn($"Update init: {_updateConfigError}");
                RegisterUpdateFailure(_updateConfigError);
                _updateConfigLoaded = true;
            }
        }
        catch (Exception ex)
        {
            _updateConfigError = $"update.config.json read failed: {ex.Message}";
            _logger.Warn($"Update init: {_updateConfigError}");
            RegisterUpdateFailure(_updateConfigError);
            _updateConfigLoaded = true;
        }
    }

    private async Task<string?> ReadFileWithTimeoutAsync(string path, TimeSpan timeout)
    {
        var readTask = Task.Run(() => File.ReadAllText(path));
        var completed = await Task.WhenAny(readTask, Task.Delay(timeout, _cts.Token)).ConfigureAwait(false);
        if (completed != readTask)
        {
            return null;
        }

        return await readTask.ConfigureAwait(false);
    }

    private void RegisterUpdateFailure(string error)
    {
        _updateState.LastResult = "failed";
        _updateState.LastError = error;
        if (string.IsNullOrWhiteSpace(_updateState.LastErrorCode))
        {
            _updateState.LastErrorCode = "update_failure";
        }
        _updateState.ConsecutiveFailures++;
        if (_updateState.ConsecutiveFailures >= 3)
        {
            _updateState.UpdatesDisabled = true;
            _logger.Warn("Updates disabled after repeated failures.");
        }

        UpdateStateStore.Save(_updateState);
    }

    private void EnsureUpdateVersionsForFailure()
    {
        if (string.IsNullOrWhiteSpace(_updateState.LastVersionBefore))
        {
            _updateState.LastVersionBefore = VersionUtil.GetCurrentVersion();
        }

        if (string.IsNullOrWhiteSpace(_updateState.LastVersionAfter))
        {
            _updateState.LastVersionAfter = _updateState.LastVersionBefore;
        }
    }

    public void ResetUpdateFailuresLocal(string initiator)
    {
        ResetUpdateFailures(string.IsNullOrWhiteSpace(initiator) ? "local" : initiator);
    }

    private async Task ExecuteUpdateSelf(string correlationId, UpdateSelfCommand command, WebSocket socket)
    {
        SendAck(socket, correlationId, ok: true, null);

        var manifestUrl = string.IsNullOrWhiteSpace(command.UpdateBaseUrl)
            ? ""
            : $"{command.UpdateBaseUrl.TrimEnd('/')}/updates/latest.json";

        await CheckForUpdatesAsync(force: true, manifestOverride: manifestUrl).ConfigureAwait(false);
    }

    private void SendUpdateStatus(string status, string? message)
    {
        _state.UpdateStatus = status;
        _state.UpdateMessage = message ?? "";
        var payload = new UpdateStatusPayload
        {
            Status = status,
            Message = message,
            ErrorCode = _updateState.LastErrorCode,
            VersionBefore = _updateState.LastVersionBefore,
            VersionAfter = _updateState.LastVersionAfter,
            UpdatesDisabled = _updateState.UpdatesDisabled,
            ConsecutiveFailures = _updateState.ConsecutiveFailures,
            LastError = _updateState.LastError,
            LastResult = _updateState.LastResult,
            DisabledUntilUtc = _updateState.DisabledUntilUtc,
            LastResetUtc = _updateState.LastResetUtc,
            LastResetBy = _updateState.LastResetBy
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
            Message = _state.UpdateMessage,
            ErrorCode = _updateState.LastErrorCode,
            VersionBefore = _updateState.LastVersionBefore,
            VersionAfter = _updateState.LastVersionAfter,
            UpdatesDisabled = _updateState.UpdatesDisabled,
            ConsecutiveFailures = _updateState.ConsecutiveFailures,
            LastError = _updateState.LastError,
            LastResult = _updateState.LastResult,
            DisabledUntilUtc = _updateState.DisabledUntilUtc,
            LastResetUtc = _updateState.LastResetUtc,
            LastResetBy = _updateState.LastResetBy
        };
        SendEnvelope(socket, ProtocolConstants.TypeUpdateStatus, "", payload);
    }

    private void ResetUpdateFailures(string initiator)
    {
        _logger.Info($"ResetUpdateFailures: before disabled={_updateState.UpdatesDisabled} failures={_updateState.ConsecutiveFailures}");
        _updateState.ConsecutiveFailures = 0;
        _updateState.UpdatesDisabled = false;
        _updateState.LastResult = "";
        _updateState.LastError = "";
        _updateState.LastErrorCode = "";
        _updateState.LastVersionBefore = "";
        _updateState.LastVersionAfter = "";
        _updateState.DisabledUntilUtc = "";
        _updateState.LastResetUtc = DateTime.UtcNow.ToString("O");
        _updateState.LastResetBy = initiator;
        UpdateStateStore.Save(_updateState);

        _state.UpdateStatus = "idle";
        _state.UpdateMessage = "Update failures reset.";
        SendUpdateStatus("idle", "Update failures reset.");
        _logger.Info($"ResetUpdateFailures: after disabled={_updateState.UpdatesDisabled} failures={_updateState.ConsecutiveFailures}");
        _logger.Info($"Update failures reset by {initiator}.");
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

    private void SendStatus(string correlationId, string state, string? gameId, string? message, string? errorClass = null)
    {
        var status = new StatusPayload
        {
            State = state,
            GameId = gameId,
            Message = message,
            ErrorClass = errorClass
        };
        _state.LastCommandState = state;
        if (!string.IsNullOrWhiteSpace(gameId))
        {
            _state.LastLaunchGameId = gameId;
            _state.LastLaunchCorrelationId = correlationId;
            _state.LastLaunchState = state;
            _state.LastLaunchMessage = message ?? "";
            _state.LastLaunchErrorClass = errorClass ?? "";
            _state.LastLaunchTs = DateTime.UtcNow.ToString("O");
        }
        BroadcastEnvelope(ProtocolConstants.TypeStatus, correlationId, status);
    }

    private static string MapLaunchErrorClass(Win32Exception ex)
    {
        return ex.NativeErrorCode switch
        {
            2 => "file_not_found",
            3 => "file_not_found",
            5 => "permission_denied",
            _ => "launch_failed"
        };
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
            Version = VersionUtil.Normalize(GetAppVersion()),
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
        try
        {
            var path = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path))
            {
                return "0.0.0";
            }

            return VersionUtil.GetVersionFromFile(path);
        }
        catch
        {
            return "0.0.0";
        }
    }
}
