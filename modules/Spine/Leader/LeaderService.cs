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
    private readonly HttpClient _updateHttp = new();
    private UpdateConfig _updateConfig = new();
    private readonly TimeSpan _updateGracePeriod = TimeSpan.FromMinutes(3);
    private Timer? _mirrorTimer;
    private readonly object _mirrorLock = new();
    private bool _mirrorDisabled;
    private int _mirrorFailures;
    private string _mirrorManifestUrl = "";
    private string _mirrorLocalHostUrl = "";
    private string _mirrorLastManifestResult = "";
    private string _mirrorLastDownloadResult = "";
    private DateTime _mirrorLastManifestFetchUtc;
    private DateTime _mirrorLastDownloadUtc;
    private string _mirrorLastError = "";
    private string _cachedVersions = "";
    private string _mirrorLatestVersion = "";
    private bool _updateConfigLoaded;
    private string _lastHostIp = "";
    private string _lastHostReason = "";

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
        _updateHttp.Timeout = TimeSpan.FromSeconds(5);

        _config = LoadConfig();
        LoadInventoryCaches();

        StartDiscovery();
        StartUpdateHost();
        _offlineTimer = new Timer(_ => UpdateOnlineStates(), null, 0, 1000);
        _persistTimer = new Timer(_ => PersistKnownAgents(), null, 0, 5000);
        _stateTimer = new Timer(_ => PersistState(), null, 0, 1000);
        _reconnectTimer = new Timer(_ => TryReconnectOnlineAgents(), null, 0, 2000);

        _ = Task.Run(RefreshSteamInventory);
        _ = Task.Run(InitializeUpdateMirrorAsync);
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

    public UpdateMirrorSnapshot GetUpdateMirrorSnapshot()
    {
        return new UpdateMirrorSnapshot
        {
            Enabled = _updateConfig.MirrorEnabled && string.Equals(_updateConfig.Source, "github_mirror", StringComparison.OrdinalIgnoreCase),
            ManifestUrl = UpdateConfigStore.ResolveManifestUrl(_updateConfig),
            LatestVersion = _mirrorLatestVersion,
            LocalHostUrl = _mirrorLocalHostUrl,
            LastManifestFetchUtc = _mirrorLastManifestFetchUtc == default ? "" : _mirrorLastManifestFetchUtc.ToString("O"),
            LastManifestResult = _mirrorLastManifestResult,
            LastDownloadUtc = _mirrorLastDownloadUtc == default ? "" : _mirrorLastDownloadUtc.ToString("O"),
            LastDownloadResult = _mirrorLastDownloadResult,
            CachedVersions = _cachedVersions,
            LastError = _mirrorLastError
        };
    }

    public string GetAvailableVersion()
    {
        if (!string.IsNullOrWhiteSpace(_mirrorLatestVersion))
        {
            return _mirrorLatestVersion;
        }

        var localManifest = Path.Combine(DadBoardPaths.UpdateSourceDir, "latest.json");
        var version = TryReadManifestVersion(localManifest);
        if (!string.IsNullOrWhiteSpace(version))
        {
            _mirrorLatestVersion = version;
        }

        return _mirrorLatestVersion;
    }

    public (string Url, string Reason) GetLocalUpdateHostUrlWithReason()
    {
        var url = ResolveLocalHostUrl(out var reason);
        return (url, reason);
    }

    public void ReloadUpdateConfig()
    {
        try
        {
            _updateConfig = UpdateConfigStore.Load();
            _updateConfigLoaded = true;
            _mirrorDisabled = false;
            _mirrorFailures = 0;
            var resolved = UpdateConfigStore.ResolveManifestUrl(_updateConfig);
            var sourceLabel = UpdateConfigStore.IsDefaultManifestUrl(_updateConfig) ? "default" : "override";
            _logger.Info($"Update mirror: config reloaded source={_updateConfig.Source} manifest={resolved} ({sourceLabel})");

            if (IsMirrorEnabled())
            {
                StartMirrorTimer();
            }
            else
            {
                _mirrorTimer?.Dispose();
            }
        }
        catch (Exception ex)
        {
            _mirrorLastError = ex.Message;
            _logger.Warn($"Update mirror: config reload failed: {ex.Message}");
        }
    }

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

        _logger.Info($"Update requested (single) pcId={agent.PcId} ip={agent.Ip}");
        _ = Task.Run(() => SendUpdateSelf(agent));
        return true;
    }

    public void SendUpdateAllOnline()
    {
        var targets = _agents.Values.Where(a => a.Online).ToList();
        _logger.Info($"Update requested (all online) targets={targets.Count} pcIds=[{string.Join(",", targets.Select(t => t.PcId))}]");
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
            Directory.CreateDirectory(DadBoardPaths.UpdateSourceDir);
            var builder = WebApplication.CreateBuilder();
            var port = GetUpdateHostPort();
            builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
            var app = builder.Build();

            app.MapGet("/updates/latest.json", (HttpContext context) =>
            {
                var manifest = BuildUpdateManifest(context);
                if (manifest == null)
                {
                    _logger.Warn("Update host latest.json not available.");
                    return Results.Problem("Update manifest not available.", statusCode: StatusCodes.Status404NotFound);
                }

                return Results.Json(manifest);
            });

            app.MapGet("/updates/{file}", (string file) =>
            {
                var path = Path.Combine(DadBoardPaths.UpdateSourceDir, file);
                if (!File.Exists(path))
                {
                    var cached = Path.Combine(DadBoardPaths.UpdateSourceDir, "cache", file);
                    if (File.Exists(cached))
                    {
                        return Results.File(cached, "application/octet-stream", file);
                    }

                    _logger.Warn($"Update host missing file: {path}");
                    return Results.Problem("Update file not found.", statusCode: StatusCodes.Status404NotFound);
                }

                return Results.File(path, "application/octet-stream", file);
            });

            _updateHost = app;
            _updateHostTask = app.RunAsync(_cts.Token);
            _logger.Info($"Update host listening on port {port}.");
        }
        catch (Exception ex)
        {
            _logger.Error($"Update host failed to start: {ex.Message}");
        }
    }

    private int GetUpdateHostPort()
    {
        if (_updateConfig.LocalHostPort > 0)
        {
            return _updateConfig.LocalHostPort;
        }

        return _config.UpdatePort;
    }

    private async Task InitializeUpdateMirrorAsync()
    {
        try
        {
            _logger.Info("Update mirror: loading update config.");
            _updateConfig = await Task.Run(UpdateConfigStore.Load).ConfigureAwait(false);
            _updateConfigLoaded = true;
            var resolved = UpdateConfigStore.ResolveManifestUrl(_updateConfig);
            var sourceLabel = UpdateConfigStore.IsDefaultManifestUrl(_updateConfig) ? "default" : "override";
            _logger.Info($"Update mirror: source={_updateConfig.Source} manifest={resolved} ({sourceLabel})");

            if (IsMirrorEnabled())
            {
                StartMirrorTimer();
            }
        }
        catch (Exception ex)
        {
            _mirrorLastError = ex.Message;
            _logger.Warn($"Update mirror: config load failed: {ex.Message}");
        }
    }

    private bool IsMirrorEnabled()
    {
        return _updateConfig.MirrorEnabled &&
               string.Equals(_updateConfig.Source, "github_mirror", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(UpdateConfigStore.ResolveManifestUrl(_updateConfig));
    }

    private void StartMirrorTimer()
    {
        var minutes = _updateConfig.MirrorPollMinutes > 0 ? _updateConfig.MirrorPollMinutes : 10;
        _mirrorTimer?.Dispose();
        _mirrorTimer = new Timer(_ => _ = Task.Run(MirrorPollAsync), null, TimeSpan.Zero, TimeSpan.FromMinutes(minutes));
        _logger.Info($"Update mirror: polling every {minutes} minutes.");
    }

    private async Task MirrorPollAsync()
    {
        if (_mirrorDisabled || !IsMirrorEnabled())
        {
            return;
        }

        lock (_mirrorLock)
        {
            _mirrorManifestUrl = UpdateConfigStore.ResolveManifestUrl(_updateConfig);
        }

        try
        {
            _mirrorLastManifestFetchUtc = DateTime.UtcNow;
            _mirrorLastManifestResult = "fetching";
            var manifest = await FetchManifestAsync(_mirrorManifestUrl).ConfigureAwait(false);
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.PackageUrl))
            {
                RegisterMirrorFailure("Manifest unavailable.");
                return;
            }

            var version = VersionUtil.Normalize(manifest.LatestVersion);
            if (string.IsNullOrWhiteSpace(version))
            {
                RegisterMirrorFailure("Manifest missing version.");
                return;
            }
            _mirrorLatestVersion = version;

            var cacheDir = Path.Combine(DadBoardPaths.UpdateSourceDir, "cache");
            Directory.CreateDirectory(cacheDir);
            var cachedZip = Path.Combine(cacheDir, $"DadBoard-{version}.zip");
            var needsDownload = !File.Exists(cachedZip);

            if (needsDownload)
            {
                _mirrorLastDownloadUtc = DateTime.UtcNow;
                _mirrorLastDownloadResult = "downloading";
                await DownloadFileAsync(manifest.PackageUrl, cachedZip).ConfigureAwait(false);
                _mirrorLastDownloadResult = "downloaded";
                _mirrorLastDownloadUtc = DateTime.UtcNow;
            }

            var sha256 = EnsureSha256(cachedZip);
            var localHostUrl = GetLocalHostUrl();
            var localManifest = new UpdateManifest
            {
                LatestVersion = version,
                PackageUrl = $"{localHostUrl}/updates/DadBoard-{version}.zip",
                Sha256 = sha256,
                ForceCheckToken = manifest.ForceCheckToken,
                MinSupportedVersion = manifest.MinSupportedVersion
            };

            var manifestPath = Path.Combine(DadBoardPaths.UpdateSourceDir, "latest.json");
            var json = JsonSerializer.Serialize(localManifest, JsonUtil.Options);
            File.WriteAllText(manifestPath, json);

            _mirrorLocalHostUrl = $"{localHostUrl}/updates/latest.json";
            _mirrorLastManifestResult = "ok";
            _mirrorLastError = "";
            _mirrorFailures = 0;
            _mirrorDisabled = false;
            PruneCache(cacheDir, version);
            _cachedVersions = string.Join(", ", Directory.GetFiles(cacheDir, "DadBoard-*.zip")
                .Select(Path.GetFileNameWithoutExtension)
                .Select(name => name?.Replace("DadBoard-", "", StringComparison.OrdinalIgnoreCase))
                .Where(name => !string.IsNullOrWhiteSpace(name)));

            _logger.Info($"Update mirror: cached {version} and wrote local manifest.");
            BroadcastUpdateSource();
        }
        catch (Exception ex)
        {
            RegisterMirrorFailure(ex.Message);
        }
    }

    private async Task<UpdateManifest?> FetchManifestAsync(string manifestUrl)
    {
        try
        {
            _logger.Info($"Update mirror: fetching manifest {manifestUrl}");
            var response = await _updateHttp.GetAsync(manifestUrl, _cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonSerializer.Deserialize<UpdateManifest>(json, JsonUtil.Options);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Update mirror: manifest fetch failed: {ex.Message}");
            _mirrorLastManifestResult = $"error: {ex.Message}";
            return null;
        }
    }

    private async Task DownloadFileAsync(string url, string destination)
    {
        _logger.Info($"Update mirror: downloading {url}");
        using var response = await _updateHttp.GetAsync(url, _cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(file, _cts.Token).ConfigureAwait(false);
    }

    private string EnsureSha256(string zipPath)
    {
        var shaPath = zipPath + ".sha256";
        try
        {
            if (File.Exists(shaPath))
            {
                var existing = File.ReadAllText(shaPath).Trim();
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    return existing;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Update mirror: failed reading sha256 for {Path.GetFileName(zipPath)}: {ex.Message}");
        }

        try
        {
            var sha256 = HashUtil.ComputeSha256(zipPath);
            File.WriteAllText(shaPath, sha256);
            _logger.Info($"Update mirror: sha256 computed for {Path.GetFileName(zipPath)}");
            return sha256;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Update mirror: sha256 compute failed for {Path.GetFileName(zipPath)}: {ex.Message}");
            return "";
        }
    }

    private void PruneCache(string cacheDir, string latestVersion)
    {
        if (!Directory.Exists(cacheDir))
        {
            return;
        }

        var entries = Directory.GetFiles(cacheDir, "DadBoard-*.zip")
            .Select(path => (path, version: ParseVersionFromFileName(Path.GetFileName(path))))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.version))
            .OrderByDescending(entry => entry.version, new VersionComparer())
            .ToList();

        if (entries.Count <= 3)
        {
            return;
        }

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var latestPath = Path.Combine(cacheDir, $"DadBoard-{latestVersion}.zip");
        if (File.Exists(latestPath))
        {
            keep.Add(latestPath);
        }

        foreach (var entry in entries)
        {
            if (keep.Count >= 3)
            {
                break;
            }

            keep.Add(entry.path);
        }

        foreach (var entry in entries)
        {
            if (keep.Contains(entry.path))
            {
                continue;
            }

            try
            {
                File.Delete(entry.path);
                var shaPath = entry.path + ".sha256";
                if (File.Exists(shaPath))
                {
                    File.Delete(shaPath);
                }
                _logger.Info($"Update mirror: pruned {Path.GetFileName(entry.path)}");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Update mirror: prune failed for {entry.path}: {ex.Message}");
            }
        }
    }

    private void RegisterMirrorFailure(string error)
    {
        _mirrorLastError = error;
        _mirrorLastManifestResult = $"error: {error}";
        _mirrorFailures++;
        if (_mirrorFailures >= 3)
        {
            _mirrorDisabled = true;
            _logger.Warn("Update mirror disabled after repeated failures.");
        }

        _logger.Warn($"Update mirror: {error}");
    }

    private string GetLocalHostUrl()
    {
        return ResolveLocalHostUrl(out _);
    }

    private string ResolveLocalHostUrl(out string reason)
    {
        string ip;
        if (!string.IsNullOrWhiteSpace(_updateConfig.LocalHostIp))
        {
            ip = _updateConfig.LocalHostIp;
            reason = "config override";
        }
        else
        {
            ip = _localIps.FirstOrDefault(addr => !string.Equals(addr, "127.0.0.1", StringComparison.OrdinalIgnoreCase)) ?? "";
            if (string.IsNullOrWhiteSpace(ip))
            {
                ip = "127.0.0.1";
                reason = "fallback loopback";
            }
            else
            {
                reason = "auto non-loopback";
            }
        }

        if (!string.Equals(ip, _lastHostIp, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(reason, _lastHostReason, StringComparison.OrdinalIgnoreCase))
        {
            _lastHostIp = ip;
            _lastHostReason = reason;
            _logger.Info($"Update host IP selected: {ip} ({reason})");
        }

        return $"http://{ip}:{GetUpdateHostPort()}";
    }

    private UpdateManifest? BuildUpdateManifest(HttpContext context)
    {
        try
        {
            var manifestPath = Path.Combine(DadBoardPaths.UpdateSourceDir, "latest.json");
            UpdateManifest? manifest = null;

            if (File.Exists(manifestPath))
            {
                var json = File.ReadAllText(manifestPath);
                manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonUtil.Options);
            }

            if (manifest == null || string.IsNullOrWhiteSpace(manifest.PackageUrl))
            {
                if (!TryGetLatestPackage(out var fileName, out var version))
                {
                    return null;
                }

                manifest ??= new UpdateManifest();
                manifest.LatestVersion = VersionUtil.Normalize(version);
                manifest.PackageUrl = $"{context.Request.Scheme}://{context.Request.Host}/updates/{fileName}";
            }

            if (string.IsNullOrWhiteSpace(manifest.LatestVersion))
            {
                manifest.LatestVersion = VersionUtil.Normalize("0.0.0");
            }

            if (manifest.PackageUrl.StartsWith("/", StringComparison.Ordinal))
            {
                manifest.PackageUrl = $"{context.Request.Scheme}://{context.Request.Host}{manifest.PackageUrl}";
            }

            if (string.IsNullOrWhiteSpace(manifest.Sha256))
            {
                try
                {
                    var fileName = Path.GetFileName(new Uri(manifest.PackageUrl).LocalPath);
                    if (!string.IsNullOrWhiteSpace(fileName))
                    {
                        var localPath = Path.Combine(DadBoardPaths.UpdateSourceDir, fileName);
                        if (!File.Exists(localPath))
                        {
                            localPath = Path.Combine(DadBoardPaths.UpdateSourceDir, "cache", fileName);
                        }

                        var shaPath = localPath + ".sha256";
                        if (File.Exists(shaPath))
                        {
                            manifest.Sha256 = File.ReadAllText(shaPath).Trim();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Update manifest sha256 lookup failed: {ex.Message}");
                }
            }

            return manifest;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Update manifest build failed: {ex.Message}");
            return null;
        }
    }

    private static bool TryGetLatestPackage(out string fileName, out string version)
    {
        fileName = "";
        version = "0.0.0";
        if (!Directory.Exists(DadBoardPaths.UpdateSourceDir))
        {
            return false;
        }

        var packages = Directory.GetFiles(DadBoardPaths.UpdateSourceDir, "DadBoard-*.zip");
        var cacheDir = Path.Combine(DadBoardPaths.UpdateSourceDir, "cache");
        if (Directory.Exists(cacheDir))
        {
            packages = packages.Concat(Directory.GetFiles(cacheDir, "DadBoard-*.zip")).ToArray();
        }
        if (packages.Length == 0)
        {
            return false;
        }

        var best = packages
            .Select(path => (path, version: ParseVersionFromFileName(Path.GetFileName(path))))
            .OrderByDescending(entry => entry.version, new VersionComparer())
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(best.path))
        {
            return false;
        }

        fileName = Path.GetFileName(best.path);
        version = best.version;
        return true;
    }

    private static string ParseVersionFromFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (name.StartsWith("DadBoard-", StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring("DadBoard-".Length);
        }

        return VersionUtil.Normalize(name);
    }

    private sealed class VersionComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
            => VersionUtil.Compare(x, y);
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
            version = VersionUtil.GetVersionFromFile(runtimePath);
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

        EvaluateUpdateCompletion(agent);

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
                if (agent.Online)
                {
                    agent.Online = false;
                    if (agent.UpdateInProgress)
                    {
                        MarkUpdateRestarting(agent, "Agent went offline during update.");
                    }
                }
            }

            if (agent.UpdateInProgress && now > agent.UpdateGraceUntilUtc && !agent.Online)
            {
                MarkUpdateFailed(agent, "Update timed out waiting for restart.");
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

    public void LogGameSelection(int appId, string gameName)
    {
        _logger.Info($"Game selected appId={appId} name={gameName}");
    }

    public void LogTargetSelection(IEnumerable<string> pcIds)
    {
        var targets = string.Join(",", pcIds);
        _logger.Info($"Targets selected=[{targets}]");
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
        var port = GetUpdateHostPort();
        var agentIp = agent.Ip ?? "";
        if (!string.IsNullOrWhiteSpace(_updateConfig.LocalHostIp))
        {
            return $"http://{_updateConfig.LocalHostIp}:{port}";
        }

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
            var message = connection?.LastError ?? "Unable to connect";
            MarkUpdateFailed(agent, message);
            _logger.Warn($"Update command skipped for {agent.Name}: {message}");
            return;
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var manifestUrl = GetPreferredManifestUrl(agent);
        var payload = new TriggerUpdateNowCommand
        {
            ManifestUrl = manifestUrl
        };

        var envelope = new
        {
            type = ProtocolConstants.TypeCommandTriggerUpdateNow,
            correlationId,
            pcId = agent.PcId,
            payload,
            ts = DateTime.UtcNow.ToString("O")
        };

        var json = JsonSerializer.Serialize(envelope, JsonUtil.Options);
        var buffer = Encoding.UTF8.GetBytes(json);

        MarkUpdateRequested(agent, manifestUrl);
        _logger.Info($"Sending TriggerUpdateNow to pcId={agent.PcId} ip={agent.Ip} ws={agent.WsPort} manifest={manifestUrl} corr={correlationId}");

        try
        {
            await connection.Socket.SendAsync(buffer, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            MarkUpdateFailed(agent, $"Send failed: {ex.Message}");
            _logger.Warn($"TriggerUpdateNow send failed for {agent.Name}: {ex.Message}");
        }
    }

    private string GetPreferredManifestUrl(AgentInfo agent)
    {
        var local = GetLocalManifestUrl(agent);
        if (!string.IsNullOrWhiteSpace(local))
        {
            return local;
        }

        return UpdateConfigStore.ResolveManifestUrl(_updateConfig);
    }

    private string GetLocalManifestUrl(AgentInfo agent)
    {
        var localManifestPath = Path.Combine(DadBoardPaths.UpdateSourceDir, "latest.json");
        if (!File.Exists(localManifestPath))
        {
            return "";
        }

        return $"{GetUpdateBaseUrl(agent)}/updates/latest.json";
    }

    private void MarkUpdateRequested(AgentInfo agent, string manifestUrl)
    {
        agent.UpdateRequestedUtc = DateTime.UtcNow;
        agent.UpdateStartedUtc = default;
        agent.UpdateGraceUntilUtc = DateTime.UtcNow.Add(_updateGracePeriod);
        agent.UpdatePreviousVersion = agent.Version ?? "";
        agent.UpdateExpectedVersion = ResolveExpectedVersion(manifestUrl);
        agent.UpdateStatus = "requested";
        agent.UpdateMessage = $"Update requested via {manifestUrl}";
        agent.UpdateInProgress = true;
        agent.LastResult = "Updating";
        agent.LastError = "None";
        _logger.Info($"Update requested pcId={agent.PcId} current={agent.UpdatePreviousVersion} expected={agent.UpdateExpectedVersion}");
    }

    private void MarkUpdateRestarting(AgentInfo agent, string message)
    {
        agent.UpdateStatus = "restarting";
        agent.UpdateMessage = message;
        agent.UpdateInProgress = true;
        agent.UpdateGraceUntilUtc = DateTime.UtcNow.Add(_updateGracePeriod);
        agent.LastResult = "Restarting";
        agent.LastError = "None";
        _logger.Info($"Update restarting pcId={agent.PcId} message={message}");
    }

    private void MarkUpdateSuccess(AgentInfo agent, string? message = null)
    {
        agent.UpdateStatus = "updated";
        agent.UpdateMessage = message ?? $"Updated to {agent.Version}";
        agent.UpdateInProgress = false;
        agent.LastResult = "Updated";
        agent.LastError = "None";
        _logger.Info($"Update success pcId={agent.PcId} version={agent.Version}");
    }

    private void MarkUpdateFailed(AgentInfo agent, string message)
    {
        agent.UpdateStatus = "failed";
        agent.UpdateMessage = message;
        agent.UpdateInProgress = false;
        agent.LastResult = "Failed";
        agent.LastError = string.IsNullOrWhiteSpace(message) ? "Update failed." : message;
        _logger.Warn($"Update failed pcId={agent.PcId} error={agent.LastError}");
    }

    private void ApplyUpdateStatus(AgentInfo agent, UpdateStatusPayload status)
    {
        var statusValue = (status.Status ?? "").Trim();
        if (string.IsNullOrWhiteSpace(statusValue))
        {
            return;
        }

        var normalized = statusValue.ToLowerInvariant();
        var message = status.Message ?? "";
        agent.UpdateStatus = normalized;
        agent.UpdateMessage = message;

        if (normalized is "starting" or "starting_update" or "downloading" or "installing" or "applying")
        {
            agent.UpdateInProgress = true;
            if (agent.UpdateStartedUtc == default)
            {
                agent.UpdateStartedUtc = DateTime.UtcNow;
            }
            agent.UpdateGraceUntilUtc = DateTime.UtcNow.Add(_updateGracePeriod);
            agent.LastResult = "Updating";
            agent.LastError = "None";
        }
        else if (normalized is "restarting")
        {
            MarkUpdateRestarting(agent, string.IsNullOrWhiteSpace(message) ? "Agent restarting for update." : message);
        }
        else if (normalized is "updated")
        {
            MarkUpdateSuccess(agent, message);
        }
        else if (normalized is "failed")
        {
            MarkUpdateFailed(agent, message);
        }

        _logger.Info($"Update status pcId={agent.PcId} status={normalized} message={message}");
    }

    private void EvaluateUpdateCompletion(AgentInfo agent)
    {
        if (!agent.UpdateInProgress)
        {
            return;
        }

        if (IsUpdateSuccess(agent))
        {
            MarkUpdateSuccess(agent);
            return;
        }

        if (DateTime.UtcNow > agent.UpdateGraceUntilUtc)
        {
            MarkUpdateFailed(agent, "Update did not complete before timeout.");
            return;
        }

        if (!string.Equals(agent.UpdateStatus, "restarting", StringComparison.OrdinalIgnoreCase))
        {
            MarkUpdateRestarting(agent, "Waiting for update restart.");
        }
    }

    private bool IsUpdateSuccess(AgentInfo agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.UpdateExpectedVersion))
        {
            return VersionUtil.Compare(agent.Version, agent.UpdateExpectedVersion) >= 0;
        }

        if (!string.IsNullOrWhiteSpace(agent.UpdatePreviousVersion))
        {
            return VersionUtil.Compare(agent.Version, agent.UpdatePreviousVersion) > 0;
        }

        return false;
    }

    private string ResolveExpectedVersion(string manifestUrl)
    {
        var localManifest = Path.Combine(DadBoardPaths.UpdateSourceDir, "latest.json");
        var version = TryReadManifestVersion(localManifest);
        if (!string.IsNullOrWhiteSpace(version))
        {
            return version;
        }

        if (TryResolveManifestPath(manifestUrl, out var path))
        {
            version = TryReadManifestVersion(path);
            if (!string.IsNullOrWhiteSpace(version))
            {
                return version;
            }
        }

        return "";
    }

    private static bool TryResolveManifestPath(string manifestUrl, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            return false;
        }

        if (File.Exists(manifestUrl))
        {
            path = manifestUrl;
            return true;
        }

        if (Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            path = uri.LocalPath;
            return true;
        }

        return false;
    }

    private static string TryReadManifestVersion(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return "";
            }

            var json = File.ReadAllText(path);
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonUtil.Options);
            return VersionUtil.Normalize(manifest?.LatestVersion ?? "");
        }
        catch
        {
            return "";
        }
    }

    private void BroadcastUpdateSource()
    {
        foreach (var entry in _connections.Values)
        {
            if (entry.Socket.State != WebSocketState.Open)
            {
                continue;
            }

            if (_agents.TryGetValue(entry.PcId, out var agent))
            {
                _ = Task.Run(() => SendUpdateSource(agent, entry));
            }
        }
    }

    private async Task SendUpdateSource(AgentInfo agent, AgentConnection connection)
    {
        var manifestUrl = GetLocalManifestUrl(agent);
        var payload = new UpdateSourcePayload
        {
            PrimaryManifestUrl = manifestUrl,
            FallbackManifestUrl = UpdateConfigStore.ResolveManifestUrl(_updateConfig)
        };

        var envelope = new
        {
            type = ProtocolConstants.TypeUpdateSource,
            correlationId = Guid.NewGuid().ToString("N"),
            pcId = agent.PcId,
            payload,
            ts = DateTime.UtcNow.ToString("O")
        };

        var json = JsonSerializer.Serialize(envelope, JsonUtil.Options);
        var buffer = Encoding.UTF8.GetBytes(json);

        try
        {
            await connection.Socket.SendAsync(buffer, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
            _logger.Info($"Update source sent to pcId={agent.PcId} primary={payload.PrimaryManifestUrl} fallback={payload.FallbackManifestUrl}");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Update source send failed for {agent.Name}: {ex.Message}");
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
            _ = Task.Run(() => SendUpdateSource(agent, connection));
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
            _logger.Info($"Ack received pcId={agent.PcId} ok={agent.LastAckOk} error={agent.LastAckError}");
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
                ApplyUpdateStatus(agent, status);
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
