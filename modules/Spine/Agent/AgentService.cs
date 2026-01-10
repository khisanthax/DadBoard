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
    private string _updateConfigError = "";
    private readonly TimeSpan _updateConfigTimeout = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _manifestTimeout = TimeSpan.FromSeconds(5);

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

    private async Task CheckForUpdatesAsync(bool force, string? manifestOverride)
    {
        if (_updateState.UpdatesDisabled && !force)
        {
            _logger.Warn("Updates disabled due to repeated failures.");
            return;
        }
        if (_updateState.UpdatesDisabled && force)
        {
            var message = "Updates disabled due to repeated failures.";
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
                RegisterUpdateFailure(message);
                SendUpdateStatus("failed", message);
                return;
            }

            var latest = VersionUtil.Normalize(manifest.LatestVersion);
            var current = VersionUtil.GetCurrentVersion();
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
            var ok = await RunSetupUpdateAsync(updateUrl).ConfigureAwait(false);
            if (!ok && !string.IsNullOrWhiteSpace(fallbackUrl) &&
                !string.Equals(updateUrl, fallbackUrl, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warn($"Update failed via primary manifest; retrying fallback {fallbackUrl}");
                SendUpdateStatus("downloading", "Primary update failed; retrying fallback.");
                ok = await RunSetupUpdateAsync(fallbackUrl).ConfigureAwait(false);
                if (ok)
                {
                    _updateState.ManifestUrl = fallbackUrl;
                    UpdateStateStore.Save(_updateState);
                }
            }

            if (!ok)
            {
                RegisterUpdateFailure("Updater failed.");
                return;
            }

            _updateState.LastResult = "restarting";
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

    private async Task<bool> RunSetupUpdateAsync(string manifestUrl)
    {
        var setupExe = DadBoardPaths.SetupExePath;
        if (!File.Exists(setupExe))
        {
            SendUpdateStatus("failed", $"Updater not found: {setupExe}");
            _logger.Warn($"Update skipped: {setupExe} missing.");
            return false;
        }

        SendUpdateStatus("downloading", "Starting updater.");
        _logger.Info($"Launching updater: {setupExe} /update --silent --manifest \"{manifestUrl}\"");

        var startInfo = new ProcessStartInfo(setupExe, $"/update --silent --manifest \"{manifestUrl}\"")
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(setupExe) ?? Environment.CurrentDirectory
        };

        var process = Process.Start(startInfo);
        if (process == null)
        {
            SendUpdateStatus("failed", "Failed to launch updater.");
            _logger.Warn("Updater process failed to start.");
            return false;
        }

        SendUpdateStatus("installing", "Updater running.");
        await process.WaitForExitAsync(_cts.Token).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            SendUpdateStatus("failed", $"Updater exited with code {process.ExitCode}.");
            _logger.Warn($"Updater exited with {process.ExitCode}.");
            return false;
        }

        SendUpdateStatus("restarting", "Restarting DadBoard.");
        _logger.Info("Updater finished; requesting shutdown.");
        ShutdownRequested?.Invoke();
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
        _updateState.ConsecutiveFailures++;
        if (_updateState.ConsecutiveFailures >= 3)
        {
            _updateState.UpdatesDisabled = true;
            _logger.Warn("Updates disabled after repeated failures.");
        }

        UpdateStateStore.Save(_updateState);
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
