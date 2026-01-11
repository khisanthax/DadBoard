using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows.Forms;
using DadBoard.Agent;
using DadBoard.Leader;
using DadBoard.Spine.Shared;

namespace DadBoard.App;

sealed class TrayAppContext : ApplicationContext
{
    private readonly string _baseDir;
    private readonly string _agentConfigPath;
    private readonly AppConfigStore _configStore;
    private readonly SynchronizationContext _uiContext;
    private readonly string? _postInstallId;
    private System.Windows.Forms.Timer? _postInstallTimer;

    private readonly AgentService _agent;
    private LeaderService? _leader;
    private LeaderForm? _leaderForm;
    private StatusForm? _statusForm;
    private DiagnosticsForm? _diagnosticsForm;

    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _enableLeaderItem;
    private readonly ToolStripMenuItem _disableLeaderItem;
    private readonly ToolStripMenuItem _openDashboardItem;
    private readonly ToolStripMenuItem _startLeaderOnLoginItem;
    private readonly ToolStripMenuItem _installItem;
    private readonly ToolStripMenuItem _runSetupItem;
    private readonly ToolStripMenuItem _resetUpdateFailuresItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _diagnosticsItem;

    private readonly AppLaunchOptions _options;
    private AgentConfig _config;
    private bool _disposed;
    private readonly UpdateLogger _updateLogger = new();

    public TrayAppContext(AppLaunchOptions options)
    {
        _options = options;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _postInstallId = options.PostInstallId;
        _baseDir = DataPaths.ResolveBaseDir();
        _agentConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DadBoard",
            "Agent",
            "agent.config.json");
        _configStore = new AppConfigStore(_agentConfigPath);

        _agent = new AgentService(_baseDir);
        _agent.ShutdownRequested += () => _uiContext.Post(_ => Exit(), null);
        _agent.Start();

        _config = _configStore.Load();

        _enableLeaderItem = new ToolStripMenuItem("Enable Leader", null, (_, _) => EnableLeader(showUI: true));
        _disableLeaderItem = new ToolStripMenuItem("Disable Leader", null, (_, _) => DisableLeader());
        _openDashboardItem = new ToolStripMenuItem("Open Dashboard", null, (_, _) => ShowLeaderUI());
        _startLeaderOnLoginItem = new ToolStripMenuItem("Start Leader on Login") { CheckOnClick = true };
        _startLeaderOnLoginItem.Checked = _config.StartLeaderOnLogin;
        _startLeaderOnLoginItem.CheckedChanged += (_, _) => ToggleStartLeaderOnLogin();

        _installItem = new ToolStripMenuItem("Install (Admin)", null, (_, _) => Install());
        _runSetupItem = new ToolStripMenuItem("Update / Run Setup...", null, (_, _) => _ = RunSetupAsync());
        _resetUpdateFailuresItem = new ToolStripMenuItem("Reset Update Failures (This PC)", null, (_, _) => ResetUpdateFailuresLocal());
        _statusItem = new ToolStripMenuItem("Show Status", null, (_, _) => ShowStatus());
        _diagnosticsItem = new ToolStripMenuItem("Diagnostics", null, (_, _) => ShowDiagnostics());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _openDashboardItem,
            _enableLeaderItem,
            _disableLeaderItem,
            new ToolStripSeparator(),
            _startLeaderOnLoginItem,
            _installItem,
            _runSetupItem,
            _resetUpdateFailuresItem,
            _statusItem,
            _diagnosticsItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => Exit())
        });

        var trayIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        _tray = new NotifyIcon
        {
            Icon = trayIcon,
            Text = "DadBoard",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) =>
        {
            if (_leader != null)
            {
                ShowLeaderUI();
            }
            else
            {
                ShowStatus();
            }
        };
        SignalTrayReadyWhenReady();

        if (_options.Mode == AppMode.Leader || (_options.Mode != AppMode.Agent && _config.StartLeaderOnLogin))
        {
            EnableLeader(showUI: _options.Mode == AppMode.Leader);
        }

        UpdateMenuState();
    }

    private void EnableLeader(bool showUI)
    {
        if (_leader != null)
        {
            if (showUI)
            {
                ShowLeaderUI();
            }
            return;
        }

        _leader = new LeaderService(_baseDir);
        if (showUI)
        {
            ShowLeaderUI();
        }

        UpdateMenuState();
    }

    private void DisableLeader()
    {
        if (_leader == null)
        {
            return;
        }

        _leaderForm?.AllowClose();
        _leaderForm?.Close();
        _leaderForm = null;

        _leader.Dispose();
        _leader = null;

        _statusForm?.UpdateLeader(null);
        UpdateMenuState();
    }

    private void ShowLeaderUI()
    {
        if (_leader == null)
        {
            return;
        }

        if (_leaderForm == null || _leaderForm.IsDisposed)
        {
            _leaderForm = new LeaderForm(_leader);
            _leaderForm.FormClosed += (_, _) => { _leaderForm = null; };
        }

        _leaderForm.Show();
        _leaderForm.BringToFront();
    }

    private void ShowStatus()
    {
        if (_statusForm == null || _statusForm.IsDisposed)
        {
            _statusForm = new StatusForm();
        }

        _statusForm.UpdateLeader(_leader);
        _statusForm.Show();
        _statusForm.BringToFront();
    }

    private void ShowDiagnostics()
    {
        if (_diagnosticsForm == null || _diagnosticsForm.IsDisposed)
        {
            _diagnosticsForm = new DiagnosticsForm(_leader);
            _diagnosticsForm.FormClosed += (_, _) => _diagnosticsForm = null;
        }
        else
        {
            _diagnosticsForm.UpdateSnapshot(_leader);
        }

        _diagnosticsForm.Show();
        _diagnosticsForm.BringToFront();
    }

    public void HandleActivateSignal()
    {
        _uiContext.Post(_ => ShowStatus(), null);
    }

    public void HandleShutdownSignal()
    {
        _uiContext.Post(_ => Exit(), null);
    }

    private void SignalTrayReadyWhenReady()
    {
        if (string.IsNullOrWhiteSpace(_postInstallId))
        {
            return;
        }

        _postInstallTimer = new System.Windows.Forms.Timer
        {
            Interval = 250
        };
        _postInstallTimer.Tick += (_, _) =>
        {
            _postInstallTimer?.Stop();
            _postInstallTimer?.Dispose();
            _postInstallTimer = null;
            InstallHandoff.SignalTrayReady(_postInstallId);
        };
        _postInstallTimer.Start();
    }

    private void ToggleStartLeaderOnLogin()
    {
        _config.StartLeaderOnLogin = _startLeaderOnLoginItem.Checked;
        _configStore.Save(_config);
    }

    private void Install()
    {
        var result = MessageBox.Show(
            "Install DadBoard for this PC? This requires admin approval.",
            "DadBoard",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question);

        if (result != DialogResult.OK)
        {
            return;
        }

        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath))
        {
            MessageBox.Show("Unable to start installer.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var startInfo = new ProcessStartInfo(exePath, "--install")
        {
            UseShellExecute = true
        };

        var proc = Process.Start(startInfo);
        if (proc == null)
        {
            MessageBox.Show("Failed to launch installer.", "DadBoard", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Exit();
    }

    private void ResetUpdateFailuresLocal()
    {
        var confirm = MessageBox.Show(
            "This clears the update circuit breaker and retry backoff for this PC. Continue?",
            "DadBoard",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        _agent.ResetUpdateFailuresLocal("tray");
        MessageBox.Show(
            "Update failures reset. You can retry update now.",
            "DadBoard",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void UpdateMenuState()
    {
        var leaderEnabled = _leader != null;
        _enableLeaderItem.Enabled = !leaderEnabled;
        _disableLeaderItem.Enabled = leaderEnabled;
        _openDashboardItem.Enabled = leaderEnabled;
        var installed = Installer.IsInstalled();
        _installItem.Text = installed ? "Reinstall (Admin)" : "Install (Admin)";
        _installItem.Enabled = true;
    }

    private async System.Threading.Tasks.Task RunSetupAsync()
    {
        var setupPath = DadBoardPaths.SetupExePath;
        var installDir = DadBoardPaths.InstallDir;
        try
        {
            Directory.CreateDirectory(installDir);
            if (!File.Exists(setupPath))
            {
                var result = MessageBox.Show(
                    $"DadBoardSetup.exe not found at:{Environment.NewLine}{setupPath}{Environment.NewLine}{Environment.NewLine}Download updater now?",
                    "DadBoard",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (result != DialogResult.Yes)
                {
                    return;
                }

                var ok = await DownloadSetupAsync(setupPath).ConfigureAwait(true);
                if (!ok)
                {
                    return;
                }
            }

            if (!TryLaunchSetup(setupPath, installDir, out var error))
            {
                MessageBox.Show(
                    $"Failed to launch updater: {error}",
                    "DadBoard",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            _updateLogger.Error($"RunSetup failed: {ex}");
            MessageBox.Show(
                $"Failed to run updater: {ex.Message}",
                "DadBoard",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private bool TryLaunchSetup(string setupPath, string installDir, out string error)
    {
        error = "";
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = setupPath,
                WorkingDirectory = installDir,
                UseShellExecute = true
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                error = "Process failed to start.";
                _updateLogger.Warn($"Setup start returned null for {setupPath}");
                return false;
            }

            _updateLogger.Info($"Launched setup: {setupPath}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _updateLogger.Error($"Setup start failed: {ex}");
            return false;
        }
    }

    private async System.Threading.Tasks.Task<bool> DownloadSetupAsync(string setupPath)
    {
        var (primaryUrl, fallbackUrl, localPath) = ResolveSetupDownloadSources();
        if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
        {
            try
            {
                File.Copy(localPath, setupPath, true);
                _updateLogger.Info($"Copied setup from {localPath}");
                return true;
            }
            catch (Exception ex)
            {
                _updateLogger.Warn($"Setup copy failed: {ex.Message}");
            }
        }

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        string? lastError = null;
        foreach (var url in new[] { primaryUrl, fallbackUrl })
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            try
            {
                _updateLogger.Info($"Downloading setup: {url}");
                var bytes = await client.GetByteArrayAsync(url).ConfigureAwait(true);
                await File.WriteAllBytesAsync(setupPath, bytes).ConfigureAwait(true);
                _updateLogger.Info($"Setup downloaded to {setupPath}");
                return true;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                _updateLogger.Warn($"Setup download failed from {url}: {ex.Message}");
            }
        }

        MessageBox.Show(
            $"Failed to download DadBoardSetup.exe.{Environment.NewLine}{lastError ?? "No update source configured."}",
            "DadBoard",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
        return false;
    }

    private (string PrimaryUrl, string FallbackUrl, string LocalPath) ResolveSetupDownloadSources()
    {
        var manifestUrl = "";
        if (_leader != null)
        {
            var snapshot = _leader.GetUpdateMirrorSnapshot();
            manifestUrl = snapshot.LocalHostUrl;
        }

        var config = UpdateConfigStore.Load();
        var fallbackManifest = UpdateConfigStore.ResolveManifestUrl(config);

        var primaryUrl = BuildSetupUrlFromManifest(manifestUrl);
        var fallbackUrl = BuildSetupUrlFromManifest(fallbackManifest);

        var localPath = "";
        var manifestPath = ResolveLocalManifestPath(manifestUrl);
        if (!string.IsNullOrWhiteSpace(manifestPath))
        {
            localPath = Path.Combine(Path.GetDirectoryName(manifestPath) ?? "", "DadBoardSetup.exe");
        }

        return (primaryUrl, fallbackUrl, localPath);
    }

    private static string BuildSetupUrlFromManifest(string manifestUrl)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            return "";
        }

        if (manifestUrl.EndsWith("latest.json", StringComparison.OrdinalIgnoreCase))
        {
            return manifestUrl.Substring(0, manifestUrl.Length - "latest.json".Length) + "DadBoardSetup.exe";
        }

        return "";
    }

    private static string ResolveLocalManifestPath(string manifestUrl)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            return "";
        }

        if (File.Exists(manifestUrl))
        {
            return manifestUrl;
        }

        if (Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            return uri.LocalPath;
        }

        return "";
    }

    private void Exit()
    {
        _tray.Visible = false;
        _leaderForm?.AllowClose();
        _leaderForm?.Close();
        _statusForm?.Close();
        _diagnosticsForm?.Close();
        _leader?.Dispose();
        Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _postInstallTimer?.Stop();
            _postInstallTimer?.Dispose();
            _tray.Dispose();
            _leader?.Dispose();
            _agent.Dispose();
        }
        base.Dispose(disposing);
    }
}
